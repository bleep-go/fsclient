// fsclient updater — заменяет fsclient.exe и перезапускает лаунчер.
// Запускается уже с правами администратора (родитель — лаунчер), поэтому
// собственный requireAdministrator-манифест ему не нужен.

#include <windows.h>
#include <shellapi.h>
#include <string>
#include <vector>

static std::wstring Unquote(std::wstring value)
{
    if (value.size() >= 2 && value.front() == L'"' && value.back() == L'"')
        return value.substr(1, value.size() - 2);
    return value;
}

static std::wstring ParentDir(const std::wstring& path)
{
    size_t slash = path.find_last_of(L"\\/");
    return slash == std::wstring::npos ? L"." : path.substr(0, slash);
}

static void Error(const wchar_t* message)
{
    MessageBoxW(nullptr, message, L"fsclient updater", MB_ICONERROR | MB_OK);
}

int WINAPI wWinMain(HINSTANCE, HINSTANCE, LPWSTR, int)
{
    std::wstring target;
    std::wstring downloaded;
    std::vector<DWORD> pids;

    int count = 0;
    LPWSTR* argv = CommandLineToArgvW(GetCommandLineW(), &count);
    if (argv != nullptr)
    {
        for (int i = 1; i < count; ++i)
        {
            std::wstring arg = argv[i];
            if (arg.rfind(L"--target=", 0) == 0)
                target = Unquote(arg.substr(9));
            else if (arg.rfind(L"--new=", 0) == 0)
                downloaded = Unquote(arg.substr(6));
            else if (arg.rfind(L"--waitpid=", 0) == 0)
            {
                DWORD pid = wcstoul(arg.substr(10).c_str(), nullptr, 10);
                if (pid != 0)
                    pids.push_back(pid);
            }
        }
        LocalFree(argv);
    }

    if (target.empty() || downloaded.empty())
    {
        Error(L"Не переданы пути для обновления. Запустите обновление через лаунчер.");
        return 1;
    }

    // Старый процесс должен выйти до замены его EXE.
    for (DWORD pid : pids)
    {
        HANDLE process = OpenProcess(SYNCHRONIZE | PROCESS_QUERY_LIMITED_INFORMATION,
            FALSE, pid);
        if (process == nullptr)
            continue;
        WaitForSingleObject(process, 30000);
        CloseHandle(process);
    }

    // На случай, если файл всё ещё занят антивирусом или другим процессом.
    for (int attempt = 0; attempt < 60; ++attempt)
    {
        HANDLE file = CreateFileW(target.c_str(), GENERIC_WRITE, 0, nullptr,
            OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr);
        if (file != INVALID_HANDLE_VALUE)
        {
            CloseHandle(file);
            break;
        }
        Sleep(500);
    }

    std::wstring staging = target + L".new";
    std::wstring backup = target + L".bak";
    DeleteFileW(staging.c_str());

    if (!CopyFileW(downloaded.c_str(), staging.c_str(), FALSE))
    {
        Error(L"Не удалось подготовить новый fsclient.exe.");
        return 1;
    }

    bool hasBackup = false;
    if (GetFileAttributesW(target.c_str()) != INVALID_FILE_ATTRIBUTES)
    {
        DeleteFileW(backup.c_str());
        hasBackup = MoveFileW(target.c_str(), backup.c_str()) != FALSE;
    }

    if (!MoveFileExW(staging.c_str(), target.c_str(),
            MOVEFILE_REPLACE_EXISTING | MOVEFILE_WRITE_THROUGH))
    {
        if (hasBackup)
            MoveFileW(backup.c_str(), target.c_str());
        Error(L"Не удалось заменить fsclient.exe. Старая версия восстановлена.");
        return 1;
    }

    if (hasBackup)
        DeleteFileW(backup.c_str());

    std::wstring command = L"\"" + target + L"\"";
    std::vector<wchar_t> mutableCommand(command.begin(), command.end());
    mutableCommand.push_back(L'\0');

    STARTUPINFOW startup{};
    startup.cb = sizeof(startup);
    PROCESS_INFORMATION process{};

    if (!CreateProcessW(target.c_str(), mutableCommand.data(), nullptr, nullptr, FALSE,
            0, nullptr, ParentDir(target).c_str(), &startup, &process))
    {
        Error(L"Обновление установлено, но не удалось перезапустить лаунчер. Запустите fsclient.exe вручную.");
        return 1;
    }

    CloseHandle(process.hThread);
    CloseHandle(process.hProcess);

    DeleteFileW(downloaded.c_str());
    RemoveDirectoryW(ParentDir(downloaded).c_str());
    return 0;
}
