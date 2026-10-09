@echo off
cd /d "%~dp0"
taskkill /im WinNotch.exe /f >nul 2>&1
dotnet build -c Debug
if %errorlevel% neq 0 (
  echo.
  echo Build failed - copy the error above and send it to Claude.
  pause
  exit /b 1
)
start "" "bin\Debug\net10.0-windows10.0.19041.0\WinNotch.exe"
