@echo off
rem Double-click to install VocalPatch as an AppInit_DLL (auto-inject into editors).
rem This wrapper elevates to administrator; approve the UAC prompt.
setlocal
set "PS=powershell -NoProfile -ExecutionPolicy Bypass"
%PS% -Command "Start-Process powershell -Verb RunAs -ArgumentList '-NoProfile','-ExecutionPolicy','Bypass','-NoExit','-File','\"%~dp0install-appinit.ps1\"'"
endlocal
