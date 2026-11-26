using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows;

namespace FsClient;

public sealed class ReleaseInfo
{
    public string Tag { get; init; } = "";
    public string Url { get; init; }
    public bool IsLatest { get; init; }
}

/// <summary>
/// C#-замена backend_ctypes.py: та же логика, но P/Invoke вместо ctypes.
/// Сигнатуры взяты из cpp/backend.h.
/// </summary>
public sealed class Backend
{
    public const int FsOk = 0;
    public const int FsErrNeedAdmin = -3;

    public const int FsStateIdle = 0;
    public const int FsStateRunningProcess = 1;
    public const int FsStateRunningService = 2;

    private const string DllName = "fs_backend";
    private const string ReleasesApi = "https://api.github.com/repos/Flowseal/zapret-discord-youtube/releases";

    private static readonly string AppDir = AppContext.BaseDirectory;
    private static readonly string ReleasesDir = Path.Combine(AppDir, "releases");
    private static readonly string PluginsDir = Path.Combine(AppDir, "plugins");
    private static readonly string DllPath = Path.Combine(AppDir, "cpp", "fs_backend.dll");

    private static string ResolveDllPath() =>
        File.Exists(DllPath) ? DllPath : BundledResources.Extract("FsClient.fs_backend.dll", "fs_backend.dll");

    private static readonly List<ReleaseInfo> FallbackReleases = new()
    {
        new ReleaseInfo { Tag = "v1.10.4", Url = null, IsLatest = true },
        new ReleaseInfo { Tag = "v1.10.3", Url = null },
        new ReleaseInfo { Tag = "v1.10.2", Url = null },
        new ReleaseInfo { Tag = "v1.10.1", Url = null },
        new ReleaseInfo { Tag = "v1.10.0", Url = null },
    };

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void NativeLogCallback(int level, IntPtr message);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void NativeStateCallback(int state, IntPtr detail);

    private static readonly NativeLogCallback LogCallbackHolder = OnNativeLog;
    private static readonly NativeStateCallback StateCallbackHolder = OnNativeState;

    private static bool _initialized;

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    private static extern int fs_init(NativeLogCallback logCb, NativeStateCallback stateCb);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    private static extern void fs_shutdown();

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    private static extern int fs_is_admin();

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    private static extern int fs_request_elevation();

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    private static extern int fs_acquire_single_instance();

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    private static extern void fs_release_single_instance();

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    private static extern int fs_get_zapret_state([Out] byte[] detail, int detailLen);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    private static extern int fs_get_active_service([Out] byte[] output, int outputLen);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    private static extern int fs_get_installed_service([Out] byte[] output, int outputLen);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Unicode)]
    private static extern int fs_run_strategy(
        [MarshalAs(UnmanagedType.LPWStr)] string dir,
        [MarshalAs(UnmanagedType.LPWStr)] string bat);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Unicode)]
    private static extern int fs_get_strategy_command(
        [MarshalAs(UnmanagedType.LPWStr)] string dir,
        [MarshalAs(UnmanagedType.LPWStr)] string bat,
        [Out] byte[] output, int outputLen);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    private static extern int fs_stop_strategy();

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Unicode)]
    private static extern int fs_install_service(
        [MarshalAs(UnmanagedType.LPWStr)] string dir,
        [MarshalAs(UnmanagedType.LPWStr)] string bat);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    private static extern int fs_remove_service();

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    private static extern int fs_start_service();

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    private static extern int fs_stop_service();

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    private static extern int fs_last_error([Out] byte[] output, int outputLen);

    static Backend()
    {
        NativeLibrary.SetDllImportResolver(typeof(Backend).Assembly,
            (name, _, _) =>
            {
                if (name != DllName)
                    return IntPtr.Zero;
                return NativeLibrary.Load(ResolveDllPath());
            });
    }

    private Backend() { }

    public static Backend Instance { get; private set; }

    public event Action<string> Log;
    public event Action<List<ReleaseInfo>> ReleasesReady;
    public event Action<int, string> DownloadProgress;
    public event Action<string> DownloadDone;
    public event Action<string> DownloadFailed;
    public event Action<string> ServiceChanged;
    public event Action<string> StrategyStarted;
    public event Action<int, string> StateChanged;

    public List<ReleaseInfo> Releases { get; private set; } = new();
    public string InstalledService { get; private set; } = "";
    public string RunningStrategy { get; set; } = "";
    public int NativeState { get; private set; } = FsStateIdle;
    public string NativeDetail { get; private set; } = "";

    public static void Initialize()
    {
        if (_initialized)
            return;

        string dll = ResolveDllPath();
        if (!File.Exists(dll))
            throw new FileNotFoundException("Не найден fs_backend.dll", dll);

        Instance = new Backend();
        if (fs_init(LogCallbackHolder, StateCallbackHolder) != FsOk)
            throw new InvalidOperationException("fs_init вернул ошибку");

        _initialized = true;

        try { Directory.CreateDirectory(PluginsDir); } catch { /* ignore */ }

        Instance.InstalledService = Instance.GetInstalledService();
        var (state, detail) = Instance.GetState();
        Instance.NativeState = state;
        Instance.NativeDetail = detail;
    }

    public static void Shutdown()
    {
        if (!_initialized)
            return;
        fs_shutdown();
        _initialized = false;
    }

    private static void Ui(Action action)
    {
        var app = System.Windows.Application.Current;
        if (app == null || app.Dispatcher.CheckAccess())
        {
            action();
            return;
        }
        app.Dispatcher.BeginInvoke(action);
    }

    private static string Decode(IntPtr ptr) =>
        ptr == IntPtr.Zero ? "" : Marshal.PtrToStringUTF8(ptr) ?? "";

    private static string Decode(byte[] buffer)
    {
        int length = Array.IndexOf(buffer, (byte)0);
        if (length < 0) length = buffer.Length;
        return Encoding.UTF8.GetString(buffer, 0, length);
    }

    private static void OnNativeLog(int level, IntPtr message)
    {
        string prefix = level switch { 1 => "⚠ ", 2 => "✖ ", _ => "" };
        string text = prefix + Decode(message);
        Ui(() => Instance?.Log?.Invoke(text));
    }

    private static void OnNativeState(int state, IntPtr detail)
    {
        string text = Decode(detail);
        Ui(() =>
        {
            if (Instance == null) return;
            Instance.NativeState = state;
            Instance.NativeDetail = text;
            Instance.StateChanged?.Invoke(state, text);
        });
    }

    public (bool Ok, string Error) AcquireSingleInstance()
    {
        int rc = fs_acquire_single_instance();
        return (rc == FsOk, LastError());
    }

    public bool IsAdmin() => fs_is_admin() != 0;

    public bool RequestElevation()
    {
        fs_release_single_instance();
        bool ok = fs_request_elevation() == FsOk;
        if (!ok)
            fs_acquire_single_instance();
        Ui(() => Log?.Invoke(ok
            ? "Перезапуск с правами администратора..."
            : $"Не удалось повысить права: {LastError()}"));
        return ok;
    }

    public string LastError()
    {
        var buffer = new byte[1024];
        fs_last_error(buffer, buffer.Length);
        return Decode(buffer);
    }

    public (int State, string Detail) GetState()
    {
        var buffer = new byte[1024];
        int state = fs_get_zapret_state(buffer, buffer.Length);
        return (state, Decode(buffer));
    }

    public string GetActiveService()
    {
        var buffer = new byte[512];
        return fs_get_active_service(buffer, buffer.Length) == FsOk ? Decode(buffer) : "";
    }

    public string GetInstalledService()
    {
        var buffer = new byte[512];
        return fs_get_installed_service(buffer, buffer.Length) == FsOk ? Decode(buffer) : "";
    }

    public string GetStrategyCommand(string tag, string bat)
    {
        var buffer = new byte[8192];
        return fs_get_strategy_command(ReleaseDir(tag), bat, buffer, buffer.Length) == FsOk
            ? Decode(buffer)
            : "";
    }

    public (int State, string Detail) PollState()
    {
        var previous = (NativeState, NativeDetail);
        var (state, detail) = GetState();
        string installed = GetInstalledService();

        NativeState = state;
        NativeDetail = detail;

        if (installed != InstalledService)
        {
            InstalledService = installed;
            Ui(() => ServiceChanged?.Invoke(installed));
        }
        if ((state, detail) != previous)
            Ui(() => StateChanged?.Invoke(state, detail));

        return (state, detail);
    }

    // ---------- Релизы ----------

    public void FetchReleasesAsync()
    {
        _ = Task.Run(async () =>
        {
            try
            {
                using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
                client.DefaultRequestHeaders.Add("User-Agent", "fsclient/1.0");
                client.DefaultRequestHeaders.Add("Accept", "application/vnd.github+json");

                string json = await client.GetStringAsync(ReleasesApi);
                using var doc = JsonDocument.Parse(json);

                var result = new List<ReleaseInfo>();
                foreach (var release in doc.RootElement.EnumerateArray())
                {
                    if (result.Count >= 15) break;

                    string tag = release.TryGetProperty("tag_name", out var tagElement)
                        ? tagElement.GetString() ?? ""
                        : "";
                    string url = null;

                    if (release.TryGetProperty("assets", out var assets) &&
                        assets.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var asset in assets.EnumerateArray())
                        {
                            string name = asset.TryGetProperty("name", out var nameElement)
                                ? nameElement.GetString() ?? ""
                                : "";
                            if (name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                            {
                                url = asset.TryGetProperty("browser_download_url", out var urlElement)
                                    ? urlElement.GetString()
                                    : null;
                                break;
                            }
                        }
                    }

                    result.Add(new ReleaseInfo
                    {
                        Tag = tag,
                        Url = url,
                        IsLatest = result.Count == 0
                    });
                }

                if (result.Count == 0)
                    throw new InvalidOperationException("Пустой список релизов");

                Releases = result;
                Ui(() => ReleasesReady?.Invoke(result));
            }
            catch (Exception ex)
            {
                Ui(() =>
                {
                    Log?.Invoke($"GitHub API недоступен: {ex.Message}");
                    Log?.Invoke("Использую резервный список для демонстрации.");
                });
                Releases = FallbackReleases;
                Ui(() => ReleasesReady?.Invoke(FallbackReleases));
            }
        });
    }

    public bool IsDownloaded(string tag) =>
        !string.IsNullOrEmpty(tag) && Directory.Exists(Path.Combine(ReleasesDir, tag));

    public HashSet<string> ListDownloadedTags()
    {
        if (!Directory.Exists(ReleasesDir))
            return new HashSet<string>();

        return Directory.GetDirectories(ReleasesDir)
            .Select(Path.GetFileName)
            .Where(name => !string.IsNullOrEmpty(name) && !name.StartsWith("."))
            .Select(name => name!)
            .ToHashSet(StringComparer.Ordinal);
    }

    public List<string> ListStrategies(string tag)
    {
        string dir = string.IsNullOrEmpty(tag) ? "" : Path.Combine(ReleasesDir, tag);
        if (!Directory.Exists(dir))
            return new List<string>();

        return Directory.GetFiles(dir, "*.bat")
            .Select(Path.GetFileName)
            .Where(name => name != null &&
                           !name.Contains("service", StringComparison.OrdinalIgnoreCase))
            .Select(name => name!)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public void DownloadRelease(string tag, string url)
    {
        if (string.IsNullOrEmpty(url))
        {
            Ui(() => DownloadFailed?.Invoke("Нет ссылки для скачивания (демо-режим)"));
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                Directory.CreateDirectory(ReleasesDir);
                string targetDir = Path.Combine(ReleasesDir, tag);
                string zipPath = Path.Combine(ReleasesDir, tag + ".zip");

                Ui(() => DownloadProgress?.Invoke(0, "Скачивание архива..."));

                using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
                client.DefaultRequestHeaders.Add("User-Agent", "fsclient/1.0");
                using var response = await client.GetAsync(
                    url, HttpCompletionOption.ResponseHeadersRead);
                response.EnsureSuccessStatusCode();

                long total = response.Content.Headers.ContentLength ?? 0;
                long downloaded = 0;
                var buffer = new byte[65536];

                await using (var source = await response.Content.ReadAsStreamAsync())
                await using (var target = File.Create(zipPath))
                {
                    int read;
                    while ((read = await source.ReadAsync(buffer)) > 0)
                    {
                        await target.WriteAsync(buffer.AsMemory(0, read));
                        downloaded += read;
                        if (total > 0)
                        {
                            int pct = (int)Math.Min(90, downloaded * 90 / total);
                            Ui(() => DownloadProgress?.Invoke(pct, "Скачивание архива..."));
                        }
                    }
                }

                Ui(() => DownloadProgress?.Invoke(92, "Распаковка..."));

                if (Directory.Exists(targetDir))
                    Directory.Delete(targetDir, true);
                Directory.CreateDirectory(targetDir);
                ZipFile.ExtractToDirectory(zipPath, targetDir);

                var entries = Directory.GetFileSystemEntries(targetDir);
                if (entries.Length == 1 && Directory.Exists(entries[0]))
                {
                    string sub = entries[0];
                    foreach (string file in Directory.GetFileSystemEntries(sub))
                    {
                        string destination = Path.Combine(targetDir, Path.GetFileName(file));
                        if (Directory.Exists(file))
                            Directory.Move(file, destination);
                        else
                            File.Move(file, destination, true);
                    }
                    Directory.Delete(sub);
                }

                try { File.Delete(zipPath); } catch { /* ignore */ }

                Ui(() =>
                {
                    DownloadProgress?.Invoke(100, "Готово");
                    DownloadDone?.Invoke(tag);
                });
            }
            catch (Exception ex)
            {
                Ui(() => DownloadFailed?.Invoke(ex.Message));
            }
        });
    }

    // ---------- Стратегии / служба ----------

    public bool RunStrategy(string tag, string bat)
    {
        int rc = fs_run_strategy(ReleaseDir(tag), bat);
        if (rc == FsOk)
        {
            RunningStrategy = bat;
            Ui(() =>
            {
                Log?.Invoke($"Запуск стратегии: {bat}");
                StrategyStarted?.Invoke(bat);
            });
            return true;
        }

        string error = LastError();
        Ui(() => Log?.Invoke($"Запуск заблокирован: {error}"));
        if (rc == FsErrNeedAdmin)
        {
            Ui(() => Log?.Invoke("Запрос прав администратора..."));
            RequestElevation();
        }
        return false;
    }

    public bool StopStrategy()
    {
        if (fs_stop_strategy() != FsOk)
        {
            Ui(() => Log?.Invoke($"Ошибка остановки: {LastError()}"));
            return false;
        }

        RunningStrategy = "";
        Ui(() => Log?.Invoke("Процесс обхода остановлен"));
        PollState();
        return true;
    }

    public bool InstallService(string tag, string bat)
    {
        int rc = fs_install_service(ReleaseDir(tag), bat);
        if (rc == FsOk)
        {
            InstalledService = bat;
            Ui(() =>
            {
                Log?.Invoke($"Служба установлена: {bat}");
                ServiceChanged?.Invoke(bat);
            });
            return true;
        }

        string error = LastError();
        Ui(() => Log?.Invoke($"Не удалось установить службу: {error}"));
        if (rc == FsErrNeedAdmin)
        {
            Ui(() => Log?.Invoke("Запрос прав администратора..."));
            RequestElevation();
        }
        return false;
    }

    public bool RemoveService()
    {
        if (fs_remove_service() != FsOk)
        {
            Ui(() => Log?.Invoke($"Не удалось удалить службу: {LastError()}"));
            return false;
        }

        if (!string.IsNullOrEmpty(InstalledService))
            Ui(() => Log?.Invoke($"Служба удалена: {InstalledService}"));
        InstalledService = "";
        Ui(() => ServiceChanged?.Invoke(""));
        PollState();
        return true;
    }

    public bool StartService()
    {
        if (fs_start_service() != FsOk)
        {
            Ui(() => Log?.Invoke($"Не удалось запустить службу: {LastError()}"));
            return false;
        }
        Ui(() => Log?.Invoke("Служба zapret запущена"));
        PollState();
        return true;
    }

    public bool StopService()
    {
        if (fs_stop_service() != FsOk)
        {
            Ui(() => Log?.Invoke($"Не удалось остановить службу: {LastError()}"));
            return false;
        }
        Ui(() => Log?.Invoke("Служба zapret остановлена"));
        PollState();
        return true;
    }

    public string ReleaseDir(string tag) =>
        Path.GetFullPath(Path.Combine(ReleasesDir, tag ?? ""));

    public void OpenFolder(string tag)
    {
        string dir = string.IsNullOrEmpty(tag) ? ReleasesDir : Path.Combine(ReleasesDir, tag);
        try
        {
            Directory.CreateDirectory(dir);
            Process.Start(new ProcessStartInfo
            {
                FileName = dir,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            Ui(() => Log?.Invoke($"Не удалось открыть папку: {ex.Message}"));
        }
    }

    public void OpenPluginsFolder()
    {
        try
        {
            Directory.CreateDirectory(PluginsDir);
            Process.Start(new ProcessStartInfo
            {
                FileName = PluginsDir,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            Ui(() => Log?.Invoke($"Не удалось открыть папку плагинов: {ex.Message}"));
        }
    }
}
