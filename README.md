# fsclient

![fsclient](assets/fsclient.png)

PyQt6-лаунчер для стратегий zapret с нативным C++ бэкендом.

## Скачать

- **Готовый EXE:** вкладка [Releases](https://github.com/bleep-go/fsclient/releases/latest) → `fsclient.exe`.
  Прямая ссылка: `https://github.com/bleep-go/fsclient/releases/latest/download/fsclient.exe`
- **Исходники:** кнопка `Code → Download ZIP` на главной странице репозитория
  или `git clone https://github.com/bleep-go/fsclient.git`.

EXE уже содержит DLL и ресурсы внутри, запрашивает права администратора через
UAC при запуске и не показывает консоль `winws.exe` на панели задач.

## Запуск из исходников

Нужен Python 3.10+ с `PyQt6` и компилятор MinGW-w64 (`g++`).

```bat
pip install PyQt6
build.bat
python main.py
```

`build.bat` сам ищет `g++` в `PATH`, а при отсутствии — в WinGet-пакете
`BrechtSanders.WinLibs.POSIX.UCRT`. Результат: `cpp\fs_backend.dll`.

## Сборка EXE

```bat
pip install pyinstaller
build_exe.bat
```

Скрипт пересобирает DLL и создаёт `dist\fsclient.exe` (onefile, без консоли,
с манифестом `requireAdministrator` и иконкой `assets\fsclient.ico`).

Папки `releases\` и `plugins\` создаются рядом с EXE при первом запуске.

## Как это работает

- Состояние системы (`winws.exe` / служба `zapret`) опрашивается каждые 1.8 с.
- Повторный запуск поверх активного обхода блокируется.
- Командная строка `winws.exe` извлекается из `.bat` стратегии и запускается
  напрямую с `CREATE_NO_WINDOW` — без `cmd.exe` и без окна в панели задач.
- Вывод процесса передаётся в мини-консоль лаунчера через pipe.
- Управление службой: установка, запуск, остановка, удаление.

## Что не лежит в репозитории

`dist/`, `build/`, `__pycache__/`, `*.spec`, `cpp/*.a`, `cpp/fs_backend.dll`,
`releases/`, `plugins/` — генерируется локально или скачивается лаунчером.

`backend_mock.py` оставлен только как справочник по старому API и не импортируется.
