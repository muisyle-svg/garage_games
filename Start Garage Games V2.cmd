@echo off
setlocal
start "" powershell.exe -NoLogo -NoProfile -STA -ExecutionPolicy Bypass -WindowStyle Hidden -File "%~dp0Start Garage Games V2.ps1"
endlocal
