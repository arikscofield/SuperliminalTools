@echo off
rem Build SuperliminalDeterminism.dll (x64).
rem
rem Run this from an "x64 Native Tools Command Prompt for VS", which requires the
rem "Desktop development with C++" workload in the Visual Studio Installer. A
rem .NET-only install does not ship cl.exe.
rem
rem The output must be x64. If the plugin logs "Failed to load
rem SuperliminalDeterminism.dll (Win32 error 193)" you built it 32-bit; you were
rem in the x86 command prompt.

setlocal

where cl >nul 2>nul
if errorlevel 1 (
    echo ERROR: cl.exe not found.
    echo Open "x64 Native Tools Command Prompt for VS" and run this again.
    echo If that shortcut does not exist, add "Desktop development with C++"
    echo via the Visual Studio Installer.
    exit /b 1
)

cd /d "%~dp0"

cl /nologo /LD /O2 /EHsc determinism.cpp /link /OUT:SuperliminalDeterminism.dll
if errorlevel 1 (
    echo Build failed.
    exit /b 1
)

del /q determinism.obj determinism.exp determinism.lib 2>nul

echo.
echo Built CPUDeterminism\SuperliminalDeterminism.dll
echo Verify it is x64 with:  dumpbin /headers SuperliminalDeterminism.dll ^| findstr machine
echo Expect: 8664 (x64)

endlocal
