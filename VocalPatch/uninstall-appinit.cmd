@echo off
rem Double-click to remove the VocalPatch AppInit_DLL registration.
rem This wrapper elevates to administrator; approve the UAC prompt.
setlocal
set "PS=powershell -NoProfile -ExecutionPolicy Bypass"
%PS% -Command "Start-Process powershell -Verb RunAs -ArgumentList '-NoProfile','-ExecutionPolicy','Bypass','-NoExit','-File','\"%~dp0uninstall-appinit.ps1\"','-RemoveFiles'"
endlocal
