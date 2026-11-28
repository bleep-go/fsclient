// fsclient 1.4.0 — единый EXE.
// Снаружи: проверка .NET 10 Windows Desktop Runtime.
// Внутри: встроенное WPF-приложение fsclient-core.dll и все его файлы.
// Если runtime есть — один раз распаковываем приложение в Program Files
// и запускаем оттуда. Если нет — сообщение и переход на страницу скачивания.
// Распаковка не идёт в %LOCALAPPDATA%: это снижает ложные срабатывания
// антивирусов, которые считают такие "dropper"-схемы подозрительными.

#define UNICODE
#define _UNICODE

#include <windows.h>
#include <shellapi.h>
#include <shlobj.h>
#include <stdlib.h>
#include <cstdint>
#include <cstdio>
#include <cwchar>
#include <cwctype>
#include <string>
#include <vector>

static const wchar_t* kDownloadUrl =
    L"https://dotnet.microsoft.com/download/dotnet/10.0";

static const wchar_t* kVersion = L"1.4.0";

// Планировщик добавляет эту метку в командную строку launcher.exe.
// Ядро использует её, чтобы отличить автозапуск от ручного открытия.
static const wchar_t* kScheduledArg = L"--scheduled";

static bool HasScheduledArg()
{
    std::wstring commandLine = GetCommandLineW();
    size_t length = wcslen(kScheduledArg);
    for (size_t i = 0; i + length <= commandLine.size(); ++i)
    {
        bool same = true;
        for (size_t j = 0; j < length; ++j)
        {
            if (std::towlower(commandLine[i + j]) != kScheduledArg[j])
            {
                same = false;
                break;
            }
        }
        if (!same)
            continue;

        bool leftEdge = i == 0 || commandLine[i - 1] == L' ' ||
                        commandLine[i - 1] == L'\t' || commandLine[i - 1] == L'"';
        size_t end = i + length;
        bool rightEdge = end == commandLine.size() || commandLine[end] == L' ' ||
                         commandLine[end] == L'\t' || commandLine[end] == L'"';
        if (leftEdge && rightEdge)
            return true;
    }
    return false;
}

struct EmbeddedFile
{
    int id;
    const wchar_t* relativePath;
};

static const EmbeddedFile kFiles[] =
{
    { 101, L"fsclient-core.exe" },
    { 102, L"fsclient-core.dll" },
    { 103, L"fsclient-core.deps.json" },
    { 104, L"fsclient-core.runtimeconfig.json" },
    { 105, L"cpp\\fs_backend.dll" },
    { 106, L"assets\\fsclient.ico" },
    { 107, L"updater.exe" },
};

static bool HasDesktopRuntime10(const std::wstring& dotnetRoot)
{
    std::wstring pattern = dotnetRoot + L"\\shared\\Microsoft.WindowsDesktop.App\\10.*";
    WIN32_FIND_DATAW findData{};
    HANDLE handle = FindFirstFileW(pattern.c_str(), &findData);
    if (handle == INVALID_HANDLE_VALUE)
        return false;

    bool found = false;
    do
    {
        if ((findData.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) != 0)
        {
            found = true;
            break;
        }
    } while (FindNextFileW(handle, &findData));

    FindClose(handle);
    return found;
}

static bool IsNet10Installed()
{
    const wchar_t* roots[] =
    {
        _wgetenv(L"DOTNET_ROOT"),
        _wgetenv(L"ProgramFiles"),
        _wgetenv(L"ProgramFiles(x86)"),
        _wgetenv(L"LOCALAPPDATA"),
    };
    const wchar_t* suffixes[] =
    {
        L"",
        L"\\dotnet",
        L"\\dotnet",
        L"\\Microsoft\\dotnet",
    };

    for (size_t i = 0; i < 4; ++i)
    {
        if (roots[i] == nullptr || *roots[i] == L'\0')
            continue;
        if (HasDesktopRuntime10(std::wstring(roots[i]) + suffixes[i]))
            return true;
    }

    return false;
}

static std::wstring AppRoot()
{
    const wchar_t* programFiles = _wgetenv(L"ProgramFiles");
    if (programFiles != nullptr && *programFiles != L'\0')
        return std::wstring(programFiles) + L"\\fsclient\\app";

    // Запасной вариант для систем без ProgramFiles.
    const wchar_t* local = _wgetenv(L"LOCALAPPDATA");
    if (local != nullptr && *local != L'\0')
        return std::wstring(local) + L"\\Programs\\fsclient\\app";

    wchar_t temp[MAX_PATH]{};
    GetTempPathW(MAX_PATH, temp);
    return std::wstring(temp) + L"fsclient\\app";
}

static std::string VersionString()
{
    std::string version;
    for (size_t i = 0; kVersion[i] != L'\0'; ++i)
        version.push_back(static_cast<char>(kVersion[i]));
    return version;
}

// Отпечаток встроенного приложения. Версия может не меняться, а содержимое
//.dll — уже другое (исправления в рамках одного релиза), поэтому одного
// номера версии недостаточно: иначе распаковщик пропустит обновление.
static std::string AppFingerprint()
{
    constexpr uint64_t kOffset = 1469598103934665603ULL;
    constexpr uint64_t kPrime = 1099511628211ULL;
    uint64_t hash = kOffset;

    for (const auto& file : kFiles)
    {
        HRSRC resource = FindResourceW(nullptr, MAKEINTRESOURCEW(file.id), RT_RCDATA);
        if (resource == nullptr)
            continue;

        HGLOBAL global = LoadResource(nullptr, resource);
        if (global == nullptr)
            continue;

        DWORD size = SizeofResource(nullptr, resource);
        const unsigned char* data = static_cast<const unsigned char*>(LockResource(global));
        if (data == nullptr || size == 0)
            continue;

        hash ^= size;
        hash *= kPrime;
        for (DWORD i = 0; i < size; ++i)
        {
            hash ^= data[i];
            hash *= kPrime;
        }
    }

    char buffer[96]{};
    snprintf(buffer, sizeof(buffer), "%s|%016llX",
        VersionString().c_str(), static_cast<unsigned long long>(hash));
    return buffer;
}

static std::wstring MarkerPath(const std::wstring& root)
{
    return root + L"\\install.ok";
}

static bool MarkerMatches(const std::wstring& root)
{
    HANDLE file = CreateFileW(MarkerPath(root).c_str(), GENERIC_READ, FILE_SHARE_READ,
        nullptr, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (file == INVALID_HANDLE_VALUE)
        return false;

    char buffer[128]{};
    DWORD read = 0;
    BOOL ok = ReadFile(file, buffer, sizeof(buffer) - 1, &read, nullptr);
    CloseHandle(file);

    std::string expected = AppFingerprint();
    return ok != FALSE && read >= expected.size() &&
        std::string(buffer, read).substr(0, expected.size()) == expected;
}

static bool WriteMarker(const std::wstring& root)
{
    HANDLE file = CreateFileW(MarkerPath(root).c_str(), GENERIC_WRITE, 0, nullptr,
        CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (file == INVALID_HANDLE_VALUE)
        return false;

    std::string fingerprint = AppFingerprint();

    DWORD written = 0;
    BOOL ok = WriteFile(file, fingerprint.data(), static_cast<DWORD>(fingerprint.size()), &written, nullptr);
    CloseHandle(file);
    return ok != FALSE && written == fingerprint.size();
}

static bool WriteResource(int id, const std::wstring& target, bool force)
{
    HRSRC resource = FindResourceW(nullptr, MAKEINTRESOURCEW(id), RT_RCDATA);
    if (resource == nullptr)
        return false;

    HGLOBAL global = LoadResource(nullptr, resource);
    if (global == nullptr)
        return false;

    DWORD size = SizeofResource(nullptr, resource);
    void* data = LockResource(global);
    if (data == nullptr || size == 0)
        return false;

    WIN32_FILE_ATTRIBUTE_DATA attributes{};
    if (!force &&
        GetFileAttributesExW(target.c_str(), GetFileExInfoStandard, &attributes) &&
        attributes.nFileSizeLow == size && attributes.nFileSizeHigh == 0)
    {
        return true; // Файл уже распакован и совпадает по размеру.
    }

    HANDLE file = CreateFileW(
        target.c_str(), GENERIC_WRITE, 0, nullptr,
        CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (file == INVALID_HANDLE_VALUE)
        return false;

    DWORD written = 0;
    BOOL ok = WriteFile(file, data, size, &written, nullptr);
    CloseHandle(file);
    return ok != FALSE && written == size;
}

static bool ExtractApp(const std::wstring& root)
{
    // При смене содержимого перезаписываем все файлы, даже если размер совпал.
    bool force = !MarkerMatches(root);

    SHCreateDirectoryExW(nullptr, root.c_str(), nullptr);
    SHCreateDirectoryExW(nullptr, (root + L"\\cpp").c_str(), nullptr);
    SHCreateDirectoryExW(nullptr, (root + L"\\assets").c_str(), nullptr);

    for (const auto& file : kFiles)
    {
        if (!WriteResource(file.id, root + L"\\" + file.relativePath, force))
            return false;
    }
    return WriteMarker(root);
}

int WINAPI WinMain(HINSTANCE, HINSTANCE, LPSTR, int)
{
    if (!IsNet10Installed())
    {
        MessageBoxW(
            nullptr,
            L"Для работы fsclient необходим .NET 10 (Windows Desktop Runtime).\n\n"
            L"Сейчас откроется официальная страница скачивания.",
            L"fsclient 1.4.0 - требуется .NET 10",
            MB_ICONWARNING | MB_OK);

        ShellExecuteW(nullptr, L"open", kDownloadUrl, nullptr, nullptr, SW_SHOWNORMAL);
        return 1;
    }

    std::wstring root = AppRoot();
    if (!ExtractApp(root))
    {
        MessageBoxW(
            nullptr,
            L"Не удалось установить встроенное приложение.\n"
            L"Подтвердите права администратора в окне UAC и проверьте,\n"
            L"доступна ли для записи папка Program Files\\fsclient.",
            L"fsclient 1.4.0",
            MB_ICONERROR | MB_OK);
        return 1;
    }

    wchar_t modulePath[MAX_PATH]{};
    GetModuleFileNameW(nullptr, modulePath, MAX_PATH);
    std::wstring launcherPath = modulePath;
    // updater.exe распакован из ресурса 107 рядом с ядром в папке приложения.
    std::wstring updaterPath = root + L"\\updater.exe";

    std::wstring executable = root + L"\\fsclient-core.exe";
    std::wstring commandLine = L"\"" + executable + L"\" --launcher=\"" + launcherPath +
        L"\" --updater=\"" + updaterPath + L"\"";
    if (HasScheduledArg())
        commandLine += std::wstring(L" ") + kScheduledArg;
    std::vector<wchar_t> command(commandLine.begin(), commandLine.end());
    command.push_back(L'\0');

    STARTUPINFOW startup{};
    startup.cb = sizeof(startup);
    PROCESS_INFORMATION process{};

    if (!CreateProcessW(
            executable.c_str(), command.data(), nullptr, nullptr, FALSE,
            0, nullptr, root.c_str(), &startup, &process))
    {
        MessageBoxW(
            nullptr,
            L"Не удалось запустить лаунчер. Подтвердите запрос прав\n"
            L"администратора в окне UAC или проверьте антивирус.",
            L"fsclient 1.4.0",
            MB_ICONERROR | MB_OK);
        return 1;
    }

    CloseHandle(process.hThread);
    CloseHandle(process.hProcess);

    return 0;
}
