@echo off
setlocal enabledelayedexpansion
cd /d "%~dp0"

set "MINGW_BIN="
for /f "delims=" %%i in ('where g++ 2^>nul') do (
    if not defined MINGW_BIN set "MINGW_BIN=%%~dpi"
)

if not defined MINGW_BIN (
    for /d %%d in ("%LOCALAPPDATA%\Microsoft\WinGet\Packages\BrechtSanders.WinLibs.POSIX.UCRT*") do (
        if exist "%%~d\mingw64\bin\g++.exe" set "MINGW_BIN=%%~d\mingw64\bin\"
    )
)

if not defined MINGW_BIN (
    echo [!] g++.exe not found. Install MinGW-w64 or add it to PATH.
    exit /b 1
)

echo [*] Using compiler: !MINGW_BIN!g++.exe

"!MINGW_BIN!g++.exe" -shared -O2 -static -static-libgcc -static-libstdc++ ^
    -std=c++17 -DUNICODE -D_UNICODE ^
    -o "%~dp0cpp\fs_backend.dll" ^
    "%~dp0cpp\backend.cpp" ^
    -ladvapi32 -lshell32 -lws2_32 -lkernel32 -luser32 ^
    -Wl,--out-implib,"%~dp0cpp\libfs_backend.a"

if errorlevel 1 (
    echo [!] Build failed.
    exit /b 1
)

echo [+] Built: %~dp0cpp\fs_backend.dll
endlocal
