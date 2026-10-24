@echo off
setlocal
pushd "%~dp0.."
set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"

if not exist "%CSC%" (
    echo csc.exe not found. .NET Framework 4.x is required ^(included in Windows 10/11^).
    popd
    exit /b 1
)

"%CSC%" -nologo -codepage:65001 -target:winexe -optimize+ ^
    -out:"%CD%\MOSS-Image.exe" ^
    -r:System.dll -r:System.Drawing.dll -r:System.Windows.Forms.dll -r:System.Core.dll ^
    -r:System.IO.Compression.dll -r:System.IO.Compression.FileSystem.dll ^
    -resource:"%CD%\web\index.html",web.index.html ^
    "%~dp0Launcher.cs"

if errorlevel 1 (
    popd
    exit /b 1
)

echo Built: %CD%\MOSS-Image.exe
popd
