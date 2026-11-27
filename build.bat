@echo off
setlocal enabledelayedexpansion
cd /d "%~dp0"

rem ==== 1. Build native backend ====
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

echo [*] Building cpp\fs_backend.dll ...
"!MINGW_BIN!g++.exe" -shared -O2 -static -static-libgcc -static-libstdc++ ^
    -std=c++17 -DUNICODE -D_UNICODE ^
    -o "%~dp0cpp\fs_backend.dll" ^
    "%~dp0cpp\backend.cpp" ^
    -ladvapi32 -lshell32 -lws2_32 -lkernel32 -luser32 ^
    -Wl,--out-implib,"%~dp0cpp\libfs_backend.a"
if errorlevel 1 (
    echo [!] Native backend build failed.
    exit /b 1
)

rem ==== 2. Publish framework-dependent app into staging folder ====
echo [*] Publishing fsclient-core ...
if exist "%~dp0build-publish" rmdir /s /q "%~dp0build-publish" 2>nul
dotnet publish "%~dp0fsclient.csproj" -c Release -r win-x64 --self-contained false -p:PublishSingleFile=false -o "%~dp0build-publish"
if errorlevel 1 (
    echo [!] dotnet publish failed.
    exit /b 1
)

rem ==== 3. Embed published files into a single launcher EXE ====
set "PUB=%~dp0build-publish"
set "PUB=%PUB:\=/%"
set "ICO=%~dp0assets\fsclient.ico"
set "ICO=%ICO:\=/%"
(
    echo 100 ICON "%ICO%"
    echo 101 RCDATA "%PUB%/fsclient-core.exe"
    echo 102 RCDATA "%PUB%/fsclient-core.dll"
    echo 103 RCDATA "%PUB%/fsclient-core.deps.json"
    echo 104 RCDATA "%PUB%/fsclient-core.runtimeconfig.json"
    echo 105 RCDATA "%PUB%/cpp/fs_backend.dll"
    echo 106 RCDATA "%PUB%/assets/fsclient.ico"
) > "%~dp0launcher.rc"

"!MINGW_BIN!windres.exe" -i "%~dp0launcher.rc" -o "%~dp0launcher.res" -O coff
if errorlevel 1 (
    echo [!] Resource compilation failed.
    exit /b 1
)

echo [*] Building single dist\fsclient.exe ...
if exist "%~dp0dist" rmdir /s /q "%~dp0dist" 2>nul
mkdir "%~dp0dist"
rem -static обязателен: лаунчер загружает libwinpthread-1.dll до WinMain,
rem поэтому положить её в ресурсы и распаковать позже нельзя.
"!MINGW_BIN!g++.exe" -O2 -std=c++17 -mwindows -static -static-libgcc -static-libstdc++ ^
    -o "%~dp0dist\fsclient.exe" "%~dp0launcher.cpp" "%~dp0launcher.res" -lshell32 -luser32
if errorlevel 1 (
    echo [!] Launcher build failed.
    exit /b 1
)

rem ==== 4. Проверка: в EXE не должно остаться DLL от MinGW ====
if exist "!MINGW_BIN!objdump.exe" (
    "!MINGW_BIN!objdump.exe" -p "%~dp0dist\fsclient.exe" 2>nul | findstr /i "libwinpthread libstdc++ libgcc libatomic" >nul
    if not errorlevel 1 (
        echo [!] fsclient.exe всё ещё зависит от DLL MinGW. Сборка прервана.
        exit /b 1
    )
) else (
    echo [!] objdump.exe не найден — проверка внешних DLL пропущена.
)

echo [+] Done: %~dp0dist\fsclient.exe
endlocal
