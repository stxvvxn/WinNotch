@echo off
cd /d "%~dp0"
taskkill /im WinNotch.exe /f >nul 2>&1
timeout /t 1 /nobreak >nul
echo Building WinNotch as a single portable exe...
echo.
dotnet publish WinNotch.csproj -c Release -r win-x64 --self-contained true ^
  -p:PublishSingleFile=true ^
  -p:IncludeNativeLibrariesForSelfExtract=true ^
  -p:EnableCompressionInSingleFile=true ^
  -o publish
echo.
if %errorlevel%==0 (
  echo Done! Your exe is in the "publish" folder: publish\WinNotch.exe
) else (
  echo Build failed - copy the error above and send it to Claude.
)
pause
