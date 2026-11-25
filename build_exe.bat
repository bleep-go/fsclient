@echo off
setlocal
cd /d "%~dp0"

echo [*] Building native backend...
call build.bat
if errorlevel 1 exit /b 1

echo [*] Building fsclient.exe with PyInstaller...

python -m PyInstaller --noconfirm --clean --onefile --noconsole ^
    --uac-admin ^
    --name fsclient ^
    --icon "assets\fsclient.ico" ^
    --add-binary "cpp\fs_backend.dll;cpp" ^
    --add-data "assets;assets" ^
    --hidden-import PyQt6.QtCore ^
    --hidden-import PyQt6.QtGui ^
    --hidden-import PyQt6.QtWidgets ^
    main.py

if errorlevel 1 (
    echo [!] EXE build failed.
    exit /b 1
)

echo [+] Built: %~dp0dist\fsclient.exe
endlocal
