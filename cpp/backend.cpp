// fsclient native backend: real process/service control for zapret.
// Build with build.bat (MinGW-w64, UCRT, statically linked runtime).
#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#include <tlhelp32.h>
#include <winsvc.h>
#include <shellapi.h>
#include <shlobj.h>

#include <algorithm>
#include <atomic>
#include <cstdint>
#include <cstdio>
#include <cstring>
#include <cwchar>
#include <cwctype>
#include <fstream>
#include <mutex>
#include <string>
#include <thread>
#include <vector>

#include "backend.h"

namespace {

std::mutex g_cb_mutex;
FSLogCallback g_log_cb = nullptr;
FSStateCallback g_state_cb = nullptr;

std::mutex g_runtime_mutex;
HANDLE g_job = nullptr;
HANDLE g_process = nullptr;
HANDLE g_read_pipe = nullptr;
DWORD g_pid = 0;
std::wstring g_active_strategy;
std::thread g_reader;

// TG WS Proxy has its own lifecycle: it must never change the zapret state.
std::mutex g_tgws_mutex;
HANDLE g_tgws_job = nullptr;
HANDLE g_tgws_process = nullptr;
HANDLE g_tgws_read_pipe = nullptr;
DWORD g_tgws_pid = 0;
std::wstring g_tgws_exe;
std::wstring g_tgws_dir;
std::thread g_tgws_reader;

std::mutex g_error_mutex;
std::string g_last_error;

HANDLE g_single_mutex = nullptr;
std::atomic<bool> g_initialized{false};

// ---------- small helpers ----------

void copy_out(char *out, int out_len, const std::string &value) {
    if (!out || out_len <= 0) {
        return;
    }
    std::snprintf(out, static_cast<size_t>(out_len), "%s", value.c_str());
}

void set_error(const std::string &message) {
    std::lock_guard<std::mutex> lock(g_error_mutex);
    g_last_error = message;
}

std::string last_error_text() {
    std::lock_guard<std::mutex> lock(g_error_mutex);
    return g_last_error;
}

std::string system_error_text(DWORD code) {
    wchar_t *raw = nullptr;
    FormatMessageW(
        FORMAT_MESSAGE_ALLOCATE_BUFFER | FORMAT_MESSAGE_FROM_SYSTEM |
            FORMAT_MESSAGE_IGNORE_INSERTS,
        nullptr, code, MAKELANGID(LANG_NEUTRAL, SUBLANG_DEFAULT),
        reinterpret_cast<LPWSTR>(&raw), 0, nullptr
    );
    std::string result;
    if (raw) {
        int size = WideCharToMultiByte(CP_UTF8, 0, raw, -1, nullptr, 0,
                                       nullptr, nullptr);
        if (size > 0) {
            result.resize(static_cast<size_t>(size - 1));
            WideCharToMultiByte(CP_UTF8, 0, raw, -1, &result[0], size,
                                nullptr, nullptr);
        }
        LocalFree(raw);
    }
    if (result.empty()) {
        result = "Код ошибки Windows: " + std::to_string(code);
    }
    return result;
}

std::string to_utf8(const std::wstring &value) {
    if (value.empty()) {
        return std::string();
    }
    int size = WideCharToMultiByte(CP_UTF8, 0, value.c_str(),
                                   static_cast<int>(value.size()), nullptr, 0,
                                   nullptr, nullptr);
    if (size <= 0) {
        return std::string();
    }
    std::string result(static_cast<size_t>(size), '\0');
    WideCharToMultiByte(CP_UTF8, 0, value.c_str(),
                        static_cast<int>(value.size()), &result[0], size,
                        nullptr, nullptr);
    return result;
}

std::wstring lower_copy(std::wstring value) {
    for (wchar_t &ch : value) {
        ch = static_cast<wchar_t>(std::towlower(ch));
    }
    return value;
}

bool contains_ci(const std::wstring &haystack, const wchar_t *needle) {
    std::wstring h = lower_copy(haystack);
    std::wstring n = lower_copy(std::wstring(needle));
    return h.find(n) != std::wstring::npos;
}

bool starts_with(const std::wstring &value, const std::wstring &prefix) {
    return value.size() >= prefix.size() &&
           value.compare(0, prefix.size(), prefix) == 0;
}

std::wstring join_path(const std::wstring &dir, const std::wstring &name) {
    if (name.empty()) {
        return dir;
    }
    if (name.size() >= 2 && name[1] == L':') {
        return name;
    }
    if (starts_with(name, L"\\\\")) {
        return name;
    }
    if (dir.empty()) {
        return name;
    }
    if (dir.back() == L'\\' || dir.back() == L'/') {
        return dir + name;
    }
    return dir + L"\\" + name;
}

void replace_all_ci(std::wstring &value, const std::wstring &from,
                    const std::wstring &to) {
    if (from.empty()) {
        return;
    }
    std::wstring haystack = lower_copy(value);
    std::wstring needle = lower_copy(from);
    size_t pos = 0;
    while ((pos = haystack.find(needle, pos)) != std::wstring::npos) {
        value.replace(pos, from.size(), to);
        haystack.replace(pos, needle.size(), lower_copy(to));
        pos += to.size();
    }
}

std::wstring game_filter_value(const std::wstring &dir,
                               const wchar_t *variable) {
    const std::wstring default_tcp = L"12";
    const std::wstring default_udp = L"12";
    const std::wstring default_range = L"1024-65535";

    std::wstring tcp = default_tcp;
    std::wstring udp = default_udp;
    std::wstring tcp_range = default_range;
    std::wstring udp_range = default_range;
    std::wstring mode = L"disabled";

    std::wstring flag_path = join_path(dir, L"utils\\game_filter.enabled");
    std::ifstream file(flag_path.c_str(), std::ios::binary);
    if (file.is_open()) {
        std::string narrow;
        while (std::getline(file, narrow)) {
            if (!narrow.empty() && narrow.back() == '\r') {
                narrow.pop_back();
            }
            std::wstring line(narrow.begin(), narrow.end());
            size_t equals = line.find(L'=');
            if (equals == std::wstring::npos) {
                continue;
            }
            std::wstring key = lower_copy(line.substr(0, equals));
            std::wstring val = line.substr(equals + 1);
            if (key == L"mode") {
                mode = lower_copy(val);
            } else if (key == L"all") {
                mode = L"all";
            } else if (key == L"tcp") {
                if (val.empty()) {
                    mode = L"tcp";
                } else {
                    tcp_range = val;
                }
            } else if (key == L"udp") {
                if (val.empty()) {
                    mode = L"udp";
                } else {
                    udp_range = val;
                }
            }
        }
    }

    if (mode == L"all") {
        tcp = tcp_range;
        udp = udp_range;
    } else if (mode == L"tcp") {
        tcp = tcp_range;
        udp = default_udp;
    } else if (mode == L"udp") {
        tcp = default_tcp;
        udp = udp_range;
    }

    if (wcscmp(variable, L"%GameFilterTCP%") == 0) {
        return tcp;
    }
    if (wcscmp(variable, L"%GameFilterUDP%") == 0) {
        return udp;
    }
    return L"12";
}

std::wstring expand_batch_variables(const std::wstring &dir,
                                    const std::wstring &value) {
    std::wstring result = value;
    std::wstring root = dir;
    while (!root.empty() && (root.back() == L'\\' || root.back() == L'/')) {
        root.pop_back();
    }
    replace_all_ci(result, L"%~dp0", root + L"\\");
    replace_all_ci(result, L"%BIN%", root + L"\\bin\\");
    replace_all_ci(result, L"%LISTS%", root + L"\\lists\\");
    replace_all_ci(result, L"%GameFilterTCP%",
                   game_filter_value(dir, L"%GameFilterTCP%"));
    replace_all_ci(result, L"%GameFilterUDP%",
                   game_filter_value(dir, L"%GameFilterUDP%"));
    replace_all_ci(result, L"%GameFilter%", L"12");
    return result;
}

void emit_log(int level, const std::string &message) {
    FSLogCallback callback = nullptr;
    {
        std::lock_guard<std::mutex> lock(g_cb_mutex);
        callback = g_log_cb;
    }
    if (callback && g_initialized.load()) {
        callback(level, message.c_str());
    }
}

void emit_state(int state, const std::string &detail) {
    FSStateCallback callback = nullptr;
    {
        std::lock_guard<std::mutex> lock(g_cb_mutex);
        callback = g_state_cb;
    }
    if (callback && g_initialized.load()) {
        callback(state, detail.c_str());
    }
}

// ---------- privilege / instance helpers ----------

bool is_admin_process() {
    HANDLE token = nullptr;
    if (!OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, &token)) {
        return false;
    }
    TOKEN_ELEVATION elevation{};
    DWORD size = sizeof(elevation);
    BOOL ok = GetTokenInformation(token, TokenElevation, &elevation, size, &size);
    CloseHandle(token);
    return ok && elevation.TokenIsElevated != 0;
}

// ---------- process / service inspection ----------

struct ProcessMatch {
    bool found = false;
    DWORD pid = 0;
};

ProcessMatch find_process(const wchar_t *image_name) {
    ProcessMatch result;
    HANDLE snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
    if (snapshot == INVALID_HANDLE_VALUE) {
        return result;
    }
    PROCESSENTRY32W entry{};
    entry.dwSize = sizeof(entry);
    if (Process32FirstW(snapshot, &entry)) {
        do {
            if (_wcsicmp(entry.szExeFile, image_name) == 0) {
                result.found = true;
                result.pid = entry.th32ProcessID;
                break;
            }
        } while (Process32NextW(snapshot, &entry));
    }
    CloseHandle(snapshot);
    return result;
}

void terminate_orphan_winws() {
    HANDLE snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
    if (snapshot == INVALID_HANDLE_VALUE) {
        return;
    }
    PROCESSENTRY32W entry{};
    entry.dwSize = sizeof(entry);
    std::vector<DWORD> pids;
    if (Process32FirstW(snapshot, &entry)) {
        do {
            if (_wcsicmp(entry.szExeFile, L"winws.exe") == 0) {
                pids.push_back(entry.th32ProcessID);
            }
        } while (Process32NextW(snapshot, &entry));
    }
    CloseHandle(snapshot);

    for (DWORD pid : pids) {
        HANDLE process = OpenProcess(PROCESS_TERMINATE, FALSE, pid);
        if (process) {
            TerminateProcess(process, 0);
            CloseHandle(process);
            emit_log(1, "Завершён осиротевший процесс winws.exe (PID " +
                           std::to_string(pid) + ")");
        }
    }
}

bool service_running_by_name(const wchar_t *name) {
    SC_HANDLE scm = OpenSCManagerW(nullptr, nullptr, SC_MANAGER_CONNECT);
    if (!scm) {
        return false;
    }
    SC_HANDLE service = OpenServiceW(scm, name, SERVICE_QUERY_STATUS);
    if (!service) {
        CloseServiceHandle(scm);
        return false;
    }
    SERVICE_STATUS status{};
    bool running = QueryServiceStatus(service, &status) &&
                   (status.dwCurrentState == SERVICE_RUNNING ||
                    status.dwCurrentState == SERVICE_START_PENDING);
    CloseServiceHandle(service);
    CloseServiceHandle(scm);
    return running;
}

bool find_running_zapret_service(std::wstring &out_name) {
    static const wchar_t *known[] = {
        L"zapret", L"winws", L"zapret-discord-youtube"
    };
    for (const wchar_t *name : known) {
        if (service_running_by_name(name)) {
            out_name = name;
            return true;
        }
    }

    SC_HANDLE scm = OpenSCManagerW(nullptr, nullptr,
                                   SC_MANAGER_ENUMERATE_SERVICE);
    if (!scm) {
        return false;
    }

    std::vector<BYTE> buffer(16384);
    DWORD resume = 0;
    for (;;) {
        DWORD needed = 0;
        DWORD returned = 0;
        BOOL ok = EnumServicesStatusExW(
            scm, SC_ENUM_PROCESS_INFO, SERVICE_WIN32, SERVICE_STATE_ALL,
            buffer.data(), static_cast<DWORD>(buffer.size()), &needed,
            &returned, &resume, nullptr
        );
        if (!ok) {
            if (GetLastError() == ERROR_MORE_DATA && needed > buffer.size()) {
                buffer.resize(static_cast<size_t>(needed) + 1024);
                continue;
            }
            break;
        }

        auto *items = reinterpret_cast<ENUM_SERVICE_STATUS_PROCESSW *>(
            buffer.data()
        );
        for (DWORD i = 0; i < returned; ++i) {
            std::wstring name = items[i].lpServiceName ? items[i].lpServiceName : L"";
            std::wstring display = items[i].lpDisplayName ? items[i].lpDisplayName : L"";
            DWORD state = items[i].ServiceStatusProcess.dwCurrentState;
            if ((contains_ci(name, L"zapret") || contains_ci(name, L"winws") ||
                 contains_ci(display, L"zapret") || contains_ci(display, L"winws")) &&
                (state == SERVICE_RUNNING || state == SERVICE_START_PENDING)) {
                out_name = name;
                CloseServiceHandle(scm);
                return true;
            }
        }
        if (returned == 0 || resume == 0) {
            break;
        }
    }

    CloseServiceHandle(scm);
    return false;
}

bool service_exists(const wchar_t *name) {
    SC_HANDLE scm = OpenSCManagerW(nullptr, nullptr, SC_MANAGER_CONNECT);
    if (!scm) {
        return false;
    }
    SC_HANDLE service = OpenServiceW(scm, name, SERVICE_QUERY_CONFIG);
    bool exists = service != nullptr;
    if (service) {
        CloseServiceHandle(service);
    }
    CloseServiceHandle(scm);
    return exists;
}

std::wstring installed_strategy_from_registry() {
    HKEY key = nullptr;
    if (RegOpenKeyExW(HKEY_LOCAL_MACHINE,
                      L"SYSTEM\\CurrentControlSet\\Services\\zapret",
                      0, KEY_READ, &key) != ERROR_SUCCESS) {
        return std::wstring();
    }
    wchar_t value[512] = {};
    DWORD size = sizeof(value);
    DWORD type = 0;
    LONG result = RegQueryValueExW(key, L"zapret-discord-youtube", nullptr,
                                   &type, reinterpret_cast<LPBYTE>(value),
                                   &size);
    RegCloseKey(key);
    if (result != ERROR_SUCCESS || value[0] == L'\0') {
        return std::wstring();
    }
    return std::wstring(value);
}

int get_state_impl(std::string &detail) {
    std::wstring service_name;
    if (find_running_zapret_service(service_name)) {
        std::wstring strategy = installed_strategy_from_registry();
        detail = to_utf8(service_name);
        if (!strategy.empty()) {
            detail += " (" + to_utf8(strategy) + ")";
        }
        return FS_STATE_RUNNING_SERVICE;
    }

    ProcessMatch process = find_process(L"winws.exe");
    if (process.found) {
        std::lock_guard<std::mutex> lock(g_runtime_mutex);
        detail = "winws.exe (PID " + std::to_string(process.pid) + ")";
        if (!g_active_strategy.empty()) {
            detail += " — " + to_utf8(g_active_strategy);
        }
        return FS_STATE_RUNNING_PROCESS;
    }

    detail.clear();
    return FS_STATE_IDLE;
}

// ---------- service helpers ----------

bool stop_service_internal(const wchar_t *name) {
    SC_HANDLE scm = OpenSCManagerW(nullptr, nullptr, SC_MANAGER_CONNECT);
    if (!scm) {
        return false;
    }
    SC_HANDLE service = OpenServiceW(scm, name, SERVICE_STOP | SERVICE_QUERY_STATUS);
    if (!service) {
        CloseServiceHandle(scm);
        return false;
    }
    SERVICE_STATUS status{};
    ControlService(service, SERVICE_CONTROL_STOP, &status);
    for (int i = 0; i < 40; ++i) {
        if (!QueryServiceStatus(service, &status) ||
            status.dwCurrentState == SERVICE_STOPPED) {
            break;
        }
        Sleep(250);
    }
    CloseServiceHandle(service);
    CloseServiceHandle(scm);
    return true;
}

bool delete_service_internal(const wchar_t *name) {
    SC_HANDLE scm = OpenSCManagerW(nullptr, nullptr, SC_MANAGER_CONNECT);
    if (!scm) {
        return false;
    }
    SC_HANDLE service = OpenServiceW(scm, name, DELETE);
    if (!service) {
        CloseServiceHandle(scm);
        return false;
    }
    bool ok = DeleteService(service) != FALSE;
    CloseServiceHandle(service);
    CloseServiceHandle(scm);
    return ok;
}

bool service_binary_belongs_to_zapret(SC_HANDLE service) {
    DWORD needed = 0;
    QueryServiceConfigW(service, nullptr, 0, &needed);
    if (needed == 0) {
        return false;
    }
    std::vector<BYTE> buffer(needed);
    auto *config = reinterpret_cast<QUERY_SERVICE_CONFIGW *>(buffer.data());
    if (!QueryServiceConfigW(service, config, needed, &needed)) {
        return false;
    }
    std::wstring path = config->lpBinaryPathName ? config->lpBinaryPathName : L"";
    return contains_ci(path, L"zapret") || contains_ci(path, L"winws");
}

std::wstring service_binary_path(const wchar_t *name) {
    SC_HANDLE scm = OpenSCManagerW(nullptr, nullptr, SC_MANAGER_CONNECT);
    if (!scm) {
        return std::wstring();
    }
    SC_HANDLE service = OpenServiceW(scm, name, SERVICE_QUERY_CONFIG);
    std::wstring result;
    if (service) {
        DWORD needed = 0;
        QueryServiceConfigW(service, nullptr, 0, &needed);
        if (needed > 0) {
            std::vector<BYTE> buffer(needed);
            auto *config = reinterpret_cast<QUERY_SERVICE_CONFIGW *>(buffer.data());
            if (QueryServiceConfigW(service, config, needed, &needed) &&
                config->lpBinaryPathName) {
                result = config->lpBinaryPathName;
            }
        }
        CloseServiceHandle(service);
    }
    CloseServiceHandle(scm);
    return result;
}

// ---------- strategy argument parsing ----------

// ---------- strategy command parsing ----------

std::wstring trim_right(const std::wstring &value) {
    size_t end = value.size();
    while (end > 0 && (value[end - 1] == L' ' || value[end - 1] == L'\t')) {
        --end;
    }
    return value.substr(0, end);
}

// Splits a command line into tokens but keeps quotes as part of each token,
// so paths with spaces survive variable expansion and CreateProcessW.
std::vector<std::wstring> split_command_line(const std::wstring &line) {
    std::vector<std::wstring> result;
    std::wstring current;
    bool quoted = false;
    bool has_content = false;
    for (wchar_t ch : line) {
        if (ch == L'"') {
            quoted = !quoted;
            current.push_back(ch);
            has_content = true;
            continue;
        }
        if (!quoted && (ch == L' ' || ch == L'\t')) {
            if (has_content) {
                result.push_back(current);
                current.clear();
                has_content = false;
            }
            continue;
        }
        current.push_back(ch);
        has_content = true;
    }
    if (has_content) {
        result.push_back(current);
    }
    return result;
}

std::wstring unquote(const std::wstring &value) {
    if (value.size() >= 2 && value.front() == L'"' && value.back() == L'"') {
        return value.substr(1, value.size() - 2);
    }
    return value;
}

// В батниках Flowseal caret экранирует следующие символы, например ^!
// должен дойти до winws.exe как ordinary '!'. Внутри кавычек caret — обычный
// символ, поэтому трогаем только текст вне кавычек.
std::wstring unescape_batch_caret(const std::wstring &value) {
    std::wstring result;
    bool quoted = false;
    for (size_t i = 0; i < value.size(); ++i) {
        wchar_t ch = value[i];
        if (ch == L'"') {
            quoted = !quoted;
            result.push_back(ch);
            continue;
        }
        if (!quoted && ch == L'^' && i + 1 < value.size()) {
            result.push_back(value[++i]);
            continue;
        }
        result.push_back(ch);
    }
    return result;
}

bool is_absolute_path(const std::wstring &value) {
    return (value.size() >= 2 && value[1] == L':') ||
           starts_with(value, L"\\\\");
}

std::wstring normalize_arg(const std::wstring &dir,
                           const std::wstring &token_raw) {
    std::wstring token = expand_batch_variables(
        dir, unescape_batch_caret(token_raw));
    if (token.find(L'"') != std::wstring::npos) {
        return token;
    }

    size_t equals = token.find(L'=');
    std::wstring prefix = equals == std::wstring::npos
                              ? std::wstring()
                              : token.substr(0, equals + 1);
    std::wstring value = equals == std::wstring::npos
                             ? token
                             : token.substr(equals + 1);
    if (value.find(L'\\') != std::wstring::npos && !is_absolute_path(value)) {
        value = join_path(dir, value);
    }

    std::wstring result = prefix + value;
    if (result.find(L' ') != std::wstring::npos) {
        return L"\"" + result + L"\"";
    }
    return result;
}

bool ends_with_ci(const std::wstring &value, const wchar_t *suffix) {
    size_t length = wcslen(suffix);
    return value.size() >= length &&
           _wcsicmp(value.c_str() + value.size() - length, suffix) == 0;
}

bool create_file_if_missing(const std::wstring &path, const char *content,
                            bool &created) {
    created = false;
    size_t slash = path.find_last_of(L"\\/");
    if (slash != std::wstring::npos && slash > 0) {
        SHCreateDirectoryExW(nullptr, path.substr(0, slash).c_str(), nullptr);
    }

    HANDLE file = CreateFileW(path.c_str(), GENERIC_WRITE, 0, nullptr,
                              CREATE_NEW, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (file == INVALID_HANDLE_VALUE) {
        // Файл уже есть — пользовательские правки трогать нельзя.
        return GetLastError() == ERROR_FILE_EXISTS;
    }
    created = true;
    DWORD length = static_cast<DWORD>(strlen(content));
    DWORD written = 0;
    BOOL ok = WriteFile(file, content, length, &written, nullptr);
    CloseHandle(file);
    return ok != FALSE && written == length;
}

// Оригинальные батники Flowseal перед запуском winws.exe вызывают
// service.bat load_user_lists, который создаёт *-user.txt, если их нет.
// Мы запускаем winws.exe напрямую, поэтому выполняем эту подготовку сами:
// иначе winws падает с "failed to register ipset '...ipset-exclude-user.txt'".
void ensure_user_lists(const std::wstring &args) {
    for (const std::wstring &token : split_command_line(args)) {
        std::wstring value = unquote(token);
        size_t equals = value.find(L'=');
        if (equals == std::wstring::npos) {
            continue;
        }

        std::wstring option = lower_copy(value.substr(0, equals));
        bool is_ipset = option.find(L"ipset") != std::wstring::npos;
        bool is_hostlist = option.find(L"hostlist") != std::wstring::npos;
        if (!is_ipset && !is_hostlist) {
            continue;
        }

        std::wstring path = unquote(value.substr(equals + 1));
        if (!ends_with_ci(path, L"-user.txt")) {
            continue;
        }

        // Заглушки совпадают с теми, что пишет service.bat.
        const char *content = is_ipset
                                  ? "203.0.113.113/32\r\n"
                                  : "domain.example.abc\r\n";
        bool created = false;
        if (!create_file_if_missing(path, content, created)) {
            emit_log(1, std::string("Не удалось создать пользовательский список: ") +
                           to_utf8(path));
        } else if (created) {
            emit_log(0, std::string("Создан пользовательский список: ") + to_utf8(path));
        }
    }
}

bool parse_winws_command(const std::wstring &dir, const std::wstring &bat,
                         std::wstring &out_exe, std::wstring &out_args,
                         std::wstring &out_command) {
    std::wstring full = join_path(dir, bat);
    std::ifstream file(full.c_str(), std::ios::binary);
    if (!file.is_open()) {
        set_error("Не найден батник стратегии: " + to_utf8(full));
        return false;
    }

    std::vector<std::wstring> tokens;
    std::string narrow;
    while (std::getline(file, narrow)) {
        if (!narrow.empty() && narrow.back() == '\r') {
            narrow.pop_back();
        }
        std::wstring line(narrow.begin(), narrow.end());
        std::wstring trimmed = trim_right(line);
        bool continuation = !trimmed.empty() && trimmed.back() == L'^';
        if (continuation) {
            trimmed.pop_back();
        }
        std::vector<std::wstring> parts = split_command_line(trimmed);

        if (tokens.empty()) {
            size_t exe_index = std::wstring::npos;
            for (size_t i = 0; i < parts.size(); ++i) {
                if (contains_ci(parts[i], L"winws.exe")) {
                    exe_index = i;
                    break;
                }
            }
            if (exe_index == std::wstring::npos) {
                continue;
            }
            tokens.assign(parts.begin() + static_cast<ptrdiff_t>(exe_index),
                          parts.end());
        } else {
            for (const std::wstring &part : parts) {
                if (part == L"^") {
                    continue;
                }
                tokens.push_back(part);
            }
        }
        if (!continuation) {
            break;
        }
    }

    if (tokens.empty() || !contains_ci(tokens[0], L"winws.exe")) {
        set_error("Не удалось найти запуск winws.exe в " + to_utf8(bat));
        return false;
    }

    std::wstring exe = expand_batch_variables(
        dir, unescape_batch_caret(unquote(tokens[0])));
    while (!exe.empty() && exe.front() == L'@') {
        exe.erase(exe.begin());
    }
    exe = join_path(dir, exe);
    if (GetFileAttributesW(exe.c_str()) == INVALID_FILE_ATTRIBUTES) {
        std::wstring fallback = join_path(dir, L"bin\\winws.exe");
        if (GetFileAttributesW(fallback.c_str()) != INVALID_FILE_ATTRIBUTES) {
            exe = fallback;
        } else {
            fallback = join_path(dir, L"winws.exe");
            if (GetFileAttributesW(fallback.c_str()) == INVALID_FILE_ATTRIBUTES) {
                set_error("Не найден winws.exe для стратегии " + to_utf8(bat));
                return false;
            }
            exe = fallback;
        }
    }

    std::wstring args;
    for (size_t i = 1; i < tokens.size(); ++i) {
        if (tokens[i].empty() || tokens[i] == L"^") {
            continue;
        }
        if (!args.empty()) {
            args.push_back(L' ');
        }
        args += normalize_arg(dir, tokens[i]);
    }
    if (args.empty()) {
        set_error("Не удалось извлечь аргументы winws.exe из " + to_utf8(bat));
        return false;
    }

    out_exe = exe;
    out_args = args;
    out_command = L"\"" + exe + L"\" " + args;
    return true;
}

bool parse_winws_args(const std::wstring &dir, const std::wstring &bat,
                      std::wstring &out_args) {
    std::wstring exe;
    std::wstring command;
    return parse_winws_command(dir, bat, exe, out_args, command);
}

// ---------- runtime process ----------

void reader_thread(HANDLE read_pipe) {
    std::string pending;
    char chunk[4096];
    for (;;) {
        DWORD read = 0;
        if (!ReadFile(read_pipe, chunk, sizeof(chunk), &read, nullptr) || read == 0) {
            break;
        }
        pending.append(chunk, read);
        size_t pos = std::string::npos;
        while ((pos = pending.find('\n')) != std::string::npos) {
            std::string line = pending.substr(0, pos);
            pending.erase(0, pos + 1);
            if (!line.empty() && line.back() == '\r') {
                line.pop_back();
            }
            if (!line.empty()) {
                emit_log(0, line);
            }
        }
    }
    if (!pending.empty()) {
        emit_log(0, pending);
    }
}

// Читаем всё, что успел вывести winws, если он завершился сразу после старта.
std::string drain_pipe(HANDLE read_pipe) {
    std::string output;
    for (;;) {
        DWORD available = 0;
        if (!PeekNamedPipe(read_pipe, nullptr, 0, nullptr, &available, nullptr) ||
            available == 0) {
            break;
        }
        std::vector<char> buffer(available);
        DWORD read = 0;
        if (!ReadFile(read_pipe, buffer.data(), buffer.size(), &read, nullptr) ||
            read == 0) {
            break;
        }
        output.append(buffer.data(), read);
    }
    while (!output.empty() && (output.back() == '\r' || output.back() == '\n')) {
        output.pop_back();
    }
    return output;
}

void stop_runtime_internal(bool kill_orphans) {
    HANDLE job = nullptr;
    HANDLE process = nullptr;
    HANDLE read_pipe = nullptr;
    DWORD pid = 0;
    std::wstring strategy;
    {
        std::lock_guard<std::mutex> lock(g_runtime_mutex);
        job = g_job;
        process = g_process;
        read_pipe = g_read_pipe;
        pid = g_pid;
        strategy = g_active_strategy;
        g_job = nullptr;
        g_process = nullptr;
        g_read_pipe = nullptr;
        g_pid = 0;
        g_active_strategy.clear();
    }

    if (job) {
        // JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE terminates the whole tree.
        CloseHandle(job);
    }
    if (process) {
        WaitForSingleObject(process, 2000);
        CloseHandle(process);
    }
    if (read_pipe) {
        CancelIoEx(read_pipe, nullptr);
        CloseHandle(read_pipe);
    }
    if (g_reader.joinable()) {
        g_reader.join();
    }
    if (kill_orphans) {
        terminate_orphan_winws();
    }
    if (!strategy.empty() || pid != 0) {
        emit_log(0, "Остановлена стратегия: " + to_utf8(strategy) +
                       (pid ? " (PID " + std::to_string(pid) + ")" : ""));
    }
}

// ---------- TG WS Proxy runtime ----------

bool tgws_name_matches(const wchar_t *name) {
    return name && _wcsnicmp(name, L"TgWsProxy", 9) == 0 &&
           ends_with_ci(name, L".exe");
}

std::wstring trim_directory(std::wstring value) {
    while (!value.empty() &&
           (value.back() == L'\\' || value.back() == L'/')) {
        value.pop_back();
    }
    return value;
}

std::wstring process_image_path(DWORD pid) {
    HANDLE process = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, FALSE, pid);
    if (!process) {
        return std::wstring();
    }
    wchar_t buffer[32768];
    DWORD size = sizeof(buffer) / sizeof(buffer[0]);
    BOOL ok = QueryFullProcessImageNameW(process, 0, buffer, &size);
    CloseHandle(process);
    return ok ? std::wstring(buffer, size) : std::wstring();
}

bool path_under_dir(const std::wstring &path, const std::wstring &dir) {
    std::wstring root = lower_copy(trim_directory(dir));
    if (root.empty()) {
        return false;
    }
    std::wstring value = lower_copy(path);
    return value.size() > root.size() &&
           value.compare(0, root.size(), root) == 0 &&
           (value[root.size()] == L'\\' || value[root.size()] == L'/');
}

std::vector<DWORD> find_tgws_processes(const std::wstring &dir) {
    std::vector<DWORD> pids;
    HANDLE snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
    if (snapshot == INVALID_HANDLE_VALUE) {
        return pids;
    }

    PROCESSENTRY32W entry{};
    entry.dwSize = sizeof(entry);
    if (Process32FirstW(snapshot, &entry)) {
        do {
            if (!tgws_name_matches(entry.szExeFile)) {
                continue;
            }
            if (dir.empty() ||
                path_under_dir(process_image_path(entry.th32ProcessID), dir)) {
                pids.push_back(entry.th32ProcessID);
            }
        } while (Process32NextW(snapshot, &entry));
    }
    CloseHandle(snapshot);
    return pids;
}

void kill_tgws_orphans(const std::wstring &dir) {
    if (dir.empty()) {
        return;
    }
    for (DWORD pid : find_tgws_processes(dir)) {
        if (pid == g_tgws_pid && g_tgws_process) {
            continue;
        }
        HANDLE process = OpenProcess(PROCESS_TERMINATE, FALSE, pid);
        if (!process) {
            continue;
        }
        TerminateProcess(process, 0);
        CloseHandle(process);
        emit_log(1, "Завершён процесс TG WS Proxy (PID " +
                       std::to_string(pid) + ")");
    }
}

void tgws_reader_thread(HANDLE read_pipe) {
    std::string pending;
    char chunk[4096];
    for (;;) {
        DWORD read = 0;
        if (!ReadFile(read_pipe, chunk, sizeof(chunk), &read, nullptr) || read == 0) {
            break;
        }
        pending.append(chunk, read);
        size_t pos = std::string::npos;
        while ((pos = pending.find('\n')) != std::string::npos) {
            std::string line = pending.substr(0, pos);
            pending.erase(0, pos + 1);
            if (!line.empty() && line.back() == '\r') {
                line.pop_back();
            }
            if (!line.empty()) {
                emit_log(0, "[TG-WS] " + line);
            }
        }
    }
    if (!pending.empty()) {
        emit_log(0, "[TG-WS] " + pending);
    }
}

void stop_tgws_internal(bool kill_orphans, const std::wstring &dir) {
    HANDLE job = nullptr;
    HANDLE process = nullptr;
    HANDLE read_pipe = nullptr;
    DWORD pid = 0;
    {
        std::lock_guard<std::mutex> lock(g_tgws_mutex);
        job = g_tgws_job;
        process = g_tgws_process;
        read_pipe = g_tgws_read_pipe;
        pid = g_tgws_pid;
        g_tgws_job = nullptr;
        g_tgws_process = nullptr;
        g_tgws_read_pipe = nullptr;
        g_tgws_pid = 0;
        g_tgws_exe.clear();
        g_tgws_dir.clear();
    }

    if (job) {
        // PyInstaller launches a child process: the job kills the whole tree.
        CloseHandle(job);
    }
    if (process) {
        WaitForSingleObject(process, 2000);
        CloseHandle(process);
    }
    if (read_pipe) {
        CancelIoEx(read_pipe, nullptr);
        CloseHandle(read_pipe);
    }
    if (g_tgws_reader.joinable()) {
        g_tgws_reader.join();
    }
    if (kill_orphans) {
        kill_tgws_orphans(dir);
    }
    if (pid) {
        emit_log(0, "TG WS Proxy остановлен (PID " +
                       std::to_string(pid) + ")");
    }
}

int tgws_state_impl(const std::wstring &dir, std::string &detail) {
    {
        std::lock_guard<std::mutex> lock(g_tgws_mutex);
        if (g_tgws_process &&
            WaitForSingleObject(g_tgws_process, 0) != WAIT_OBJECT_0) {
            detail = "PID " + std::to_string(g_tgws_pid);
            return FS_TGWS_STATE_RUNNING;
        }
    }

    std::vector<DWORD> pids = find_tgws_processes(dir);
    if (!pids.empty()) {
        detail = "PID " + std::to_string(pids.front());
        return FS_TGWS_STATE_RUNNING;
    }

    detail.clear();
    return FS_TGWS_STATE_IDLE;
}

} // namespace

// ============================================================
// Exported API
// ============================================================

FS_API int FS_CALL fs_init(FSLogCallback log_cb, FSStateCallback state_cb) {
    {
        std::lock_guard<std::mutex> lock(g_cb_mutex);
        g_log_cb = log_cb;
        g_state_cb = state_cb;
    }
    g_initialized.store(true);
    set_error("");
    return FS_OK;
}

FS_API void FS_CALL fs_shutdown(void) {
    // Only release our own jobs; never kill processes we did not start.
    stop_runtime_internal(false);
    stop_tgws_internal(false, std::wstring());
    g_initialized.store(false);
    {
        std::lock_guard<std::mutex> lock(g_cb_mutex);
        g_log_cb = nullptr;
        g_state_cb = nullptr;
    }
    fs_release_single_instance();
}

FS_API int FS_CALL fs_is_admin(void) {
    return is_admin_process() ? 1 : 0;
}

FS_API int FS_CALL fs_request_elevation(void) {
    wchar_t executable[MAX_PATH] = {};
    GetModuleFileNameW(nullptr, executable, MAX_PATH);

    const wchar_t *cmd = GetCommandLineW();
    std::wstring params;
    if (cmd && *cmd == L'"') {
        const wchar_t *end = wcschr(cmd + 1, L'"');
        if (end) {
            params = end + 1;
        }
    } else if (cmd) {
        const wchar_t *space = wcschr(cmd, L' ');
        if (space) {
            params = space + 1;
        }
    }
    while (!params.empty() && params.front() == L' ') {
        params.erase(params.begin());
    }

    HINSTANCE result = ShellExecuteW(
        nullptr, L"runas", executable,
        params.empty() ? nullptr : params.c_str(), nullptr, SW_SHOWNORMAL
    );
    if (reinterpret_cast<intptr_t>(result) > 32) {
        return FS_OK;
    }
    set_error("Не удалось запросить повышение прав: " +
              system_error_text(GetLastError()));
    return FS_ERR_GENERIC;
}

FS_API int FS_CALL fs_acquire_single_instance(void) {
    if (g_single_mutex) {
        return FS_OK;
    }
    SetLastError(0);
    g_single_mutex = CreateMutexW(nullptr, TRUE,
                                  L"Global\\fsclient_single_instance_mutex");
    if (g_single_mutex && GetLastError() == ERROR_ALREADY_EXISTS) {
        CloseHandle(g_single_mutex);
        g_single_mutex = nullptr;
        set_error("Другой экземпляр fsclient уже запущен.");
        return FS_ERR_GENERIC;
    }
    if (!g_single_mutex) {
        SetLastError(0);
        g_single_mutex = CreateMutexW(nullptr, TRUE,
                                      L"Local\\fsclient_single_instance_mutex");
        if (g_single_mutex && GetLastError() == ERROR_ALREADY_EXISTS) {
            CloseHandle(g_single_mutex);
            g_single_mutex = nullptr;
            set_error("Другой экземпляр fsclient уже запущен.");
            return FS_ERR_GENERIC;
        }
    }
    if (!g_single_mutex) {
        set_error("Не удалось создать mutex одиночного запуска: " +
                  system_error_text(GetLastError()));
        return FS_ERR_GENERIC;
    }
    set_error("");
    return FS_OK;
}

FS_API void FS_CALL fs_release_single_instance(void) {
    if (g_single_mutex) {
        CloseHandle(g_single_mutex);
        g_single_mutex = nullptr;
    }
}

FS_API int FS_CALL fs_get_zapret_state(char *detail, int detail_len) {
    std::string detail_text;
    int state = get_state_impl(detail_text);
    copy_out(detail, detail_len, detail_text);
    return state;
}

FS_API int FS_CALL fs_get_active_service(char *out, int out_len) {
    std::wstring name;
    if (find_running_zapret_service(name)) {
        copy_out(out, out_len, to_utf8(name));
        return FS_OK;
    }
    copy_out(out, out_len, std::string());
    return FS_ERR_GENERIC;
}

FS_API int FS_CALL fs_get_installed_service(char *out, int out_len) {
    std::wstring value;
    if (!service_exists(L"zapret")) {
        copy_out(out, out_len, std::string());
        return FS_ERR_GENERIC;
    }
    value = installed_strategy_from_registry();
    if (value.empty()) {
        value = L"zapret";
    }
    copy_out(out, out_len, to_utf8(value));
    return FS_OK;
}

FS_API int FS_CALL fs_run_strategy(const wchar_t *dir, const wchar_t *bat) {
    if (!g_initialized.load()) {
        set_error("Бэкенд не инициализирован.");
        return FS_ERR_NOT_INITIALIZED;
    }
    if (!dir || !bat || !*dir || !*bat) {
        set_error("Не выбран релиз или стратегия.");
        return FS_ERR_INVALID_ARG;
    }
    std::string detail;
    int state = get_state_impl(detail);
    if (state == FS_STATE_RUNNING_PROCESS) {
        bool ours = false;
        {
            std::lock_guard<std::mutex> lock(g_runtime_mutex);
            ours = g_process != nullptr && g_pid != 0;
        }
        if (!ours) {
            // Осиротевший winws держит фильтр WinDivert. Убираем его и даём
            // драйверу время освободить фильтр перед новым запуском.
            terminate_orphan_winws();
            Sleep(1500);
            state = get_state_impl(detail);
        }
    }
    if (state != FS_STATE_IDLE) {
        set_error("Обход уже активен: " + detail +
                  ". Повторный запуск заблокирован.");
        return FS_ERR_ALREADY_RUNNING;
    }
    if (!is_admin_process()) {
        set_error("Для запуска стратегии нужны права администратора.");
        return FS_ERR_NEED_ADMIN;
    }
    if (service_exists(L"zapret")) {
        set_error("Установлена служба zapret. Удалите её перед запуском "
                  "обычного процесса.");
        return FS_ERR_SERVICE_INSTALLED;
    }

    std::wstring directory(dir);
    std::wstring strategy(bat);
    std::wstring exe;
    std::wstring args;
    std::wstring command_line;
    if (!parse_winws_command(directory, strategy, exe, args, command_line)) {
        return FS_ERR_INVALID_ARG;
    }
    ensure_user_lists(args);

    std::wstring work_dir = directory;
    size_t slash = exe.find_last_of(L"\\/");
    if (slash != std::wstring::npos) {
        work_dir = exe.substr(0, slash);
    }

    // winws может выйти сразу, если WinDivert ещё удерживает фильтр после
    // предыдущего запуска. Повторяем старт пару раз с паузой и пишем вывод
    // в лог, чтобы причина была видна.
    bool launched = false;
    for (int attempt = 0; attempt < 3 && !launched; ++attempt) {
        HANDLE job = CreateJobObjectW(nullptr, nullptr);
        if (!job) {
            set_error("Не удалось создать Job Object: " +
                      system_error_text(GetLastError()));
            return FS_ERR_GENERIC;
        }
        JOBOBJECT_EXTENDED_LIMIT_INFORMATION limits{};
        limits.BasicLimitInformation.LimitFlags =
            JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
        if (!SetInformationJobObject(job, JobObjectExtendedLimitInformation,
                                     &limits, sizeof(limits))) {
            set_error("Не удалось настроить Job Object: " +
                      system_error_text(GetLastError()));
            CloseHandle(job);
            return FS_ERR_GENERIC;
        }

        SECURITY_ATTRIBUTES sa{};
        sa.nLength = sizeof(sa);
        sa.bInheritHandle = TRUE;

        HANDLE read_pipe = nullptr;
        HANDLE write_pipe = nullptr;
        if (!CreatePipe(&read_pipe, &write_pipe, &sa, 0)) {
            set_error("Не удалось создать pipe вывода: " +
                      system_error_text(GetLastError()));
            CloseHandle(job);
            return FS_ERR_GENERIC;
        }
        SetHandleInformation(read_pipe, HANDLE_FLAG_INHERIT, 0);

        HANDLE input_read = nullptr;
        HANDLE input_write = nullptr;
        CreatePipe(&input_read, &input_write, &sa, 0);
        SetHandleInformation(input_read, HANDLE_FLAG_INHERIT, 0);

        STARTUPINFOW startup{};
        startup.cb = sizeof(startup);
        startup.dwFlags = STARTF_USESTDHANDLES | STARTF_USESHOWWINDOW;
        startup.hStdInput = input_read;
        startup.hStdOutput = write_pipe;
        startup.hStdError = write_pipe;
        startup.wShowWindow = SW_HIDE;

        PROCESS_INFORMATION process{};
        std::vector<wchar_t> mutable_command(command_line.begin(),
                                             command_line.end());
        mutable_command.push_back(L'\0');

        BOOL created = CreateProcessW(
            nullptr, mutable_command.data(), nullptr, nullptr, TRUE,
            CREATE_NO_WINDOW, nullptr, work_dir.c_str(), &startup, &process
        );
        DWORD create_error = GetLastError();

        CloseHandle(input_read);
        CloseHandle(input_write);
        CloseHandle(write_pipe);
        if (!created) {
            CloseHandle(read_pipe);
            CloseHandle(job);
            set_error("Не удалось запустить стратегию: " +
                      system_error_text(create_error));
            return FS_ERR_GENERIC;
        }

        BOOL assigned = AssignProcessToJobObject(job, process.hProcess);
        if (!assigned) {
            emit_log(1, std::string("Не удалось связать процесс с Job Object: ") +
                           system_error_text(GetLastError()));
        }

        // Если процесс умер за 1.2 с — это почти всегда ошибка WinDivert.
        DWORD wait = WaitForSingleObject(process.hProcess, 1200);
        if (wait == WAIT_OBJECT_0) {
            DWORD code = 0;
            GetExitCodeProcess(process.hProcess, &code);
            std::string output = drain_pipe(read_pipe);
            emit_log(1, "winws.exe завершился сразу (код " +
                           std::to_string(code) +
                           "). Повтор через 1 с...");
            if (!output.empty()) {
                emit_log(1, output);
            }
            CloseHandle(process.hThread);
            CloseHandle(process.hProcess);
            CloseHandle(read_pipe);
            CloseHandle(job);
            Sleep(1000);
            continue;
        }

        {
            std::lock_guard<std::mutex> lock(g_runtime_mutex);
            if (g_reader.joinable()) {
                g_reader.join();
            }
            g_job = job;
            g_process = process.hProcess;
            g_read_pipe = read_pipe;
            g_pid = process.dwProcessId;
            g_active_strategy = strategy;
            g_reader = std::thread(reader_thread, read_pipe);
        }

        emit_log(0, "Запущена стратегия: " + to_utf8(strategy) +
                       " (PID " + std::to_string(process.dwProcessId) + ")");
        if (!assigned) {
            emit_log(1, "Процесс не привязан к Job Object — при остановке "
                       "будет выполнен принудительный поиск winws.exe.");
        }
        emit_state(FS_STATE_RUNNING_PROCESS, to_utf8(strategy));
        set_error("");
        launched = true;
    }

    if (!launched) {
        set_error("winws.exe завершался сразу после старта. Проверьте, "
                  "не удерживает ли фильтр WinDivert другая программа.");
        return FS_ERR_GENERIC;
    }
    return FS_OK;
}

FS_API int FS_CALL fs_stop_strategy(void) {
    if (!g_initialized.load()) {
        set_error("Бэкенд не инициализирован.");
        return FS_ERR_NOT_INITIALIZED;
    }
    stop_runtime_internal(true);
    if (service_exists(L"zapret")) {
        stop_service_internal(L"zapret");
    }
    emit_state(FS_STATE_IDLE, std::string());
    set_error("");
    return FS_OK;
}

FS_API int FS_CALL fs_install_service(const wchar_t *dir, const wchar_t *bat) {
    if (!g_initialized.load()) {
        set_error("Бэкенд не инициализирован.");
        return FS_ERR_NOT_INITIALIZED;
    }
    if (!dir || !bat || !*dir || !*bat) {
        set_error("Не выбрана стратегия для службы.");
        return FS_ERR_INVALID_ARG;
    }
    std::string detail;
    if (get_state_impl(detail) != FS_STATE_IDLE) {
        set_error("Сначала остановите активный обход: " + detail);
        return FS_ERR_ALREADY_RUNNING;
    }
    if (!is_admin_process()) {
        set_error("Для установки службы нужны права администратора.");
        return FS_ERR_NEED_ADMIN;
    }

    std::wstring directory(dir);
    std::wstring strategy(bat);
    std::wstring binary;
    std::wstring args;
    std::wstring command_line;
    if (!parse_winws_command(directory, strategy, binary, args, command_line)) {
        return FS_ERR_INVALID_ARG;
    }
    // Служба стартует winws.exe без батника — пользовательские списки должны
    // существовать заранее, иначе zapret не сможет их зарегистрировать.
    ensure_user_lists(args);

    SC_HANDLE scm = OpenSCManagerW(nullptr, nullptr, SC_MANAGER_ALL_ACCESS);
    if (!scm) {
        set_error("Не удалось открыть SCM: " +
                  system_error_text(GetLastError()));
        return FS_ERR_GENERIC;
    }

    SC_HANDLE old = OpenServiceW(scm, L"zapret", SERVICE_ALL_ACCESS);
    if (old) {
        SERVICE_STATUS status{};
        ControlService(old, SERVICE_CONTROL_STOP, &status);
        for (int i = 0; i < 40; ++i) {
            if (!QueryServiceStatus(old, &status) ||
                status.dwCurrentState == SERVICE_STOPPED) {
                break;
            }
            Sleep(250);
        }
        DeleteService(old);
        CloseServiceHandle(old);
    }

    std::wstring binary_path = L"\"" + binary + L"\" " + args;
    std::wstring display = L"zapret (" + strategy + L")";
    SC_HANDLE service = CreateServiceW(
        scm, L"zapret", display.c_str(), SERVICE_ALL_ACCESS,
        SERVICE_WIN32_OWN_PROCESS, SERVICE_AUTO_START, SERVICE_ERROR_NORMAL,
        binary_path.c_str(), nullptr, nullptr, nullptr, nullptr, nullptr
    );
    if (!service) {
        DWORD error = GetLastError();
        CloseServiceHandle(scm);
        set_error("Не удалось создать службу: " + system_error_text(error));
        return FS_ERR_GENERIC;
    }

    wchar_t description[] = L"Zapret DPI bypass software";
    SERVICE_DESCRIPTION desc{};
    desc.lpDescription = description;
    ChangeServiceConfig2W(service, SERVICE_CONFIG_DESCRIPTION, &desc);

    HKEY key = nullptr;
    if (RegOpenKeyExW(HKEY_LOCAL_MACHINE,
                      L"SYSTEM\\CurrentControlSet\\Services\\zapret",
                      0, KEY_SET_VALUE, &key) == ERROR_SUCCESS) {
        RegSetValueExW(key, L"zapret-discord-youtube", 0, REG_SZ,
                       reinterpret_cast<const BYTE *>(strategy.c_str()),
                       static_cast<DWORD>((strategy.size() + 1) * sizeof(wchar_t)));
        RegCloseKey(key);
    }

    if (!StartServiceW(service, 0, nullptr)) {
        emit_log(1, "Служба создана, но не запустилась: " +
                       system_error_text(GetLastError()));
    }
    CloseServiceHandle(service);
    CloseServiceHandle(scm);

    emit_log(0, "Установлена служба zapret: " + to_utf8(strategy));
    emit_state(FS_STATE_RUNNING_SERVICE, "zapret (" + to_utf8(strategy) + ")");
    set_error("");
    return FS_OK;
}

FS_API int FS_CALL fs_remove_service(void) {
    if (!g_initialized.load()) {
        set_error("Бэкенд не инициализирован.");
        return FS_ERR_NOT_INITIALIZED;
    }
    if (!is_admin_process()) {
        set_error("Для удаления службы нужны права администратора.");
        return FS_ERR_NEED_ADMIN;
    }

    if (service_exists(L"zapret")) {
        stop_service_internal(L"zapret");
        delete_service_internal(L"zapret");
        emit_log(0, "Служба zapret удалена");
    }

    static const wchar_t *helpers[] = {L"WinDivert", L"WinDivert14"};
    SC_HANDLE scm = OpenSCManagerW(nullptr, nullptr, SC_MANAGER_CONNECT);
    if (scm) {
        for (const wchar_t *name : helpers) {
            SC_HANDLE service = OpenServiceW(
                scm, name, SERVICE_QUERY_CONFIG | SERVICE_STOP | DELETE
            );
            if (!service) {
                continue;
            }
            bool belongs = service_binary_belongs_to_zapret(service);
            if (belongs) {
                stop_service_internal(name);
                DeleteService(service);
                emit_log(0, std::string("Удалена вспомогательная служба: ") +
                               to_utf8(name));
            }
            CloseServiceHandle(service);
        }
        CloseServiceHandle(scm);
    }

    std::string detail;
    emit_state(get_state_impl(detail), detail);
    set_error("");
    return FS_OK;
}

FS_API int FS_CALL fs_start_service(void) {
    if (!g_initialized.load()) {
        set_error("Бэкенд не инициализирован.");
        return FS_ERR_NOT_INITIALIZED;
    }
    if (!is_admin_process()) {
        set_error("Для запуска службы нужны права администратора.");
        return FS_ERR_NEED_ADMIN;
    }
    SC_HANDLE scm = OpenSCManagerW(nullptr, nullptr, SC_MANAGER_CONNECT);
    if (!scm) {
        return FS_ERR_GENERIC;
    }
    // Служба может быть установлена старой версией без пользовательских
    // списков — создаём их по её command line перед стартом.
    ensure_user_lists(service_binary_path(L"zapret"));
    SC_HANDLE service = OpenServiceW(scm, L"zapret", SERVICE_START);
    bool ok = false;
    if (service) {
        ok = StartServiceW(service, 0, nullptr) != FALSE;
        CloseServiceHandle(service);
    }
    CloseServiceHandle(scm);
    if (!ok) {
        set_error("Не удалось запустить службу: " +
                  system_error_text(GetLastError()));
        return FS_ERR_GENERIC;
    }
    emit_log(0, "Служба zapret запущена");
    emit_state(FS_STATE_RUNNING_SERVICE, "zapret");
    return FS_OK;
}

FS_API int FS_CALL fs_stop_service(void) {
    if (!g_initialized.load()) {
        set_error("Бэкенд не инициализирован.");
        return FS_ERR_NOT_INITIALIZED;
    }
    if (!is_admin_process()) {
        set_error("Для остановки службы нужны права администратора.");
        return FS_ERR_NEED_ADMIN;
    }
    if (!service_exists(L"zapret")) {
        set_error("Служба zapret не установлена.");
        return FS_ERR_GENERIC;
    }
    stop_service_internal(L"zapret");
    emit_log(0, "Служба zapret остановлена");
    std::string detail;
    emit_state(get_state_impl(detail), detail);
    return FS_OK;
}

FS_API int FS_CALL fs_get_strategy_command(const wchar_t *dir,
                                           const wchar_t *bat,
                                           char *out, int out_len) {
    if (!dir || !bat || !*dir || !*bat) {
        set_error("Не выбрана стратегия.");
        return FS_ERR_INVALID_ARG;
    }
    std::wstring exe;
    std::wstring args;
    std::wstring command;
    if (!parse_winws_command(dir, bat, exe, args, command)) {
        return FS_ERR_INVALID_ARG;
    }
    copy_out(out, out_len, to_utf8(command));
    return FS_OK;
}

FS_API int FS_CALL fs_tgws_start(const wchar_t *exe, const wchar_t *dir) {
    if (!g_initialized.load()) {
        set_error("Бэкенд не инициализирован.");
        return FS_ERR_NOT_INITIALIZED;
    }
    if (!exe || !*exe || !dir || !*dir) {
        set_error("Не выбран исполняемый файл TG WS Proxy.");
        return FS_ERR_INVALID_ARG;
    }
    if (GetFileAttributesW(exe) == INVALID_FILE_ATTRIBUTES) {
        set_error("Не найден исполняемый файл: " + to_utf8(exe));
        return FS_ERR_INVALID_ARG;
    }

    std::string detail;
    if (tgws_state_impl(std::wstring(dir), detail) == FS_TGWS_STATE_RUNNING) {
        set_error("TG WS Proxy уже запущен: " + detail +
                  ". Повторный запуск заблокирован.");
        return FS_ERR_ALREADY_RUNNING;
    }

    // Clean up stale handles from a previously exited process.
    stop_tgws_internal(false, std::wstring());

    HANDLE job = CreateJobObjectW(nullptr, nullptr);
    if (!job) {
        set_error("Не удалось создать Job Object для TG WS Proxy: " +
                  system_error_text(GetLastError()));
        return FS_ERR_GENERIC;
    }
    JOBOBJECT_EXTENDED_LIMIT_INFORMATION limits{};
    limits.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
    if (!SetInformationJobObject(job, JobObjectExtendedLimitInformation,
                                 &limits, sizeof(limits))) {
        set_error("Не удалось настроить Job Object: " +
                  system_error_text(GetLastError()));
        CloseHandle(job);
        return FS_ERR_GENERIC;
    }

    SECURITY_ATTRIBUTES sa{};
    sa.nLength = sizeof(sa);
    sa.bInheritHandle = TRUE;

    HANDLE read_pipe = nullptr;
    HANDLE write_pipe = nullptr;
    if (!CreatePipe(&read_pipe, &write_pipe, &sa, 0)) {
        set_error("Не удалось создать pipe вывода: " +
                  system_error_text(GetLastError()));
        CloseHandle(job);
        return FS_ERR_GENERIC;
    }
    SetHandleInformation(read_pipe, HANDLE_FLAG_INHERIT, 0);

    STARTUPINFOW startup{};
    startup.cb = sizeof(startup);
    startup.dwFlags = STARTF_USESTDHANDLES | STARTF_USESHOWWINDOW;
    startup.hStdInput = GetStdHandle(STD_INPUT_HANDLE);
    startup.hStdOutput = write_pipe;
    startup.hStdError = write_pipe;
    startup.wShowWindow = SW_HIDE;

    std::wstring command = L"\"" + std::wstring(exe) + L"\" --portable";
    std::vector<wchar_t> mutable_command(command.begin(), command.end());
    mutable_command.push_back(L'\0');

    PROCESS_INFORMATION process{};
    BOOL created = CreateProcessW(
        nullptr, mutable_command.data(), nullptr, nullptr, TRUE,
        CREATE_NO_WINDOW, nullptr, dir, &startup, &process
    );
    DWORD create_error = GetLastError();
    CloseHandle(write_pipe);
    if (!created) {
        CloseHandle(read_pipe);
        CloseHandle(job);
        set_error("Не удалось запустить TG WS Proxy: " +
                  system_error_text(create_error));
        return FS_ERR_GENERIC;
    }

    BOOL assigned = AssignProcessToJobObject(job, process.hProcess);
    if (!assigned) {
        emit_log(1, std::string("TG WS Proxy не привязан к Job Object: ") +
                       system_error_text(GetLastError()));
    }

    {
        std::lock_guard<std::mutex> lock(g_tgws_mutex);
        g_tgws_job = job;
        g_tgws_process = process.hProcess;
        g_tgws_read_pipe = read_pipe;
        g_tgws_pid = process.dwProcessId;
        g_tgws_exe = exe;
        g_tgws_dir = dir;
        g_tgws_reader = std::thread(tgws_reader_thread, read_pipe);
    }

    emit_log(0, "Запущен TG WS Proxy (PID " +
                   std::to_string(process.dwProcessId) + ")");
    set_error("");
    return FS_OK;
}

FS_API int FS_CALL fs_tgws_stop(const wchar_t *dir) {
    if (!g_initialized.load()) {
        set_error("Бэкенд не инициализирован.");
        return FS_ERR_NOT_INITIALIZED;
    }
    stop_tgws_internal(true, dir ? std::wstring(dir) : std::wstring());
    set_error("");
    return FS_OK;
}

FS_API int FS_CALL fs_tgws_state(const wchar_t *dir, char *out, int out_len) {
    std::string detail;
    int state = tgws_state_impl(dir ? std::wstring(dir) : std::wstring(), detail);
    copy_out(out, out_len, detail);
    return state;
}

FS_API int FS_CALL fs_last_error(char *out, int out_len) {
    copy_out(out, out_len, last_error_text());
    return FS_OK;
}
