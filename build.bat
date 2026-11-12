@echo off
setlocal
cd /d "%~dp0"

set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" set "CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe"
if not exist "%CSC%" (
    echo [ERROR] .NET Framework 4.x C# compiler not found.
    echo Install .NET Framework 4.x and try again.
    pause
    exit /b 1
)

if not exist "build" mkdir "build"

"%CSC%" /nologo /target:winexe /platform:anycpu /optimize+ /codepage:65001 ^
    /win32icon:"ClipAck.ico" ^
    /out:"build\ClipAck.exe" ^
    /r:System.Windows.Forms.dll /r:System.Drawing.dll ^
    "ClipAck.cs"

if errorlevel 1 (
    echo.
    echo [ERROR] Build failed.
    pause
    exit /b 1
)

echo.
echo Build successful: build\ClipAck.exe
pause
