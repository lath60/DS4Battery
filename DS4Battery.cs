// DUALSHOCK 4 / DualSense バッテリー残量表示（画面のすみに小さく最前面表示）
// コントローラー本体が送ってくる電池の値（11段階）をそのまま読んで表示する。
using System;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

static class Hid
{
    [StructLayout(LayoutKind.Sequential)]
    public struct SP_DEVICE_INTERFACE_DATA { public int cbSize; public Guid InterfaceClassGuid; public int Flags; public IntPtr Reserved; }

    [StructLayout(LayoutKind.Sequential)]
    public struct HIDD_ATTRIBUTES { public int Size; public ushort VendorID; public ushort ProductID; public ushort VersionNumber; }

    [StructLayout(LayoutKind.Sequential)]
    public struct HIDP_CAPS
    {
        public ushort Usage, UsagePage, InputReportByteLength, OutputReportByteLength, FeatureReportByteLength;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)] public ushort[] Reserved;
        public ushort NumberLinkCollectionNodes, NumberInputButtonCaps, NumberInputValueCaps, NumberInputDataIndices,
            NumberOutputButtonCaps, NumberOutputValueCaps, NumberOutputDataIndices,
            NumberFeatureButtonCaps, NumberFeatureValueCaps, NumberFeatureDataIndices;
    }

    [DllImport("hid.dll")] public static extern void HidD_GetHidGuid(out Guid g);
    [DllImport("hid.dll")] public static extern bool HidD_GetAttributes(SafeFileHandle h, ref HIDD_ATTRIBUTES a);
    [DllImport("hid.dll")] public static extern bool HidD_GetPreparsedData(SafeFileHandle h, out IntPtr p);
    [DllImport("hid.dll")] public static extern bool HidD_FreePreparsedData(IntPtr p);
    [DllImport("hid.dll")] public static extern int HidP_GetCaps(IntPtr p, ref HIDP_CAPS c);
    [DllImport("hid.dll")] public static extern bool HidD_GetFeature(SafeFileHandle h, byte[] buf, int len);

    [DllImport("setupapi.dll", CharSet = CharSet.Auto)] public static extern IntPtr SetupDiGetClassDevs(ref Guid g, IntPtr e, IntPtr hwnd, int flags);
    [DllImport("setupapi.dll")] public static extern bool SetupDiEnumDeviceInterfaces(IntPtr set, IntPtr info, ref Guid g, int idx, ref SP_DEVICE_INTERFACE_DATA d);
    [DllImport("setupapi.dll", CharSet = CharSet.Auto)] public static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr set, ref SP_DEVICE_INTERFACE_DATA d, IntPtr detail, int size, out int req, IntPtr info);
    [DllImport("setupapi.dll")] public static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    public static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr sec, uint disp, uint flags, IntPtr tmpl);
    [DllImport("kernel32.dll", SetLastError = true)] public static extern bool ReadFile(SafeFileHandle h, IntPtr buf, int len, IntPtr read, IntPtr ov);
    [DllImport("kernel32.dll", SetLastError = true)] public static extern bool GetOverlappedResult(SafeFileHandle h, IntPtr ov, out int read, bool wait);
    [DllImport("kernel32.dll")] public static extern bool CancelIo(SafeFileHandle h);
    [DllImport("kernel32.dll")] public static extern uint WaitForSingleObject(IntPtr h, uint ms);

    public static System.Collections.Generic.List<string> DevicePaths()
    {
        var list = new System.Collections.Generic.List<string>();
        Guid g; HidD_GetHidGuid(out g);
        IntPtr set = SetupDiGetClassDevs(ref g, IntPtr.Zero, IntPtr.Zero, 0x12); // PRESENT | DEVICEINTERFACE
        try
        {
            for (int i = 0; ; i++)
            {
                var d = new SP_DEVICE_INTERFACE_DATA(); d.cbSize = Marshal.SizeOf(d);
                if (!SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref g, i, ref d)) break;
                int req; SetupDiGetDeviceInterfaceDetail(set, ref d, IntPtr.Zero, 0, out req, IntPtr.Zero);
                IntPtr buf = Marshal.AllocHGlobal(req);
                try
                {
                    Marshal.WriteInt32(buf, IntPtr.Size == 8 ? 8 : 4 + Marshal.SystemDefaultCharSize);
                    if (SetupDiGetDeviceInterfaceDetail(set, ref d, buf, req, out req, IntPtr.Zero))
                        list.Add(Marshal.PtrToStringAuto(new IntPtr(buf.ToInt64() + 4)));
                }
                finally { Marshal.FreeHGlobal(buf); }
            }
        }
        finally { SetupDiDestroyDeviceInfoList(set); }
        return list;
    }
}

class Reader
{
    public const int Discharging = 0, Charging = 1, Full = 2, Error = 3;

    // 読み取り結果（UI 側から読む）
    public volatile bool Connected;
    public volatile bool Bluetooth;
    public volatile string Model = "";
    public volatile int Level = -1;        // 0〜10（10%きざみ）
    public volatile int State;             // Discharging / Charging / Full / Error
    public volatile int RawByte = -1;      // 本体から届いた電池の欄の値（そのまま）
    public long LastUpdateTicks;

    public void Run()
    {
        while (true)
        {
            try { Session(); } catch { }
            Connected = false; Level = -1; RawByte = -1;
            Thread.Sleep(3000);
        }
    }

    static string ModelName(ushort pid)
    {
        switch (pid)
        {
            case 0x05C4: case 0x09CC: return "DUALSHOCK 4";
            case 0x0BA0: return "DUALSHOCK 4（USB ワイヤレスアダプター）";
            case 0x0CE6: return "DualSense";
            case 0x0DF2: return "DualSense Edge";
            default: return null;
        }
    }

    void Session()
    {
        foreach (var path in Hid.DevicePaths())
        {
            var h = Hid.CreateFile(path, 0xC0000000, 3, IntPtr.Zero, 3, 0x40000000, IntPtr.Zero);
            if (h.IsInvalid) h = Hid.CreateFile(path, 0x80000000, 3, IntPtr.Zero, 3, 0x40000000, IntPtr.Zero);
            if (h.IsInvalid) continue;
            var a = new Hid.HIDD_ATTRIBUTES(); a.Size = Marshal.SizeOf(a);
            string model = Hid.HidD_GetAttributes(h, ref a) && a.VendorID == 0x054C ? ModelName(a.ProductID) : null;
            if (model == null) { h.Dispose(); continue; }
            Model = model;
            using (h) { ReadLoop(h, a.ProductID == 0x0CE6 || a.ProductID == 0x0DF2); }
            return;
        }
    }

    void ReadLoop(SafeFileHandle h, bool dualSense)
    {
        IntPtr pp; var caps = new Hid.HIDP_CAPS();
        if (!Hid.HidD_GetPreparsedData(h, out pp)) return;
        Hid.HidP_GetCaps(pp, ref caps); Hid.HidD_FreePreparsedData(pp);
        int len = caps.InputReportByteLength;
        if (len <= 0) return;
        // USB は 64 バイト。無線は DS4 が 547 バイト、DualSense が 78 バイト。
        bool bt = len > 64;
        Bluetooth = bt;
        Connected = true;

        IntPtr buf = Marshal.AllocHGlobal(len);
        var evObj = new ManualResetEvent(false);
        IntPtr ev = evObj.SafeWaitHandle.DangerousGetHandle();
        IntPtr ov = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(NativeOverlapped)));
        byte[] data = new byte[len];
        int shortReports = 0;
        try
        {
            while (true)
            {
                var o = new NativeOverlapped(); o.EventHandle = ev;
                Marshal.StructureToPtr(o, ov, false);
                evObj.Reset();
                int read;
                if (!Hid.ReadFile(h, buf, len, IntPtr.Zero, ov))
                {
                    int err = Marshal.GetLastWin32Error();
                    if (err != 997) return; // ERROR_IO_PENDING 以外は切断
                    if (Hid.WaitForSingleObject(ev, 3000) != 0)
                    {
                        Hid.CancelIo(h);
                        Hid.GetOverlappedResult(h, ov, out read, true);
                        // 3秒なにも来ない＝電源オフ・切断の可能性。開き直す。
                        return;
                    }
                }
                if (!Hid.GetOverlappedResult(h, ov, out read, true) || read <= 0) return;
                Marshal.Copy(buf, data, 0, Math.Min(read, len));

                // 電池の欄の位置
                //   DS4      : USB 0x01 の 30 番目 / 無線 0x11 の 32 番目
                //   DualSense: USB 0x01 の 53 番目 / 無線 0x31 の 54 番目
                int idx = -1;
                if (!bt && data[0] == 0x01) idx = dualSense ? 53 : 30;
                else if (bt && !dualSense && data[0] == 0x11) idx = 32;
                else if (bt && dualSense && data[0] == 0x31) idx = 54;
                else if (bt && data[0] == 0x01)
                {
                    // 無線の簡略報告には電池の値が入っていない。
                    // 校正値の要求を送ると完全な報告に切り替わる（Steam などと同じ操作）。
                    if (++shortReports % 200 == 1) RequestFullReports(h, caps.FeatureReportByteLength);
                }
                if (idx >= 0 && idx < read)
                {
                    int b = data[idx];
                    RawByte = b;
                    if (dualSense) ParseDualSense(b); else ParseDs4(b);
                    Interlocked.Exchange(ref LastUpdateTicks, DateTime.UtcNow.Ticks);
                }
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buf); Marshal.FreeHGlobal(ov); evObj.Dispose();
        }
    }

    // DS4：下位4ビット＝残量、5ビット目＝ケーブル。ケーブルありで 11＝満充電、14・15＝エラー。
    void ParseDs4(int b)
    {
        int v = b & 0x0F; bool cable = (b & 0x10) != 0;
        if (!cable) { State = Discharging; Level = Math.Min(v, 10); }
        else if (v <= 10) { State = Charging; Level = v; }
        else if (v == 11) { State = Full; Level = 10; }
        else { State = Error; Level = 0; }
    }

    // DualSense：下位4ビット＝残量、上位4ビット＝0 放電中・1 充電中・2 満充電・それ以外はエラー。
    void ParseDualSense(int b)
    {
        int v = Math.Min(b & 0x0F, 10), st = b >> 4;
        if (st == 0) { State = Discharging; Level = v; }
        else if (st == 1) { State = Charging; Level = v; }
        else if (st == 2) { State = Full; Level = 10; }
        else { State = Error; Level = 0; }
    }

    static void RequestFullReports(SafeFileHandle h, int featLen)
    {
        if (featLen <= 0) featLen = 64;
        foreach (byte id in new byte[] { 0x05, 0x02 })
        {
            var f = new byte[Math.Max(featLen, 41)]; f[0] = id;
            if (Hid.HidD_GetFeature(h, f, f.Length)) return;
        }
    }
}

class BatteryForm : Form
{
    readonly Reader reader;
    Point dragStart; bool dragging;
    readonly string settingsPath;
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string RunName = "DS4Battery";
    ToolStripMenuItem startupItem;

    public BatteryForm(Reader r)
    {
        reader = r;
        Text = "コントローラーのバッテリー";
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        Size = new Size(150, 30);
        Font = new Font("Yu Gothic UI", 10f, FontStyle.Bold);

        settingsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DS4Battery", "position.txt");
        alphaPath = Path.Combine(Path.GetDirectoryName(settingsPath), "background.txt");
        Location = LoadPosition();
        try { bgAlpha = Math.Max(1, Math.Min(255, int.Parse(File.ReadAllText(alphaPath).Trim()))); } catch { }
        stylePath = Path.Combine(Path.GetDirectoryName(settingsPath), "style.txt");
        try { batteryStyle = Math.Max(0, Math.Min(2, int.Parse(File.ReadAllText(BatteryPath).Trim()))); } catch { }
        try
        {
            var p = File.ReadAllText(stylePath).Trim().Split(new[] { ',' }, 4);
            if (p.Length >= 4) { fontStyle = (FontStyle)int.Parse(p[2]); fontFamily = p[3]; }
            SetFont(float.Parse(p[0], System.Globalization.CultureInfo.InvariantCulture));
            textColor = p[1] == "auto" ? Color.Empty : Color.FromArgb(int.Parse(p[1], System.Globalization.NumberStyles.HexNumber) | unchecked((int)0xFF000000));
        }
        catch { }

        var menu = new ContextMenuStrip();
        menu.Items.Add("右下に戻す", null, (s, e) => { Location = DefaultPosition(); SavePosition(); });
        var bgMenu = new ToolStripMenuItem("背景の濃さ");
        foreach (var opt in new[] { Tuple.Create("なし（透明）", 1) /* 0 にするとマウスが素通りしてつかめなくなるので 1 */, Tuple.Create("ほぼ透明", 20), Tuple.Create("薄い", 70), Tuple.Create("普通", 130), Tuple.Create("濃い", 200), Tuple.Create("不透明", 255) })
        {
            int a = opt.Item2;
            var it = new ToolStripMenuItem(opt.Item1, null, (s, e) => { bgAlpha = a; SaveAlpha(); Render(); });
            it.Tag = a;
            bgMenu.DropDownItems.Add(it);
        }
        bgMenu.DropDownOpening += (s, e) => { foreach (ToolStripMenuItem it in bgMenu.DropDownItems) it.Checked = (int)it.Tag == bgAlpha; };
        menu.Items.Add(bgMenu);

        var sizeMenu = new ToolStripMenuItem("文字の大きさ");
        foreach (var opt in new[] { Tuple.Create("小", 8f), Tuple.Create("普通", 10f), Tuple.Create("大", 12f), Tuple.Create("特大", 15f), Tuple.Create("巨大", 20f) })
        {
            float pt = opt.Item2;
            var it = new ToolStripMenuItem(opt.Item1, null, (s, e) => { SetFont(pt); SaveStyle(); Render(); });
            it.Tag = pt;
            sizeMenu.DropDownItems.Add(it);
        }
        sizeMenu.DropDownOpening += (s, e) => { foreach (ToolStripMenuItem it in sizeMenu.DropDownItems) it.Checked = Math.Abs((float)it.Tag - fontPt) < 0.1f; };
        menu.Items.Add(sizeMenu);

        var fontMenu = new ToolStripMenuItem("フォント");
        fontMenu.DropDownItems.Add("PC に入っているフォントから選ぶ…", null, (s, e) =>
        {
            using (var dlg = new FontDialog { Font = Font, ShowEffects = false, FontMustExist = true, AllowVerticalFonts = false, MinSize = 6, MaxSize = 40 })
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                fontFamily = dlg.Font.FontFamily.Name;
                fontStyle = dlg.Font.Style & (FontStyle.Bold | FontStyle.Italic);
                SetFont(dlg.Font.SizeInPoints); SaveStyle(); Render();
            }
        });
        fontMenu.DropDownItems.Add("最初のフォントに戻す", null, (s, e) => { fontFamily = DefaultFamily; fontStyle = FontStyle.Bold; SetFont(fontPt); SaveStyle(); Render(); });
        menu.Items.Add(fontMenu);

        var colorMenu = new ToolStripMenuItem("文字の色");
        foreach (var opt in new[] {
            Tuple.Create("白", Color.White), Tuple.Create("薄い灰色", Color.FromArgb(190, 190, 190)),
            Tuple.Create("黄色", Color.FromArgb(255, 220, 90)), Tuple.Create("水色", Color.FromArgb(120, 210, 255)),
            Tuple.Create("ピンク", Color.FromArgb(255, 150, 200)), Tuple.Create("残量の色（緑・黄・赤）", Color.Empty) })
        {
            Color c = opt.Item2;
            var it = new ToolStripMenuItem(opt.Item1, null, (s, e) => { textColor = c; SaveStyle(); Render(); });
            it.Tag = c;
            colorMenu.DropDownItems.Add(it);
        }
        colorMenu.DropDownItems.Add(new ToolStripSeparator());
        colorMenu.DropDownItems.Add("ほかの色を選ぶ…", null, (s, e) =>
        {
            using (var dlg = new ColorDialog { Color = textColor.IsEmpty ? Color.White : textColor, FullOpen = true })
                if (dlg.ShowDialog(this) == DialogResult.OK) { textColor = Color.FromArgb(dlg.Color.R, dlg.Color.G, dlg.Color.B); SaveStyle(); Render(); }
        });
        colorMenu.DropDownOpening += (s, e) =>
        {
            foreach (var x in colorMenu.DropDownItems)
            {
                var it = x as ToolStripMenuItem;
                if (it != null && it.Tag is Color) it.Checked = ((Color)it.Tag).ToArgb() == textColor.ToArgb() && ((Color)it.Tag).IsEmpty == textColor.IsEmpty;
            }
        };
        menu.Items.Add(colorMenu);

        var batMenu = new ToolStripMenuItem("電池の表示");
        foreach (var opt in new[] { Tuple.Create("10マス", 0), Tuple.Create("コンパクト", 1), Tuple.Create("電池の絵なし（文字だけ）", 2) })
        {
            int v = opt.Item2;
            var it = new ToolStripMenuItem(opt.Item1, null, (s, e) => { batteryStyle = v; SaveBattery(); Render(); });
            it.Tag = v;
            batMenu.DropDownItems.Add(it);
        }
        batMenu.DropDownOpening += (s, e) => { foreach (ToolStripMenuItem it in batMenu.DropDownItems) it.Checked = (int)it.Tag == batteryStyle; };
        menu.Items.Add(batMenu);
        startupItem = new ToolStripMenuItem("Windows の起動時に自動で開く", null, (s, e) => ToggleStartup());
        menu.Items.Add(startupItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("終了", null, (s, e) => Close());
        menu.Opening += (s, e) => startupItem.Checked = IsStartup();
        ContextMenuStrip = menu;

        var tip = new ToolTip();
        var timer = new System.Windows.Forms.Timer { Interval = 1000 };
        timer.Tick += (s, e) =>
        {
            Render();
            TopMost = true; // ほかのウィンドウに隠れたら前に出し直す
            tip.SetToolTip(this, TooltipText());
        };
        timer.Start();
    }

    protected override CreateParams CreateParams
    {
        get { var cp = base.CreateParams; cp.ExStyle |= 0x80 /* TOOLWINDOW: Alt+Tab に出さない */ | 0x80000 /* LAYERED: 背景だけ半透明 */; return cp; }
    }

    int bgAlpha = 130;
    int batteryStyle = 0;               // 0 いまの（10マス）／1 コンパクト／2 電池の絵なし

    string BatteryPath { get { return Path.Combine(Path.GetDirectoryName(settingsPath), "battery.txt"); } }

    void SaveBattery()
    {
        try { Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)); File.WriteAllText(BatteryPath, batteryStyle.ToString()); } catch { }
    }
    float fontPt = 10f;
    const string DefaultFamily = "Yu Gothic UI";
    string fontFamily = DefaultFamily;
    FontStyle fontStyle = FontStyle.Bold;
    Color textColor = Color.White;      // Color.Empty＝残量の色に合わせる
    readonly string stylePath;

    void SetFont(float pt)
    {
        fontPt = Math.Max(6f, Math.Min(40f, pt));
        try { Font = new Font(fontFamily, fontPt, fontStyle); }
        catch { fontFamily = DefaultFamily; fontStyle = FontStyle.Bold; Font = new Font(fontFamily, fontPt, fontStyle); }
    }

    void SaveStyle()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(stylePath));
            string c = textColor.IsEmpty ? "auto" : (textColor.ToArgb() & 0xFFFFFF).ToString("X6");
            File.WriteAllText(stylePath, fontPt.ToString(System.Globalization.CultureInfo.InvariantCulture) + "," + c + "," + (int)fontStyle + "," + fontFamily);
        }
        catch { }
    }
    readonly string alphaPath;

    void SaveAlpha()
    {
        try { Directory.CreateDirectory(Path.GetDirectoryName(alphaPath)); File.WriteAllText(alphaPath, bgAlpha.ToString()); } catch { }
    }

    protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); Render(); }

    [StructLayout(LayoutKind.Sequential)] struct PT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] struct SZ { public int W, H; }
    [StructLayout(LayoutKind.Sequential, Pack = 1)] struct BLEND { public byte Op, Flags, Alpha, Format; }
    [DllImport("user32.dll")] static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst, IntPtr pptDst, ref SZ size, IntPtr hdcSrc, ref PT ptSrc, int key, ref BLEND blend, int flags);
    [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr h);
    [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr h, IntPtr dc);
    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr obj);

    Point DefaultPosition()
    {
        var wa = Screen.PrimaryScreen.WorkingArea;
        return new Point(wa.Right - Width - 12, wa.Bottom - Height - 12);
    }

    Point LoadPosition()
    {
        try
        {
            var p = File.ReadAllText(settingsPath).Split(',');
            var pt = new Point(int.Parse(p[0]), int.Parse(p[1]));
            // タスクバーの上に置いた場合もあるので、画面全体で確かめる（一部でも画面に入っていればよい）
            var rect = new Rectangle(pt, Size);
            foreach (var s in Screen.AllScreens)
                if (s.Bounds.IntersectsWith(rect)) return pt;
        }
        catch { }
        return DefaultPosition();
    }

    void SavePosition()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(settingsPath));
            File.WriteAllText(settingsPath, Location.X + "," + Location.Y);
        }
        catch { }
    }

    bool IsStartup()
    {
        using (var k = Registry.CurrentUser.OpenSubKey(RunKey)) return k != null && k.GetValue(RunName) != null;
    }

    void ToggleStartup()
    {
        using (var k = Registry.CurrentUser.CreateSubKey(RunKey))
        {
            if (k.GetValue(RunName) != null) k.DeleteValue(RunName);
            else k.SetValue(RunName, "\"" + Application.ExecutablePath + "\"");
        }
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left) { dragging = true; dragStart = e.Location; }
    }
    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (dragging) Location = new Point(Location.X + e.X - dragStart.X, Location.Y + e.Y - dragStart.Y);
    }
    protected override void OnMouseUp(MouseEventArgs e)
    {
        if (dragging) { dragging = false; SavePosition(); }
    }

    // 値 → 表示（Linux の公式ドライバーと同じ解釈：値 n は n×10〜n×10+9%）
    void Describe(out string text, out int segments, out Color color, out bool bolt)
    {
        bolt = false;
        int lv = reader.Level, st = reader.State;
        bool fresh = DateTime.UtcNow.Ticks - Interlocked.Read(ref reader.LastUpdateTicks) < TimeSpan.FromSeconds(10).Ticks;
        if (!reader.Connected || lv < 0 || !fresh)
        {
            text = reader.Connected ? "読み取り中…" : "未接続"; segments = 0; color = Color.Gray; return;
        }
        if (st == Reader.Full) { text = "FULL"; segments = 10; color = Color.FromArgb(80, 200, 120); return; }
        if (st == Reader.Error) { text = "充電エラー"; segments = 0; color = Color.OrangeRed; return; }
        segments = lv;
        if (st == Reader.Charging)
        {
            // 充電中の 10 は「まだ満充電ではない」＝実際は 90%台。満充電になると FULL に変わる。
            text = lv >= 9 ? "90〜99%" : lv * 10 + "〜" + (lv * 10 + 9) + "%";
            bolt = true;
            color = Color.FromArgb(90, 170, 255);
            return;
        }
        text = lv >= 10 ? "100%" : lv * 10 + "〜" + (lv * 10 + 9) + "%";
        color = lv <= 1 ? Color.FromArgb(255, 70, 70) : lv <= 3 ? Color.FromArgb(255, 190, 60) : Color.FromArgb(80, 200, 120);
    }

    string TooltipText()
    {
        if (!reader.Connected) return "コントローラー未接続";
        return reader.Model + "（" + (reader.Bluetooth ? "Bluetooth" : "USB") + "）";
    }

    // 背景は半透明・電池の絵と文字は不透明で描く（ピクセルごとの透明度）
    void Render()
    {
        if (!IsHandleCreated) return;
        string text; int seg; Color col; bool bolt;
        Describe(out text, out seg, out col, out bolt);

        float k = fontPt / 10f;                 // 文字の大きさに合わせて全体を拡大縮小
        // 電池の絵の幅：いまの（10マス）／コンパクト（小さい電池）／なし（文字だけ）
        float bw = batteryStyle == 0 ? 44 * k : batteryStyle == 1 ? 22 * k : 0;
        float bh = batteryStyle == 0 ? 14 * k : 11 * k;
        int bx = batteryStyle == 2 ? (int)(10 * k) : (int)(8 * k + bw + 9 * k);
        int tx = bolt ? bx + (int)(11 * k) : bx;   // 充電中は文字の前に稲妻のマーク
        int w, h = (int)Math.Round(30 * k);
        using (var tmp = new Bitmap(1, 1))
        using (var tg = Graphics.FromImage(tmp))
            w = tx + (int)tg.MeasureString(text, Font).Width + (int)(8 * k);
        if (Width != w || Height != h) Size = new Size(w, h);

        using (var bmp = new Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppPArgb))
        {
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                g.Clear(Color.Transparent);

                // 角の丸い背景
                using (var path = new System.Drawing.Drawing2D.GraphicsPath())
                {
                    int r = (int)(8 * k);
                    path.AddArc(0, 0, r, r, 180, 90); path.AddArc(w - r - 1, 0, r, r, 270, 90);
                    path.AddArc(w - r - 1, h - r - 1, r, r, 0, 90); path.AddArc(0, h - r - 1, r, r, 90, 90);
                    path.CloseFigure();
                    using (var bg = new SolidBrush(Color.FromArgb(bgAlpha, 20, 20, 20))) g.FillPath(bg, path);
                    if (col.R == 255 && col.G == 70) // 残りわずか：赤い枠
                        using (var pen = new Pen(col, 2)) g.DrawPath(pen, path);
                }

                if (batteryStyle != 2)
                {
                    var body = new RectangleF(8 * k, (h - bh) / 2f, bw, bh);
                    using (var pen = new Pen(Color.FromArgb(220, 220, 220), 1.5f * k)) g.DrawRectangle(pen, body.X, body.Y, body.Width, body.Height);
                    using (var nub = new SolidBrush(Color.FromArgb(220, 220, 220)))
                        g.FillRectangle(nub, body.Right + 1 * k, body.Y + bh * 0.3f, 2.5f * k, bh * 0.4f);
                    using (var br = new SolidBrush(col))
                    {
                        if (batteryStyle == 0)
                        {
                            // 10マス
                            float cell = (body.Width - 4 * k) / 10f;
                            for (int i = 0; i < seg; i++) g.FillRectangle(br, body.X + 2 * k + i * cell, body.Y + 2 * k, cell - 1 * k, body.Height - 4 * k);
                        }
                        else if (seg > 0)
                        {
                            // コンパクト：残量の分だけ1本で塗る
                            float inner = body.Width - 4 * k;
                            g.FillRectangle(br, body.X + 2 * k, body.Y + 2 * k, Math.Max(1.5f * k, inner * seg / 10f), body.Height - 4 * k);
                        }
                    }
                }

                if (bolt)
                {
                    // 稲妻のマーク（フォントに頼らず図形で描く）
                    float cx = bx, cy = h / 2f, u = k;
                    var pts = new[] {
                        new PointF(cx + 5.5f * u, cy - 7 * u), new PointF(cx + 0.5f * u, cy + 1 * u),
                        new PointF(cx + 4f * u, cy + 1 * u),   new PointF(cx + 2.5f * u, cy + 7 * u),
                        new PointF(cx + 8.5f * u, cy - 1.5f * u), new PointF(cx + 5f * u, cy - 1.5f * u),
                        new PointF(cx + 7f * u, cy - 7 * u) };
                    using (var sp = new Pen(Color.FromArgb(200, 0, 0, 0), 1.5f * k)) g.DrawPolygon(sp, pts);
                    using (var yb = new SolidBrush(Color.FromArgb(255, 210, 60))) g.FillPolygon(yb, pts);
                }

                // 文字（背景が薄くても読めるよう影を付ける）
                var sz = g.MeasureString(text, Font);
                float ty = (h - sz.Height) / 2f + 1;
                Color fc = col == Color.Gray ? Color.Silver : textColor.IsEmpty ? col : textColor;
                using (var sh = new SolidBrush(Color.FromArgb(200, 0, 0, 0))) g.DrawString(text, Font, sh, tx + 1, ty + 1);
                using (var br = new SolidBrush(fc)) g.DrawString(text, Font, br, tx, ty);
            }

            IntPtr screen = GetDC(IntPtr.Zero), mem = CreateCompatibleDC(screen);
            IntPtr hbmp = bmp.GetHbitmap(Color.FromArgb(0)), old = SelectObject(mem, hbmp);
            try
            {
                var size = new SZ { W = w, H = h }; var src = new PT();
                var blend = new BLEND { Op = 0, Flags = 0, Alpha = 255, Format = 1 /* AC_SRC_ALPHA */ };
                UpdateLayeredWindow(Handle, screen, IntPtr.Zero, ref size, mem, ref src, 0, ref blend, 2 /* ULW_ALPHA */);
            }
            finally { SelectObject(mem, old); DeleteObject(hbmp); DeleteDC(mem); ReleaseDC(IntPtr.Zero, screen); }
        }
    }

    [STAThread]
    static void Main()
    {
        bool created;
        using (var m = new Mutex(true, "DS4Battery_single_instance", out created))
        {
            if (!created) return; // 2つ目は開かない
            Application.EnableVisualStyles();
            var r = new Reader();
            var t = new Thread(r.Run) { IsBackground = true };
            t.Start();
            Application.Run(new BatteryForm(r));
        }
    }
}
