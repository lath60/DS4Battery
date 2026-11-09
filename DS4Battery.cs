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
    [DllImport("kernel32.dll", SetLastError = true)] public static extern bool WriteFile(SafeFileHandle h, IntPtr buf, int len, IntPtr written, IntPtr ov);
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
    public const int Discharging = 0, Charging = 1, Full = 2, Error = 3, PluggedIdle = 4;

    // 読み取り結果（UI 側から読む）
    public volatile bool Connected;
    public volatile bool Bluetooth;
    public volatile string Model = "";
    public volatile int Level = -1;        // 0〜10（10%きざみ）
    public volatile int State;             // Discharging / Charging / Full / Error
    public volatile int RawByte = -1;      // 本体から届いた電池の欄の値（そのまま）
    // 本体が数段階でしか残量を返さないコントローラー（Switch Pro・Xbox、どちらも試験的）
    public volatile bool Coarse;           // その種類を読んでいる
    public volatile int CoarseLevel = -1;  // 段階（0 がいちばん少ない）。CoarseWired＝有線で電池なし
    public volatile string[] CoarseWords;  // 段階ごとの言葉
    public volatile string Link = "";      // 接続の方法（Bluetooth / USB / 空＝分からない）
    public const int CoarseWired = 100;
    public long LastUpdateTicks;

    public void Run()
    {
        while (true)
        {
            try { Session(); } catch { }
            Connected = false; Level = -1; RawByte = -1; Coarse = false; CoarseLevel = -1; Link = "";
            // 外していたときの残量は覚えておく（ケーブルをつなぐと無線から USB へつなぎ直すことがあるため）
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
            if (!Hid.HidD_GetAttributes(h, ref a)) { h.Dispose(); continue; }
            if (a.VendorID == 0x057E && a.ProductID == 0x2009)
            {
                // Switch Pro コントローラー（試験的）。Sony とは別の読み方。
                Model = "Switch Pro コントローラー（試験的）";
                using (h) { SwitchLoop(h); }
                return;
            }
            string model = a.VendorID == 0x054C ? ModelName(a.ProductID) : null;
            if (model == null) { h.Dispose(); continue; }
            Model = model;
            using (h) { ReadLoop(h, a.ProductID == 0x0CE6 || a.ProductID == 0x0DF2); }
            return;
        }
        // Sony・Switch が見つからなければ Xbox（XInput）を探す
        for (int i = 0; i < 4; i++)
            if (XInputConnected(i)) { XboxLoop(i); return; }
    }

    // ===== Xbox コントローラー（試験的・実機では未確認）=====
    // Windows の XInput に残量を聞くだけ（コントローラーには何も送らない）。返るのは4段階と種類だけ。
    [StructLayout(LayoutKind.Sequential)] struct XInputBattery { public byte Type, Level; }
    [DllImport("xinput1_4.dll")] static extern int XInputGetState(int idx, byte[] state);
    [DllImport("xinput1_4.dll")] static extern int XInputGetBatteryInformation(int idx, byte devType, out XInputBattery info);

    static bool XInputConnected(int i)
    {
        try { return XInputGetState(i, new byte[16]) == 0; } catch { return false; } // xinput1_4 がない古い Windows
    }

    void XboxLoop(int idx)
    {
        Model = "Xbox コントローラー（試験的）"; Link = ""; Bluetooth = false;
        CoarseWords = new[] { "残りわずか", "少ない", "半分", "満タン" }; CoarseLevel = -1; Coarse = true; Level = -1; Connected = true;
        while (XInputConnected(idx))
        {
            XInputBattery b;
            if (XInputGetBatteryInformation(idx, 0 /* ゲームパッド */, out b) == 0)
            {
                if (b.Type == 0) return;                                  // つながっていない
                if (b.Type == 1) { CoarseLevel = CoarseWired; Level = 10; } // 有線（電池の情報なし）
                else if (b.Type == 0xFF || b.Level > 3) { CoarseLevel = -1; Level = -1; } // 分からない
                else { CoarseLevel = b.Level; Level = new[] { 1, 3, 6, 10 }[b.Level]; }
                State = Discharging; RawByte = b.Type << 4 | b.Level;
                Interlocked.Exchange(ref LastUpdateTicks, DateTime.UtcNow.Ticks);
            }
            Thread.Sleep(2000);
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

    // ===== Switch Pro コントローラー（試験的・実機では未確認）=====
    // 通信の仕方は Linux の公式ドライバー hid-nintendo と有志の調査（dekuNukem/Nintendo_Switch_Reverse_Engineering）による。
    // 電池の値は入力報告 0x21・0x30・0x31 の 2 バイト目：上位3ビット＝残量（0 わずか〜4 満タン）、0x10＝充電中。
    // Bluetooth の普段の報告（0x3F）には電池の値がないので、2秒ごとに「機器の情報を返して」（サブコマンド 0x02、
    // 何も変えない問い合わせ）を送り、その返事（0x21）から読む。5回続けて返事がなければ送るのをやめ、聞くだけにする。
    // USB では何も送らない（ほかのソフトが起こしたあとに届く報告だけを読む）。
    void SwitchLoop(SafeFileHandle h)
    {
        IntPtr pp; var caps = new Hid.HIDP_CAPS();
        if (!Hid.HidD_GetPreparsedData(h, out pp)) return;
        Hid.HidP_GetCaps(pp, ref caps); Hid.HidD_FreePreparsedData(pp);
        int len = caps.InputReportByteLength, outLen = caps.OutputReportByteLength;
        if (len <= 0) return;
        bool bt = len > 64;
        Bluetooth = bt; Link = bt ? "Bluetooth" : "USB";
        CoarseWords = new[] { "残りわずか", "少ない", "半分", "多い", "満タン" }; CoarseLevel = -1; Coarse = true; Level = -1; Connected = true;

        IntPtr buf = Marshal.AllocHGlobal(len), ov = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(NativeOverlapped)));
        IntPtr obuf = outLen > 0 ? Marshal.AllocHGlobal(outLen) : IntPtr.Zero;
        IntPtr wov = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(NativeOverlapped)));
        var evObj = new ManualResetEvent(false); var wevObj = new ManualResetEvent(false);
        IntPtr ev = evObj.SafeWaitHandle.DangerousGetHandle(), wev = wevObj.SafeWaitHandle.DangerousGetHandle();
        byte[] data = new byte[len];
        int counter = 0, unanswered = 0;
        bool pinging = bt && outLen >= 12;
        long lastPing = 0, lastInput = DateTime.UtcNow.Ticks;
        bool reading = false;
        try
        {
            while (true)
            {
                long now = DateTime.UtcNow.Ticks;
                if (pinging && now - lastPing >= TimeSpan.FromSeconds(2).Ticks)
                {
                    lastPing = now;
                    if (unanswered >= 5) pinging = false; // 返事がないので送るのをやめる
                    else
                    {
                        unanswered++;
                        var pkt = new byte[outLen];
                        pkt[0] = 0x01; pkt[1] = (byte)(counter++ & 0x0F);
                        // 振動なし（中立の値）
                        pkt[2] = 0x00; pkt[3] = 0x01; pkt[4] = 0x40; pkt[5] = 0x40;
                        pkt[6] = 0x00; pkt[7] = 0x01; pkt[8] = 0x40; pkt[9] = 0x40;
                        pkt[10] = 0x02; // サブコマンド：機器の情報を返して
                        Marshal.Copy(pkt, 0, obuf, outLen);
                        var wo = new NativeOverlapped(); wo.EventHandle = wev;
                        Marshal.StructureToPtr(wo, wov, false);
                        wevObj.Reset();
                        int written;
                        if (!Hid.WriteFile(h, obuf, outLen, IntPtr.Zero, wov))
                        {
                            int err = Marshal.GetLastWin32Error();
                            if (err != 997) return; // 送れない＝切断
                            if (Hid.WaitForSingleObject(wev, 1000) != 0) { Hid.CancelIo(h); Hid.GetOverlappedResult(h, wov, out written, true); return; }
                        }
                        if (!Hid.GetOverlappedResult(h, wov, out written, true)) return;
                    }
                }

                if (!reading)
                {
                    var o = new NativeOverlapped(); o.EventHandle = ev;
                    Marshal.StructureToPtr(o, ov, false);
                    evObj.Reset();
                    if (!Hid.ReadFile(h, buf, len, IntPtr.Zero, ov))
                    {
                        int err = Marshal.GetLastWin32Error();
                        if (err != 997) return;
                    }
                    reading = true;
                }
                // 普段の報告はボタンを動かしたときしか来ないので、待ち切れなくても切断とはみなさない
                if (Hid.WaitForSingleObject(ev, 500) != 0)
                {
                    if (DateTime.UtcNow.Ticks - lastInput > TimeSpan.FromSeconds(30).Ticks && !pinging) { Hid.CancelIo(h); int r0; Hid.GetOverlappedResult(h, ov, out r0, true); return; }
                    continue;
                }
                reading = false;
                int read;
                if (!Hid.GetOverlappedResult(h, ov, out read, true) || read <= 0) return;
                lastInput = DateTime.UtcNow.Ticks;
                Marshal.Copy(buf, data, 0, Math.Min(read, len));
                int id = data[0];
                if ((id == 0x21 || id == 0x30 || id == 0x31) && read > 2)
                {
                    int b = data[2];
                    RawByte = b;
                    int lv = Math.Min(b >> 5, 4);
                    CoarseLevel = lv;
                    Level = new[] { 1, 3, 5, 7, 10 }[lv]; // 電池の絵のマス
                    State = (b & 0x10) != 0 ? Charging : Discharging;
                    unanswered = 0;
                    // ほかのソフト（Steam など）が電池の値の入った報告を流しているなら、こちらからは送らない
                    if (id != 0x21) pinging = false;
                    Interlocked.Exchange(ref LastUpdateTicks, DateTime.UtcNow.Ticks);
                }
            }
        }
        finally
        {
            if (reading) { Hid.CancelIo(h); int r1; Hid.GetOverlappedResult(h, ov, out r1, true); }
            Marshal.FreeHGlobal(buf); Marshal.FreeHGlobal(ov); Marshal.FreeHGlobal(wov);
            if (obuf != IntPtr.Zero) Marshal.FreeHGlobal(obuf);
            evObj.Dispose(); wevObj.Dispose();
        }
    }

    // DS4：下位4ビット＝残量、5ビット目＝ケーブル。ケーブルありで 11＝満充電、14・15＝エラー。
    // ケーブルなしの値は 0〜9 で、ケーブルありの 0〜10 より範囲が1つ狭い。
    // Linux の hid-sony と同じく、ケーブルなしは1段上に読む（9＝100%、8＝90%台）。
    void ParseDs4(int b)
    {
        int v = b & 0x0F; bool cable = (b & 0x10) != 0;
        if (!cable) OnBattery(Math.Min(v + 1, 10));
        else if (v <= 10) OnCharging(v);
        else if (v == 11) OnFull();
        else { State = Error; Level = 0; }
    }

    // DualSense：下位4ビット＝残量、上位4ビット＝0 放電中・1 充電中・2 満充電・それ以外はエラー。
    void ParseDualSense(int b)
    {
        int v = Math.Min(b & 0x0F, 10), st = b >> 4;
        if (st == 0) OnBattery(v);
        else if (st == 1) OnCharging(v);
        else if (st == 2) OnFull();
        else { State = Error; Level = 0; }
    }

    // 本体は残量が多いと充電を始めずに、つないだ直後から「満充電」と送ってくる。
    // それを FULL と出すと紛らわしいので、充電が進んでから満充電になったときだけ FULL にする。
    // そうでないときは「つないでいるが充電していない」として、外していたときの残量を出す。
    static readonly long MinChargeTicks = TimeSpan.FromSeconds(60).Ticks;
    int lastBatteryLevel = -1;   // ケーブルを外していたときの最後の残量
    long lastBatteryTicks;       // その時刻
    long chargeSince;            // 今回つないでから充電中を最初に見た時刻（0＝まだ）
    bool trustFull;              // 今回つないでから、充電が進んで満充電になった
    bool fullDecided;            // 今回の満充電をどちらで出すか決めた

    void OnBattery(int v)
    {
        State = Discharging; Level = v;
        lastBatteryLevel = v; lastBatteryTicks = DateTime.UtcNow.Ticks;
        chargeSince = 0; trustFull = false; fullDecided = false;
    }

    void OnCharging(int v)
    {
        State = Charging; Level = v;
        if (chargeSince == 0) chargeSince = DateTime.UtcNow.Ticks;
        fullDecided = false; // 充電が始まったら、次の満充電で決め直す
    }

    void OnFull()
    {
        // 満充電を最初に受け取ったときに一度だけ決め、ケーブルを外すまで変えない
        if (!fullDecided)
        {
            fullDecided = true;
            long now = DateTime.UtcNow.Ticks;
            // 外していたときの残量が分からない・古い（起動時からつないであった、10分以上前）ときは本体の判定のまま
            trustFull = lastBatteryLevel < 0 || now - lastBatteryTicks > TimeSpan.FromMinutes(10).Ticks
                || (chargeSince != 0 && now - chargeSince >= MinChargeTicks);
        }
        if (trustFull) { State = Full; Level = 10; }
        else { State = PluggedIdle; Level = lastBatteryLevel; }
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
    ToolTip tip;
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
            using (var dlg = new FontDialog { Font = Font, ShowEffects = false, FontMustExist = true, AllowVerticalFonts = false, AllowVectorFonts = false, MinSize = 6, MaxSize = 40 })
            {
                // TrueType 以外（.otf の一部や昔の画面用フォント）を選ぶと、Windows の部品が例外を出す。
                // そのときは説明を出して、もう一度選んでもらう。
                while (true)
                {
                    try
                    {
                        if (dlg.ShowDialog(this) != DialogResult.OK) return;
                        break;
                    }
                    catch (ArgumentException)
                    {
                        MessageBox.Show(this, "このフォントはこのアプリでは使えません（TrueType 形式のフォントだけ使えます）。\nほかのフォントを選んでください。",
                            "コントローラーのバッテリー", MessageBoxButtons.OK, MessageBoxIcon.Information);
                        dlg.Font = Font;
                    }
                }
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
        var verItem = new ToolStripMenuItem("DS4Battery v" + Updater.Current) { Enabled = false };
        menu.Items.Add(verItem);
        menu.Items.Add("終了", null, (s, e) => Close());

        // 新しい版の確認：起動の30秒後と、その後24時間ごと。見つかったらメニューのいちばん上に出す。
        updateItem = new ToolStripMenuItem { Visible = false, Font = new Font(menu.Font, FontStyle.Bold) };
        updateItem.Click += (s, e) => StartUpdate();
        menu.Items.Insert(0, updateItem);
        updateSep = new ToolStripSeparator { Visible = false };
        menu.Items.Insert(1, updateSep);
        var updTimer = new System.Windows.Forms.Timer { Interval = 30 * 1000 };
        updTimer.Tick += (s, e) => { updTimer.Interval = 24 * 60 * 60 * 1000; CheckUpdateInBackground(); };
        updTimer.Start();
        menu.Opening += (s, e) => startupItem.Checked = IsStartup();
        ContextMenuStrip = menu;

        tip = new ToolTip();
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

    ToolStripMenuItem updateItem;
    ToolStripSeparator updateSep;
    Updater.Release pending;
    bool updating;

    void CheckUpdateInBackground()
    {
        if (updating) return;
        var t = new Thread(() =>
        {
            var rel = Updater.FindNewer();
            if (rel == null) return;
            try
            {
                BeginInvoke((Action)(() =>
                {
                    pending = rel;
                    updateItem.Text = "新しい版があります（v" + rel.Version + "）→ 更新する";
                    updateItem.Visible = updateSep.Visible = true;
                }));
            }
            catch { }
        }) { IsBackground = true };
        t.Start();
    }

    void StartUpdate()
    {
        if (pending == null || updating) return;
        updating = true;
        updateItem.Text = "更新しています…";
        updateItem.Enabled = false;
        overBadge = false; Cursor = Cursors.Default;
        Render(); // 印を消す
        tip.SetToolTip(this, TooltipText());
        var rel = pending;
        var t = new Thread(() =>
        {
            string err = Updater.Apply(rel);
            if (err == null) err = Updater.LaunchNewOrRollback();
            BeginInvoke((Action)(() =>
            {
                if (err == null) { Close(); return; }
                updating = false;
                updateItem.Enabled = true;
                updateItem.Text = "新しい版があります（v" + rel.Version + "）→ 更新する";
                MessageBox.Show("更新できませんでした。今の版のまま使えます。\n" + err +
                    "\n\n手で更新するときは GitHub の Releases から DS4Battery.exe を落としてください。", "DS4Battery");
            }));
        }) { IsBackground = true };
        t.Start();
    }

    int bgAlpha = 130;
    int batteryStyle = 0;              // 0 いまの（10マス）／1 コンパクト／2 電池の絵なし

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

    // 自動起動はタスク スケジューラの「ログオン時」で行う。
    // レジストリの Run は Windows の起動処理で飛ばされることがあるため（実機で確認）。
    // 自分のログオンだけのタスクなので管理者の権限は要らない。
    const string TaskName = "DS4Battery";

    static object Com(object o, string name, params object[] args)
    {
        return o.GetType().InvokeMember(name, System.Reflection.BindingFlags.InvokeMethod, null, o, args);
    }

    static object TaskFolder()
    {
        var svc = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service"));
        Com(svc, "Connect");
        return Com(svc, "GetFolder", "\\");
    }

    static bool HasTask()
    {
        try { Com(TaskFolder(), "GetTask", TaskName); return true; } catch { return false; }
    }

    static bool HasRunValue()
    {
        using (var k = Registry.CurrentUser.OpenSubKey(RunKey)) return k != null && k.GetValue(RunName) != null;
    }

    static void DeleteRunValue()
    {
        using (var k = Registry.CurrentUser.OpenSubKey(RunKey, true)) if (k != null && k.GetValue(RunName) != null) k.DeleteValue(RunName);
    }

    static void RegisterTask()
    {
        string user = System.Security.SecurityElement.Escape(Environment.UserDomainName + "\\" + Environment.UserName);
        string exe = System.Security.SecurityElement.Escape(Application.ExecutablePath);
        string xml =
            "<?xml version=\"1.0\" encoding=\"UTF-16\"?>" +
            "<Task version=\"1.2\" xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\">" +
            "<RegistrationInfo><Description>コントローラーのバッテリー残量を表示（DS4Battery）</Description></RegistrationInfo>" +
            "<Triggers><LogonTrigger><Enabled>true</Enabled><UserId>" + user + "</UserId><Delay>PT10S</Delay></LogonTrigger></Triggers>" +
            "<Principals><Principal id=\"Author\"><UserId>" + user + "</UserId><LogonType>InteractiveToken</LogonType><RunLevel>LeastPrivilege</RunLevel></Principal></Principals>" +
            "<Settings><MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>" +
            "<DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries><StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>" +
            "<ExecutionTimeLimit>PT0S</ExecutionTimeLimit><Priority>5</Priority><AllowStartOnDemand>true</AllowStartOnDemand></Settings>" +
            "<Actions Context=\"Author\"><Exec><Command>" + exe + "</Command></Exec></Actions></Task>";
        // 6 = 作成または更新、3 = ログオン中のユーザーとして実行
        Com(TaskFolder(), "RegisterTask", TaskName, xml, 6, null, null, 3, null);
    }

    // 起動のたびに呼ぶ。前の版の Run の登録をタスクへ移し、exe を動かしたときは場所を更新する。
    public static void RefreshStartup()
    {
        try
        {
            if (HasTask() || HasRunValue()) { RegisterTask(); DeleteRunValue(); }
        }
        catch { } // タスクを作れないときは Run の登録をそのまま残す
    }

    bool IsStartup()
    {
        return HasTask() || HasRunValue();
    }

    void ToggleStartup()
    {
        try
        {
            if (IsStartup())
            {
                if (HasTask()) Com(TaskFolder(), "DeleteTask", TaskName, 0);
                DeleteRunValue();
            }
            else RegisterTask();
        }
        catch (Exception ex)
        {
            MessageBox.Show("自動起動の設定を変えられませんでした。\n" + ex.Message, "DS4Battery");
        }
    }

    // 更新の印（青い丸）の場所。印がないときは空。
    RectangleF badgeRect = RectangleF.Empty;
    bool overBadge;
    Point locationAtDown;

    bool HitBadge(Point p)
    {
        if (badgeRect.IsEmpty) return false;
        var r = badgeRect; r.Inflate(3, 3); // 小さいので少し広めに受ける
        return r.Contains(p);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left) { dragging = true; dragStart = e.Location; locationAtDown = Location; }
    }
    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (dragging) Location = new Point(Location.X + e.X - dragStart.X, Location.Y + e.Y - dragStart.Y);
        bool over = !dragging && HitBadge(e.Location);
        Cursor = over ? Cursors.Hand : Cursors.Default;
        if (over != overBadge) { overBadge = over; tip.SetToolTip(this, TooltipText()); }
    }
    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (overBadge) { overBadge = false; Cursor = Cursors.Default; tip.SetToolTip(this, TooltipText()); }
    }
    protected override void OnMouseUp(MouseEventArgs e)
    {
        if (!dragging) return;
        dragging = false;
        // 動かさずに印の上で離した＝印のクリック。更新を始める。
        if (Location == locationAtDown && HitBadge(e.Location)) { StartUpdate(); return; }
        SavePosition();
    }

    // 値 → 表示（Linux の公式ドライバーと同じ解釈：値 n は n×10〜n×10+9%）
    const int MarkNone = 0, MarkBolt = 1, MarkShield = 2;

    void Describe(out string text, out int segments, out Color color, out int mark)
    {
        mark = MarkNone;
        int lv = reader.Level, st = reader.State;
        bool fresh = DateTime.UtcNow.Ticks - Interlocked.Read(ref reader.LastUpdateTicks) < TimeSpan.FromSeconds(10).Ticks;
        if (!reader.Connected || lv < 0 || !fresh)
        {
            text = reader.Connected ? "読み取り中…" : "未接続"; segments = 0; color = Color.Gray; return;
        }
        if (reader.Coarse)
        {
            // Switch Pro・Xbox：本体が返すのは数段階だけなので、言葉で出す
            int sl = reader.CoarseLevel; var words = reader.CoarseWords;
            if (sl == Reader.CoarseWired) { text = "有線"; segments = 10; color = Color.FromArgb(80, 200, 120); return; }
            if (sl < 0 || words == null || sl >= words.Length) { text = "読み取り中…"; segments = 0; color = Color.Gray; return; }
            text = words[sl];
            segments = lv;
            if (st == Reader.Charging) { mark = MarkBolt; color = Color.FromArgb(90, 170, 255); return; }
            color = sl == 0 ? Color.FromArgb(255, 70, 70) : sl == 1 ? Color.FromArgb(255, 190, 60) : Color.FromArgb(80, 200, 120);
            return;
        }
        if (st == Reader.Full) { text = "FULL"; segments = 10; color = Color.FromArgb(80, 200, 120); return; }
        if (st == Reader.Error) { text = "充電エラー"; segments = 0; color = Color.OrangeRed; return; }
        segments = lv;
        if (st == Reader.Charging)
        {
            // 充電中の 10 は「まだ満充電ではない」＝実際は 90%台。満充電になると FULL に変わる。
            text = lv >= 9 ? "90〜99%" : lv * 10 + "〜" + (lv * 10 + 9) + "%";
            mark = MarkBolt;
            color = Color.FromArgb(90, 170, 255);
            return;
        }
        // つないでいるが本体が充電していない（充電しすぎを防いでいる）：盾＋チェックと外していたときの残量
        if (st == Reader.PluggedIdle) mark = MarkShield;
        text = lv >= 10 ? "100%" : lv * 10 + "〜" + (lv * 10 + 9) + "%";
        color = lv <= 1 ? Color.FromArgb(255, 70, 70) : lv <= 3 ? Color.FromArgb(255, 190, 60) : Color.FromArgb(80, 200, 120);
    }

    string TooltipText()
    {
        if (updating) return "更新しています…";
        // 印の上：更新のことだけを出す
        if (overBadge && pending != null) return "新しい版があります（v" + Updater.Current + " → v" + pending.Version + "）\nクリックすると更新します";
        string upd = pending != null ? "\n新しい版があります（v" + pending.Version + "）。右の青い印をクリックで更新" : "";
        if (!reader.Connected) return "コントローラー未接続" + upd;
        string link = reader.Coarse ? reader.Link : (reader.Bluetooth ? "Bluetooth" : "USB");
        return reader.Model + (link == "" ? "" : "（" + link + "）") + upd;
    }

    // 背景は半透明・電池の絵と文字は不透明で描く（ピクセルごとの透明度）
    void Render()
    {
        if (!IsHandleCreated) return;
        string text; int seg; Color col; int mark;
        Describe(out text, out seg, out col, out mark);
        bool bolt = mark == MarkBolt;

        float k = fontPt / 10f;                 // 文字の大きさに合わせて全体を拡大縮小
        // 電池の絵の幅：いまの（10マス）／コンパクト（小さい電池）／なし（文字だけ）
        float bw = batteryStyle == 0 ? 44 * k : batteryStyle == 1 ? 22 * k : 0;
        float bh = batteryStyle == 0 ? 14 * k : 11 * k;
        int bx = batteryStyle == 2 ? (int)(10 * k) : (int)(8 * k + bw + 9 * k);
        // 充電中は稲妻、つないで充電していないとき（充電しすぎを防いでいる）は盾＋チェック
        // 電池の絵があるときはマークを電池の上に重ね、絵がないときは文字の前に置く
        bool overlay = batteryStyle != 2;
        int tx = overlay ? bx : mark == MarkBolt ? bx + (int)(11 * k) : mark == MarkShield ? bx + (int)(14 * k) : bx;
        float markC = overlay ? 8 * k + bw / 2f : bx + 4.5f * k;          // マークの中心の x
        float mu = overlay && batteryStyle == 1 ? 0.85f * k : k;          // コンパクトの電池は小さいので少し縮める
        int w, h = (int)Math.Round(30 * k), textW;
        using (var tmp = new Bitmap(1, 1))
        using (var tg = Graphics.FromImage(tmp))
            textW = (int)tg.MeasureString(text, Font).Width;
        // 新しい版があるときは、文字の右に小さな印（青い丸に上向きの矢印）を出す
        bool badge = pending != null && !updating;
        float bd = 12 * k, bdx = tx + textW - 1 * k;
        w = badge ? (int)(bdx + bd + 8 * k) : tx + textW + (int)(8 * k);
        badgeRect = badge ? new RectangleF(bdx, (h - bd) / 2f, bd, bd) : RectangleF.Empty;
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
                    float u = mu, cx = markC - 4.5f * u, cy = h / 2f;
                    var pts = new[] {
                        new PointF(cx + 5.5f * u, cy - 7 * u), new PointF(cx + 0.5f * u, cy + 1 * u),
                        new PointF(cx + 4f * u, cy + 1 * u),   new PointF(cx + 2.5f * u, cy + 7 * u),
                        new PointF(cx + 8.5f * u, cy - 1.5f * u), new PointF(cx + 5f * u, cy - 1.5f * u),
                        new PointF(cx + 7f * u, cy - 7 * u) };
                    using (var sp = new Pen(Color.FromArgb(200, 0, 0, 0), 1.5f * k)) g.DrawPolygon(sp, pts);
                    using (var yb = new SolidBrush(Color.FromArgb(255, 210, 60))) g.FillPolygon(yb, pts);
                }
                else if (mark == MarkShield)
                {
                    // 盾＋チェック（充電しすぎを防いでいる＝大丈夫）。幅 13 前後、中心は bx+5。
                    float u = mu, c = overlay ? markC : bx + 5 * k, cy = h / 2f, sx = 1.4f;
                    Func<float, float, PointF> P = (x, y) => new PointF(c + x * sx * u, cy + y * u);
                    if (overlay)
                    {
                        // 電池に重ねるときは盾を外し、黒いふち付きの白いチェックだけ（緑の電池に緑の盾は見分けにくい）
                        float cu = batteryStyle == 1 ? 0.85f * k : k;
                        Func<float, float, PointF> Q = (x, y) => new PointF(c + x * cu, cy + y * cu);
                        var pts = new[] { Q(-3.6f, 0f), Q(-1f, 2.8f), Q(3.8f, -3f) };
                        var round = System.Drawing.Drawing2D.LineCap.Round; var rj = System.Drawing.Drawing2D.LineJoin.Round;
                        using (var ol = new Pen(Color.FromArgb(220, 0, 0, 0), 4.2f * cu) { StartCap = round, EndCap = round, LineJoin = rj }) g.DrawLines(ol, pts);
                        using (var ck = new Pen(Color.White, 2.2f * cu) { StartCap = round, EndCap = round, LineJoin = rj }) g.DrawLines(ck, pts);
                    }
                    else
                    {
                    using (var sh = new System.Drawing.Drawing2D.GraphicsPath())
                    {
                        sh.AddLines(new[] { P(0, -7), P(4.5f, -5), P(4.5f, -1) });
                        sh.AddBezier(P(4.5f, -1), P(4.5f, 3.5f), P(2, 6), P(0, 7));
                        sh.AddBezier(P(0, 7), P(-2, 6), P(-4.5f, 3.5f), P(-4.5f, -1));
                        sh.AddLines(new[] { P(-4.5f, -1), P(-4.5f, -5) });
                        sh.CloseFigure();
                        using (var sp = new Pen(Color.FromArgb(200, 0, 0, 0), 1.5f * k)) g.DrawPath(sp, sh);
                        using (var gb = new SolidBrush(Color.FromArgb(80, 200, 120))) g.FillPath(gb, sh);
                    }
                    using (var ck = new Pen(Color.White, 1.7f * k) { StartCap = System.Drawing.Drawing2D.LineCap.Round, EndCap = System.Drawing.Drawing2D.LineCap.Round, LineJoin = System.Drawing.Drawing2D.LineJoin.Round })
                        g.DrawLines(ck, new[] { P(-2.2f, 0.2f), P(-0.5f, 2.2f), P(2.3f, -2.5f) });
                    }
                }

                // 文字（背景が薄くても読めるよう影を付ける）
                var sz = g.MeasureString(text, Font);
                float ty = (h - sz.Height) / 2f + 1;
                Color fc = col == Color.Gray ? Color.Silver : textColor.IsEmpty ? col : textColor;
                using (var sh = new SolidBrush(Color.FromArgb(200, 0, 0, 0))) g.DrawString(text, Font, sh, tx + 1, ty + 1);
                using (var br = new SolidBrush(fc)) g.DrawString(text, Font, br, tx, ty);

                if (badge)
                {
                    float by = (h - bd) / 2f, mx = bdx + bd / 2f, u = k;
                    using (var circle = new SolidBrush(Color.FromArgb(40, 140, 255))) g.FillEllipse(circle, bdx, by, bd, bd);
                    using (var edge = new Pen(Color.FromArgb(200, 0, 0, 0), 1.2f * k)) g.DrawEllipse(edge, bdx, by, bd, bd);
                    using (var ar = new Pen(Color.White, 1.5f * k) { StartCap = System.Drawing.Drawing2D.LineCap.Round, EndCap = System.Drawing.Drawing2D.LineCap.Round, LineJoin = System.Drawing.Drawing2D.LineJoin.Round })
                    {
                        float cy2 = h / 2f;
                        g.DrawLine(ar, mx, cy2 + 2.4f * u, mx, cy2 - 2.4f * u); // 丸のふちから 1〜2px 離す
                        g.DrawLines(ar, new[] { new PointF(mx - 2.3f * u, cy2 - 0.2f * u), new PointF(mx, cy2 - 2.5f * u), new PointF(mx + 2.3f * u, cy2 - 0.2f * u) });
                    }
                }
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
    static void Main(string[] args)
    {
        // 更新の直後は、古い版が閉じるのを少し待つ
        bool afterUpdate = Array.IndexOf(args, "--after-update") >= 0;
        // 更新の直後は「起動できた」と古い版へ知らせる。届かなければ古い版が元に戻す。
        if (afterUpdate) { try { using (var ev = EventWaitHandle.OpenExisting(Updater.ReadyEvent)) ev.Set(); } catch { } }
        using (var m = new Mutex(false, "DS4Battery_single_instance"))
        {
            bool got;
            try { got = m.WaitOne(afterUpdate ? 15000 : 0); }
            catch (AbandonedMutexException) { got = true; }
            if (!got) return; // 2つ目は開かない
            try
            {
                Updater.Cleanup();
                Application.EnableVisualStyles();
                BatteryForm.RefreshStartup();
                var r = new Reader();
                var t = new Thread(r.Run) { IsBackground = true };
                t.Start();
                Application.Run(new BatteryForm(r));
            }
            finally { m.ReleaseMutex(); }
        }
    }
}

// GitHub の最新リリースを見て、新しい版の exe に入れ替える。
// 動いている exe は上書きできないが名前は変えられるので、今の exe を .old にして新しい exe を置く。
static class Updater
{
    public const string Current = "1.4.4";
    const string Api = "https://api.github.com/repos/lath60/DS4Battery/releases/latest";
    const string AssetName = "DS4Battery.exe";

    public class Release { public string Version, ApiUrl, Url, Sha256; public long Size; }

    static string Exe { get { return Application.ExecutablePath; } }

    static System.Net.WebClient Client()
    {
        System.Net.ServicePointManager.SecurityProtocol |= (System.Net.SecurityProtocolType)3072; // TLS 1.2
        var wc = new System.Net.WebClient();
        wc.Headers[System.Net.HttpRequestHeader.UserAgent] = "DS4Battery-updater";
        wc.Encoding = System.Text.Encoding.UTF8;
        return wc;
    }

    // 新しい版があれば返す。無いとき・確かめられないときは null。
    public static Release FindNewer()
    {
        try
        {
            string json;
            using (var wc = Client()) json = wc.DownloadString(Api);
            var tag = System.Text.RegularExpressions.Regex.Match(json, "\"tag_name\"\\s*:\\s*\"v?([0-9.]+)\"");
            if (!tag.Success || new Version(tag.Groups[1].Value) <= new Version(Current)) return null;

            int i = json.IndexOf("\"name\":\"" + AssetName + "\"", StringComparison.Ordinal);
            if (i < 0) return null;
            int end = json.IndexOf("\"browser_download_url\"", i, StringComparison.Ordinal);
            if (end < 0) return null;
            string asset = json.Substring(i, end - i);
            var url = System.Text.RegularExpressions.Regex.Match(json.Substring(end), "^\"browser_download_url\"\\s*:\\s*\"([^\"]+)\"");
            var size = System.Text.RegularExpressions.Regex.Match(asset, "\"size\"\\s*:\\s*(\\d+)");
            var sha = System.Text.RegularExpressions.Regex.Match(asset, "\"digest\"\\s*:\\s*\"sha256:([0-9a-fA-F]{64})\"");
            if (!url.Success || !size.Success) return null;
            if (!url.Groups[1].Value.StartsWith("https://github.com/lath60/DS4Battery/releases/download/", StringComparison.Ordinal)) return null;
            // 同じファイルの API 側の取得口（"url" は "name" より前にある）。通常の取得口が止まることがあるので先に使う。
            const string apiPrefix = "\"url\":\"https://api.github.com/repos/lath60/DS4Battery/releases/assets/";
            int a = json.LastIndexOf(apiPrefix, i, StringComparison.Ordinal);
            string apiUrl = null;
            if (a >= 0)
            {
                var m = System.Text.RegularExpressions.Regex.Match(json.Substring(a), "^\"url\":\"(https://api\\.github\\.com/repos/lath60/DS4Battery/releases/assets/\\d+)\"");
                if (m.Success) apiUrl = m.Groups[1].Value;
            }
            return new Release
            {
                Version = tag.Groups[1].Value,
                ApiUrl = apiUrl,
                Url = url.Groups[1].Value,
                Size = long.Parse(size.Groups[1].Value),
                Sha256 = sha.Success ? sha.Groups[1].Value.ToLowerInvariant() : null,
            };
        }
        catch { return null; }
    }

    // 入れ替えまで行う。うまくいけば null、だめならその理由（元の exe はそのまま残す）。
    public static string Apply(Release rel)
    {
        string exe = Exe, neu = exe + ".new", old = exe + ".old";
        try
        {
            if (File.Exists(neu)) File.Delete(neu);
            bool ok = false;
            if (rel.ApiUrl != null)
            {
                try
                {
                    using (var wc = Client())
                    {
                        wc.Headers[System.Net.HttpRequestHeader.Accept] = "application/octet-stream";
                        wc.DownloadFile(rel.ApiUrl, neu);
                    }
                    ok = true;
                }
                catch { }
            }
            if (!ok) using (var wc = Client()) wc.DownloadFile(rel.Url, neu);

            var bytes = File.ReadAllBytes(neu);
            if (bytes.Length != rel.Size || bytes.Length < 2 || bytes[0] != 'M' || bytes[1] != 'Z')
                throw new Exception("落としたファイルが壊れています。");
            if (rel.Sha256 != null)
            {
                using (var h = System.Security.Cryptography.SHA256.Create())
                    if (BitConverter.ToString(h.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant() != rel.Sha256)
                        throw new Exception("落としたファイルが GitHub の記録と一致しません。");
            }

            if (File.Exists(old)) File.Delete(old);
            File.Move(exe, old);
            try { File.Move(neu, exe); }
            catch { File.Move(old, exe); throw; }

            // ウイルス対策ソフトが置いた直後に消す・中身を変えることがあるので、少し待ってから確かめる
            Thread.Sleep(2000);
            if (!SameBytes(exe, bytes))
            {
                Rollback();
                return "新しい版のファイルが消されました（ウイルス対策ソフトに止められた可能性があります）。元の版に戻しました。";
            }
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            TryDelete(neu);
            return "この場所には書き込めません（" + Path.GetDirectoryName(exe) + "）。";
        }
        catch (Exception ex)
        {
            TryDelete(neu);
            return ex.Message;
        }
    }

    public const string ReadyEvent = "DS4Battery_update_ready";

    static bool SameBytes(string path, byte[] expected)
    {
        try
        {
            var now = File.ReadAllBytes(path);
            if (now.Length != expected.Length) return false;
            for (int i = 0; i < now.Length; i++) if (now[i] != expected[i]) return false;
            return true;
        }
        catch { return false; }
    }

    // 新しい exe を消して、.old を元の名前に戻す
    static void Rollback()
    {
        string exe = Exe, old = exe + ".old";
        for (int n = 0; n < 10; n++)
        {
            try
            {
                if (File.Exists(exe)) File.Delete(exe);
                if (File.Exists(old)) File.Move(old, exe);
                return;
            }
            catch { Thread.Sleep(500); }
        }
    }

    // 新しい版を起動し、「起動できた」の知らせを待つ。届かなければ元の版に戻す。
    // うまくいけば null（呼んだ側は自分を閉じる）、だめならその理由（呼んだ側はそのまま動き続ける）。
    public static string LaunchNewOrRollback()
    {
        using (var ready = new EventWaitHandle(false, EventResetMode.ManualReset, ReadyEvent))
        {
            System.Diagnostics.Process p = null;
            try
            {
                p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Exe, "--after-update") { UseShellExecute = false });
            }
            catch { }
            if (p != null && ready.WaitOne(20000)) return null;

            try { if (p != null && !p.HasExited) p.Kill(); } catch { }
            Rollback();
            return "新しい版を起動できませんでした（ウイルス対策ソフトに止められた可能性があります）。元の版に戻しました。";
        }
    }

    // 前の更新で残った途中のファイルを消す。.old は、新しい版があとで消されたときの控えとして次の更新まで残す。
    public static void Cleanup()
    {
        TryDelete(Exe + ".new");
    }

    static void TryDelete(string p) { try { if (File.Exists(p)) File.Delete(p); } catch { } }
}
