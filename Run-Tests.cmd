@echo off
setlocal
pushd "%~dp0" || exit /b 1
call Build-App.cmd
if errorlevel 1 goto failed
"%SystemRoot%\Microsoft.NET\Framework\v4.0.30319\csc.exe" /nologo /codepage:65001 /target:exe /platform:anycpu /optimize+ /warn:4 /warnaserror+ /win32manifest:App.manifest /r:DuplicateMediaApp.exe /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:Microsoft.CSharp.dll /out:QingShi.Tests.exe Test-App.cs BinCheck.cs
if errorlevel 1 goto failed
QingShi.Tests.exe %*
if errorlevel 1 goto failed
popd
exit /b 0
:failed
popd
exit /b 1
