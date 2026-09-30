@echo off
taskkill /F /IM LegaxyyFPS.exe >nul 2>&1
schtasks /Delete /TN "LegaxyyFPSStartup" /F >nul 2>&1
exit /b 0
