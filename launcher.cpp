// fsclient 1.2.0 — единый EXE.
// Снаружи: проверка .NET 10 Windows Desktop Runtime.
// Внутри: встроенное WPF-приложение fsclient-core.dll и все его файлы.
// Если runtime есть — распаковываем приложение в %LOCALAPPDATA% и запускаем.
// Если нет — сообщение и переход на официальную страницу скачивания.

#define UNICODE
#define _UNICODE

#include <windows.h>
#include <shellapi.h>
#include <shlobj.h>
#include <stdlib.h>
#include <string>

static const wchar_t* kDownloadUrl =
    L"https://dotnet.microsoft.com/download/dotnet/10.0";

static const wchar_t* kVersion = L"1.2.0";

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
    const wchar_t* local = _wgetenv(L"LOCALAPPDATA");
    if (local != nullptr && *local != L'\0')
        return std::wstring(local) + L"\\fsclient\\" + kVersion + L"\\app";

    wchar_t temp[MAX_PATH]{};
    GetTempPathW(MAX_PATH, temp);
    return std::wstring(temp) + L"fsclient\\" + kVersion + L"\\app";
}

static bool WriteResource(int id, const std::wstring& target)
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
    if (GetFileAttributesExW(target.c_str(), GetFileExInfoStandard, &attributes) &&
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
    SHCreateDirectoryExW(nullptr, (root + L"\\cpp").c_str(), nullptr);
    SHCreateDirectoryExW(nullptr, (root + L"\\assets").c_str(), nullptr);

    for (const auto& file : kFiles)
    {
        if (!WriteResource(file.id, root + L"\\" + file.relativePath))
            return false;
    }
    return true;
}

int WINAPI WinMain(HINSTANCE, HINSTANCE, LPSTR, int)
{
    if (!IsNet10Installed())
    {
        MessageBoxW(
            nullptr,
            L"Для работы fsclient необходим .NET 10 (Windows Desktop Runtime).\n\n"
            L"Сейчас откроется официальная страница скачивания.",
            L"fsclient 1.2.0 - требуется .NET 10",
            MB_ICONWARNING | MB_OK);

        ShellExecuteW(nullptr, L"open", kDownloadUrl, nullptr, nullptr, SW_SHOWNORMAL);
        return 1;
    }

    std::wstring root = AppRoot();
    if (!ExtractApp(root))
    {
        MessageBoxW(
            nullptr,
            L"Не удалось распаковать встроенное приложение.\n"
            L"Проверьте права на запись в папку %LOCALAPPDATA%\\fsclient.",
            L"fsclient 1.2.0",
            MB_ICONERROR | MB_OK);
        return 1;
    }

    std::wstring executable = root + L"\\fsclient-core.exe";
    HINSTANCE result = ShellExecuteW(
        nullptr, L"open", executable.c_str(), nullptr, root.c_str(), SW_SHOWNORMAL);

    if (reinterpret_cast<INT_PTR>(result) <= 32)
    {
        MessageBoxW(
            nullptr,
            L"Не удалось запустить лаунчер. Подтвердите запрос прав администратора\n"
            L"в окне UAC или проверьте антивирус.",
            L"fsclient 1.2.0",
            MB_ICONERROR | MB_OK);
        return 1;
    }

    return 0;
}
