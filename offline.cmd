@echo off
setlocal
cd /d "%~dp0"

echo Switching to OFFLINE (local packages folder)...

if exist nuget.config ren nuget.config nuget.online.config

> nuget.config echo ^<?xml version="1.0" encoding="utf-8"?^>
>> nuget.config echo ^<configuration^>
>> nuget.config echo   ^<packageSources^>
>> nuget.config echo     ^<clear /^>
>> nuget.config echo     ^<add key="LocalOffline" value="packages" /^>
>> nuget.config echo   ^</packageSources^>
>> nuget.config echo ^</configuration^>

echo Done. Now using local "packages" folder only.
endlocal
