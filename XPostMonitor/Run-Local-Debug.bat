@echo off
cd /d "%~dp0"
set "ASPNETCORE_ENVIRONMENT=Development"
set "DOTNET_ENVIRONMENT=Development"

if not exist "bin\Debug\net8.0\XPostMonitor.exe" (
    echo Chua co ban Debug. Hay Build project mot lan trong Visual Studio.
    pause
    exit /b 1
)

"bin\Debug\net8.0\XPostMonitor.exe"

echo.
echo Bot da dung. Nhan phim bat ky de dong cua so.
pause >nul
