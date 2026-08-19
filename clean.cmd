@echo off
setlocal
cd /d "%~dp0"

echo Cleaning ignored files (bin, obj, .vs, logs, etc.)...

for /d %%D in (.) do (
    for /f "delims=" %%F in ('dir /s /b /a:d 2^>nul ^| findstr /i /r "\\bin$ \\obj$ \.vs$ \.idea$ \.vscode$ \x64$ x86$ [Dd]ebug$ [Rr]elease$ [Rr]eleases$ [Bb]uild[Ll]ogs$ [Ll]ogs$ [Tt]est[Rr]esults.*$"') do (
        echo Removing dir: %%F
        rd /s /q "%%F" 2>nul
    )
)

del /s /q /f *.log *.tmp *.user *.suo *.pdb *.nupkg TestResult.xml *.VisualState.xml nunit-*.xml Thumbs.db desktop.ini 2>nul

echo Done.
endlocal
