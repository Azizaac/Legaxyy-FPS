@echo off
setlocal
echo =======================================================
echo           BUILDING LEGAXYYFPS ^& INSTALLER
echo =======================================================

set "DOTNET_CMD=%LocalAppData%\Microsoft\dotnet\dotnet.exe"
if not exist "%DOTNET_CMD%" set "DOTNET_CMD=dotnet"

set "ISCC_CMD=C:\Users\Legaxyy\AppData\Local\Programs\Inno Setup 6\ISCC.exe"
if not exist "%ISCC_CMD%" set "ISCC_CMD=C:\Users\Legaxyy\AppData\Local\Programs\Antigravity IDE\resources\app\node_modules\innosetup\bin\ISCC.exe"
if not exist "%ISCC_CMD%" set "ISCC_CMD=iscc"

echo 1. Publishing LegaxyyFPS (Release win-x64)...
"%DOTNET_CMD%" publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o .\dist\App
if errorlevel 1 (
    echo [ERROR] Dotnet publish failed!
    pause
    exit /b 1
)

echo 2. Copying runtime dependencies...
if exist ".\bin\Release\net8.0-windows\win-x64\WebView2Loader.dll" (
    copy /y ".\bin\Release\net8.0-windows\win-x64\WebView2Loader.dll" ".\dist\App\WebView2Loader.dll" >nul
)

echo 3. Compiling Installer with Inno Setup...
"%ISCC_CMD%" installer.iss
if errorlevel 1 (
    echo [ERROR] Inno Setup compilation failed!
    pause
    exit /b 1
)

echo 4. Copying installer to web-server/downloads...
if exist ".\Release\LegaxyyFPS_Setup_v1.2.1.exe" (
    copy /y ".\Release\LegaxyyFPS_Setup_v1.2.1.exe" ".\web-server\downloads\LegaxyyFPS_Setup_v1.2.1.exe" >nul
)

echo =======================================================
echo   BUILD SUCCESS! Installer is ready:
echo   - Release\LegaxyyFPS_Setup_v1.2.1.exe
echo   - web-server\downloads\LegaxyyFPS_Setup_v1.2.1.exe
echo =======================================================
pause
