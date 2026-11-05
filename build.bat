@echo off
rem Builds Flick.exe. Just double-click this file.
rem   build.bat             small exe (needs the free .NET 10 Desktop Runtime, which Windows offers to install)
rem   build.bat standalone  bigger exe that runs on any Windows 10/11 PC with nothing installed
setlocal
cd /d "%~dp0"

set "PATH=%ProgramFiles%\dotnet;%LOCALAPPDATA%\Microsoft\dotnet;%PATH%"
call :has_sdk
if errorlevel 1 (
    echo The .NET SDK isn't installed yet. Installing it now...
    winget install --id Microsoft.DotNet.SDK.10 --exact --silent --accept-package-agreements --accept-source-agreements
    call :has_sdk
    if errorlevel 1 (
        echo.
        echo Couldn't install the .NET SDK automatically.
        echo Get it from https://dotnet.microsoft.com/download/dotnet/10.0 and run this again.
        goto :fail
    )
)

if /i "%~1"=="standalone" (
    set "ARGS=-p:SelfContained=true -p:EnableCompressionInSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true"
    set "OUT=dist\standalone"
) else (
    set "ARGS=-p:SelfContained=false"
    set "OUT=dist"
)

echo Building Flick...
dotnet publish Flick.csproj -c Release -r win-x64 %ARGS% -p:PublishSingleFile=true -p:DebugType=none -o "%OUT%" --nologo -v q
if errorlevel 1 goto :fail

echo.
echo Done: %CD%\%OUT%\Flick.exe
if not "%~1"=="nopause" if /i not "%~2"=="nopause" pause
exit /b 0

:has_sdk
dotnet --list-sdks 2>nul | findstr /r "^10\." >nul
exit /b %errorlevel%

:fail
echo.
echo The build failed. See the messages above.
if not "%~1"=="nopause" if /i not "%~2"=="nopause" pause
exit /b 1
