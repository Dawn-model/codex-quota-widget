@echo off
setlocal
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0uninstall.ps1"
if errorlevel 1 (
  echo.
  echo Uninstallation failed. Review the error above.
) else (
  echo.
  echo Uninstallation completed successfully.
)
pause

