@echo off
setlocal
set "CSC=%SystemRoot%\Microsoft.NET\Framework\v4.0.30319\csc.exe"
if not exist "%CSC%" (
    echo Microsoft .NET Framework 4.8 compiler is required.
    exit /b 1
)
pushd "%~dp0" || exit /b 1
"%CSC%" /nologo /codepage:65001 /target:winexe /platform:anycpu /optimize+ /warn:4 /warnaserror+ /win32manifest:App.manifest /win32icon:QingShi.ico /resource:QingShi.ico,DuplicateMediaApp.QingShi.ico /out:DuplicateMediaApp.exe /reference:System.dll /reference:System.Core.dll /reference:System.Drawing.dll /reference:System.Windows.Forms.dll Program.cs MediaEngine.cs Cleanup.cs ShellBin.cs >build.log 2>&1
if errorlevel 1 (
    type build.log
    popd
    exit /b 1
)
if not exist DuplicateMediaApp.exe (
    echo EXE missing. Check build.log and the Windows application event log.
    popd
    exit /b 1
)
echo Build succeeded: WinForms, .NET Framework 4.8, AnyCPU.>>build.log
echo Created: %~dp0DuplicateMediaApp.exe
popd
exit /b 0
