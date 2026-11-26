@echo off
powershell.exe -NoProfile -ExecutionPolicy RemoteSigned -File "%~dp0packaging\StartRegistered.ps1"
if errorlevel 1 pause
