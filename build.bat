@echo off
cd /d "%~dp0"
%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe ^
  /target:winexe /nologo /optimize+ ^
  /out:KimiQuotaTray.exe KimiQuotaTray.cs ^
  /win32manifest:app.manifest ^
  /r:System.dll,System.Drawing.dll,System.Windows.Forms.dll,System.Net.Http.dll,System.Runtime.Serialization.dll
pause
