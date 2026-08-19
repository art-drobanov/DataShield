@echo off
setlocal
cd /d "%~dp0"

echo Switching to ONLINE NuGet sources...

if exist nuget.config del /q nuget.config
if exist nuget.online.config ren nuget.online.config nuget.config

if not exist nuget.config (
    echo Creating default nuget.config with nuget.org...
    > nuget.config echo ^<?xml version="1.0" encoding="utf-8"?^>
    >> nuget.config echo ^<configuration^>
    >> nuget.config echo   ^<packageSources^>
    >> nuget.config echo     ^<add key="nuget.org" value="https://api.nuget.org/v3/index.json" /^>
    >> nuget.config echo   ^</packageSources^>
    >> nuget.config echo ^</configuration^>
)

echo Done. Now using online NuGet sources.
endlocal
