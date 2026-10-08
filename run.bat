@echo off
cd /d "%~dp0"
rem Close any copy of WinNotch that's already running, so the build can replace it
taskkill /im WinNotch.exe /f >nul 2>&1
timeout /t 1 /nobreak >nul

echo Building WinNotch...
dotnet build WinNotch.csproj -c Debug -nologo -v q
if errorlevel 1 (
  echo.
  echo Build failed - copy the error above and send it to Claude.
  pause
  exit /b 1
)

rem Start the app on its own, so this window can close
start "" "bin\Debug\net10.0-windows10.0.19041.0\WinNotch.exe"
