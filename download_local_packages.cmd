@echo off
rem Downloads every NuGet dependency of the project in this folder (sln/slnx,
rem or every *.csproj recursively) into the flat local feed "packages".
rem Portable: copy this script into any project folder and run it.
setlocal EnableExtensions
cd /d "%~dp0"

echo Downloading all NuGet dependencies into "packages"...

set "SLN="
for %%f in ("%~dp0*.slnx") do if not defined SLN set "SLN=%%~ff"
if not defined SLN for %%f in ("%~dp0*.sln") do if not defined SLN set "SLN=%%~ff"

rem When the active nuget.config is the offline-only one, give restore an online source
set "EXTRA="
if exist nuget.config (
    findstr /C:"LocalOffline" nuget.config >nul 2>&1 && set "EXTRA=--source https://api.nuget.org/v3/index.json"
)

set "TMPCACHE=%TEMP%\nuget-dl-%RANDOM%"
if exist "%TMPCACHE%" rmdir /s /q "%TMPCACHE%"
set "NUGET_PACKAGES=%TMPCACHE%"

set "FAIL=1"
if defined SLN (
    echo Restoring solution: "%SLN%"
    dotnet restore "%SLN%" %EXTRA% --no-cache --force && set "FAIL=0"
) else (
    echo No .slnx/.sln found - restoring every .csproj recursively...
    set "FAIL=0"
    for /f "delims=" %%p in ('dir /s /b /a:-d "*.csproj" 2^>nul') do dotnet restore "%%p" %EXTRA% --no-cache --force || set "FAIL=1"
)
if "%FAIL%"=="1" (
    echo ERROR: restore failed - nothing was downloaded.
    rmdir /s /q "%TMPCACHE%" 2>nul
    endlocal
    exit /b 1
)

rem Flat-copy every downloaded .nupkg into packages\ (local feeds are scanned flat only)
powershell -NoProfile -ExecutionPolicy Bypass -Command "$root = $env:NUGET_PACKAGES; $files = @(); for ($i = 0; $i -lt 3 -and -not $files; $i++) { $files = @([System.IO.Directory]::GetFiles($root, '*.nupkg', [System.IO.SearchOption]::AllDirectories)); if (-not $files) { Start-Sleep -Seconds 2 } }; if (-not $files) { Write-Host 'WARNING: no .nupkg files were downloaded.'; exit 1 }; New-Item -ItemType Directory -Force -Path packages | Out-Null; $ok = 0; foreach ($f in $files) { robocopy ([System.IO.Path]::GetDirectoryName($f)) packages ([System.IO.Path]::GetFileName($f)) /njh /njs /ndl /nfl > $null; if ($LASTEXITCODE -lt 8) { $ok++ } else { Write-Host ('WARNING: failed to copy ' + [System.IO.Path]::GetFileName($f)) } }; Write-Host ('Local feed: ' + $ok + ' of ' + $files.Count + ' packages copied into "packages".')"
if errorlevel 1 (
    echo WARNING: the local feed was not filled.
)

set "NUGET_PACKAGES="
rmdir /s /q "%TMPCACHE%" 2>nul

rem Re-restore with the active configuration so obj\project.assets.json points at the normal cache again
echo Re-restoring with the active configuration...
if defined SLN (
    dotnet restore "%SLN%" --force >nul 2>&1
) else (
    for /f "delims=" %%p in ('dir /s /b /a:-d "*.csproj" 2^>nul') do dotnet restore "%%p" --force >nul 2>&1
)

echo Done.
endlocal
exit /b 0
