# fsclient 1.1.0

![fsclient](assets/fsclient.png)

Лаунчер стратегий **zapret** на C# / WPF с нативным C++-бэкендом.
Python-версия больше не используется — фронтенд полностью переписан на C#.

## Суть программы

`fsclient` — это оболочка над проектом [Flowseal/zapret-discord-youtube](https://github.com/Flowseal/zapret-discord-youtube).
Она скачивает готовые релизы zapret, показывает список версий и стратегий,
запускает выбранную стратегию `winws.exe` или устанавливает её как службу,
а также показывает состояние обхода в мини-консоли.

**Все релизы скачиваются с оригинального репозитория
[Flowseal/zapret-discord-youtube](https://github.com/Flowseal/zapret-discord-youtube/releases).**
Этот проект не хранит и не перепаковывает бинарники zapret — он только
загружает официальные релизы через GitHub API и распаковывает их в папку
`releases\` рядом с распакованным приложением
(`%LOCALAPPDATA%\fsclient\1.1.0\app`).

### Что умеет

- Список актуальных релизов с GitHub + резервные теги `v1.10.0`–`v1.10.4`.
- Скачивание и распаковка релиза с прогресс-баром.
- Выбор стратегии и запуск через `winws.exe` без окна консоли.
- Установка / замена / удаление службы zapret.
- Опрос состояния каждые 1.8 с, кнопка «Стоп», мини-консоль вывода.
- Матовое стекло, плавный прогресс, анимации и кастомное меню в трее.
- Сворачивание в трей при закрытии окна.

## Требования

- Windows **x64**.
- **.NET 10 Desktop Runtime** (не SDK, именно Desktop Runtime).
- Права администратора: WinDivert и работа со службой требуют elevate.

Если .NET 10 не установлен, `fsclient.exe` покажет сообщение и сразу откроет
[официальную страницу скачивания](https://dotnet.microsoft.com/download/dotnet/10.0).
Проверка сделана нативным кодом до запуска WPF-приложения — сам .NET в EXE
не вшит.

## Запуск

Это **один файл**. Достаточно `dist\fsclient.exe` — рядом ничего класть не нужно:

```bat
dist\fsclient.exe
```

При запуске лаунчер распаковывает встроенное приложение в
`%LOCALAPPDATA%\fsclient\1.1.0\app\` и запускает его оттуда. EXE сам запросит
права администратора через UAC.

Для разработки (нужен .NET SDK 10 и уже собранная `cpp\fs_backend.dll`):

```bat
dotnet run
```

## Сборка

Нужны:

- **.NET SDK 10**;
- **MinGW-w64** (`g++`, `windres`) — ищется в `PATH`, либо в WinGet-пакете
  `BrechtSanders.WinLibs.POSIX.UCRT`.

Из корня репозитория:

```bat
build.bat
```

Скрипт последовательно:

1. Компилирует `cpp\backend.cpp` → `cpp\fs_backend.dll`.
2. Публикует framework-dependent приложение во временную папку `build-publish\`.
3. Вшивает все файлы приложения ресурсами в `launcher.cpp` и собирает
   единственный `dist\fsclient.exe` — с проверкой .NET 10 и переходом на
   официальный сайт, если runtime отсутствует.

Результат — один `dist\fsclient.exe` для публикации в релизе.

## Структура

```text
launcher.cpp                    Единый EXE: проверка .NET 10 и распаковка приложения
App.xaml(.cs)                   Запуск, single instance, тема
MainWindow.xaml(.cs)            Интерфейс, трей, логика состояний
Backend.cs                      P/Invoke, GitHub API, стратегии и служба
BundledResources.cs             Резервная распаковка DLL/иконки
DarkTrayMenuRenderer.cs         Кастомное меню в трее
FrostedControls.cs              Матовое стекло и плавный скруглённый прогресс
Styles.xaml                     Палитра и стили
StrategyDialog / ConfirmDialog  Модальные окна
app.manifest                    Права администратора, DPI
fsclient.csproj                 Проект .NET 10 WPF (версия 1.1.0)
cpp/                            Исходники бэкенда (DLL создаётся сборкой)
assets/                         Фон, иконка, скриншот
build.bat                       Сборка DLL, публикация и единого EXE
dist/                           Один fsclient.exe (не хранится в git)
```

## Что не лежит в репозитории

`bin/`, `obj/`, `dist/`, `build-publish/`, `launcher.rc`, `launcher.res`,
`cpp/*.a`, `cpp/fs_backend.dll`, `releases/`, `plugins/` — генерируется
локально или скачивается лаунчером.
