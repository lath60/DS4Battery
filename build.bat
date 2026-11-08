@echo off
chcp 65001 > nul
cd /d "%~dp0"
C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe /nologo /target:winexe /codepage:65001 /out:DS4Battery.exe /r:System.Windows.Forms.dll /r:System.Drawing.dll DS4Battery.cs
if errorlevel 1 (echo ビルドに失敗しました) else (echo DS4Battery.exe を作りました)
pause
