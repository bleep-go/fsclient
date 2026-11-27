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
    public string Tag { get; set; } = "";
    public string Url { get; set; }
    public bool IsPrerelease { get; set; }
    public bool IsRecommended { get; set; }
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

    public const int TgwsStateIdle = 0;
    public const int TgwsStateRunning = 1;

    private const string DllName = "fs_backend";
    private const string ReleasesApi = "https://api.github.com/repos/Flowseal/zapret-discord-youtube/releases";

    private static readonly string AppDir = AppContext.BaseDirectory;
    private static readonly string DllPath = Path.Combine(AppDir, "cpp", "fs_backend.dll");

    // Папки данных задаются в настройках и могут быть перенесены пользователем.
    private static string ReleasesDir => SettingsService.ReleasesDir;
    private static string PluginsDir => SettingsService.PluginsDir;

    private static string ResolveDllPath() =>
        File.Exists(DllPath) ? DllPath : BundledResources.Extract("FsClient.fs_backend.dll", "fs_backend.dll");

    // GitHub отдаёт теги без префикса v. Резервный список должен совпадать
    // с именами папок в releases\, иначе скачанный релиз не находится.
    private static string FallbackUrl(string tag) =>
        $"https://github.com/Flowseal/zapret-discord-youtube/releases/download/{tag}/zapret-discord-youtube-{tag}.zip";

    private static readonly List<ReleaseInfo> FallbackReleases = BuildReleaseList(new List<ReleaseInfo>
    {
        new ReleaseInfo { Tag = "1.10.4", Url = FallbackUrl("1.10.4") },
        new ReleaseInfo { Tag = "1.10.3", Url = FallbackUrl("1.10.3") },
        new ReleaseInfo { Tag = "1.10.2", Url = FallbackUrl("1.10.2") },
        new ReleaseInfo { Tag = "1.10.1", Url = FallbackUrl("1.10.1") },
        new ReleaseInfo { Tag = "1.10.0", Url = FallbackUrl("1.10.0") },
        new ReleaseInfo
        {
            Tag = "2.0.0-rc",
            Url = FallbackUrl("2.0.0-rc"),
            IsPrerelease = true
        },
    });

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

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Unicode)]
    private static extern int fs_tgws_start(
        [MarshalAs(UnmanagedType.LPWStr)] string exe,
        [MarshalAs(UnmanagedType.LPWStr)] string dir);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Unicode)]
    private static extern int fs_tgws_stop(
        [MarshalAs(UnmanagedType.LPWStr)] string dir);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Unicode)]
    private static extern int fs_tgws_state(
        [MarshalAs(UnmanagedType.LPWStr)] string dir,
        [Out] byte[] output, int outputLen);

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

    // ---------- TG WS Proxy (официальный exe Flowseal) ----------

    public (int State, string Detail) GetTgWsState(string dir)
    {
        var buffer = new byte[512];
        int state = fs_tgws_state(dir ?? "", buffer, buffer.Length);
        return (state, Decode(buffer));
    }

    public (bool Ok, string Error) StartTgWs(string exe, string dir)
    {
        int rc = fs_tgws_start(exe ?? "", dir ?? "");
        return (rc == FsOk, LastError());
    }

    public void StopTgWs(string dir) => fs_tgws_stop(dir ?? "");

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

    // Версия нужна только для сортировки и выбора рекомендуемого релиза.
    // Поддерживаются и "1.10.4", и "v2.0.0-rc", и старые "1.9.9d".
    private sealed class ZapretVersion : IComparable<ZapretVersion>
    {
        public int Major { get; init; }
        public int Minor { get; init; }
        public int Patch { get; init; }
        public bool Prerelease { get; init; }
        public string Suffix { get; init; } = "";

        public static bool LooksPrerelease(string text)
        {
            if (string.IsNullOrEmpty(text))
                return false;

            string s = text.ToLowerInvariant();
            return s.StartsWith("-") || s.Contains("rc") || s.Contains("beta") ||
                   s.Contains("alpha") || s.Contains("pre") || s.Contains("dev") ||
                   s.Contains("nightly");
        }

        public static ZapretVersion Parse(string tag, bool prereleaseHint)
        {
            if (string.IsNullOrWhiteSpace(tag))
                return null;

            string s = NormalizeTag(tag);
            int i = 0;
            if (!ReadNumber(s, ref i, out int major)) return null;
            if (i >= s.Length || s[i] != '.') return null;
            ++i;
            if (!ReadNumber(s, ref i, out int minor)) return null;
            if (i >= s.Length || s[i] != '.') return null;
            ++i;
            if (!ReadNumber(s, ref i, out int patch)) return null;

            string suffix = s.Substring(i).Trim();
            return new ZapretVersion
            {
                Major = major,
                Minor = minor,
                Patch = patch,
                Suffix = suffix,
                Prerelease = prereleaseHint || LooksPrerelease(suffix)
            };
        }

        private static bool ReadNumber(string text, ref int index, out int value)
        {
            value = 0;
            int start = index;
            while (index < text.Length && char.IsDigit(text[index]))
                ++index;
            if (index == start)
                return false;

            value = int.Parse(text.Substring(start, index - start));
            return true;
        }

        public int CompareTo(ZapretVersion other)
        {
            if (other == null)
                return 1;

            int result = Major.CompareTo(other.Major);
            if (result != 0) return result;
            result = Minor.CompareTo(other.Minor);
            if (result != 0) return result;
            result = Patch.CompareTo(other.Patch);
            if (result != 0) return result;

            // Стабильный релиз всегда выше пре-релиза с тем же номером.
            if (Prerelease != other.Prerelease)
                return Prerelease ? -1 : 1;

            return string.Compare(Suffix, other.Suffix,
                                  StringComparison.OrdinalIgnoreCase);
        }
    }

    private static ZapretVersion VersionOf(ReleaseInfo release) =>
        release == null ? null : ZapretVersion.Parse(release.Tag, release.IsPrerelease);

    private static List<ReleaseInfo> BuildReleaseList(IEnumerable<ReleaseInfo> source)
    {
        var all = source.Where(r => r != null && !string.IsNullOrWhiteSpace(r.Tag))
                        .ToList();
        foreach (var release in all)
            release.IsRecommended = false;

        // Рекомендуем самый новый стабильный релиз. Если стабильных нет —
        // самый новый пре-релиз. Так 2.0.0-rc не вытеснит 1.10.4, пока не станет стабильным.
        ReleaseInfo recommended = all
            .Where(r => !r.IsPrerelease)
            .OrderByDescending(VersionOf)
            .FirstOrDefault();
        if (recommended == null)
            recommended = all.OrderByDescending(VersionOf).FirstOrDefault();
        if (recommended != null)
            recommended.IsRecommended = true;

        var result = new List<ReleaseInfo>();
        if (recommended != null)
            result.Add(recommended);
        result.AddRange(all
            .Where(r => !ReferenceEquals(r, recommended))
            .OrderByDescending(VersionOf)
            .ThenBy(r => r.IsPrerelease ? 1 : 0)
            .ThenByDescending(r => r.Tag, StringComparer.OrdinalIgnoreCase));
        return result;
    }

    /// <summary>Мажорная версия нужна, чтобы отличать стратегии 1.x и 2.x.</summary>
    public int MajorVersion(string tag) =>
        ZapretVersion.Parse(tag, false)?.Major ?? 1;

    public void FetchReleasesAsync()
    {
        _ = Task.Run(async () =>
        {
            try
            {
                using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
                client.DefaultRequestHeaders.Add("User-Agent", "fsclient/1.3.0");
                client.DefaultRequestHeaders.Add("Accept", "application/vnd.github+json");

                string json = await client.GetStringAsync(ReleasesApi);
                using var doc = JsonDocument.Parse(json);

                var found = new List<ReleaseInfo>();
                foreach (var release in doc.RootElement.EnumerateArray())
                {
                    if (found.Count >= 30) break;

                    bool draft = release.TryGetProperty("draft", out var draftElement) &&
                                 draftElement.ValueKind == JsonValueKind.True;
                    if (draft)
                        continue;

                    string tag = release.TryGetProperty("tag_name", out var tagElement)
                        ? tagElement.GetString() ?? ""
                        : "";
                    if (string.IsNullOrWhiteSpace(tag))
                        continue;

                    bool prerelease =
                        (release.TryGetProperty("prerelease", out var preElement) &&
                         preElement.ValueKind == JsonValueKind.True) ||
                        ZapretVersion.LooksPrerelease(tag);

                    string preferredName = "zapret-discord-youtube-" + NormalizeTag(tag) + ".zip";
                    string url = null;
                    string anyZip = null;

                    if (release.TryGetProperty("assets", out var assets) &&
                        assets.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var asset in assets.EnumerateArray())
                        {
                            string name = asset.TryGetProperty("name", out var nameElement)
                                ? nameElement.GetString() ?? ""
                                : "";
                            if (!name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                                continue;

                            string download = asset.TryGetProperty("browser_download_url",
                                                  out var urlElement)
                                ? urlElement.GetString()
                                : null;
                            if (string.IsNullOrEmpty(download))
                                continue;

                            if (string.Equals(name, preferredName,
                                              StringComparison.OrdinalIgnoreCase))
                            {
                                url = download;
                                break;
                            }
                            anyZip ??= download;
                        }
                    }

                    url ??= anyZip;
                    if (string.IsNullOrEmpty(url))
                        continue;

                    found.Add(new ReleaseInfo
                    {
                        Tag = tag,
                        Url = url,
                        IsPrerelease = prerelease
                    });
                }

                if (found.Count == 0)
                    throw new InvalidOperationException("Пустой список релизов");

                var result = BuildReleaseList(found);
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

    // Старые сборки могли сохранить папку как "v1.10.4"; GitHub использует
    // "1.10.4". Приводим оба варианта к одному имени.
    private static string NormalizeTag(string tag)
    {
        if (string.IsNullOrEmpty(tag))
            return "";

        tag = tag.Trim();
        if (tag.Length > 1 && (tag[0] == 'v' || tag[0] == 'V') && char.IsDigit(tag[1]))
            return tag[1..];
        return tag;
    }

    public bool IsDownloaded(string tag) =>
        !string.IsNullOrEmpty(tag) && Directory.Exists(Path.Combine(ReleasesDir, NormalizeTag(tag)));

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
        string dir = string.IsNullOrEmpty(tag) ? "" : Path.Combine(ReleasesDir, NormalizeTag(tag));
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
                string folderTag = NormalizeTag(tag);
                Directory.CreateDirectory(ReleasesDir);
                string targetDir = Path.Combine(ReleasesDir, folderTag);
                string zipPath = Path.Combine(ReleasesDir, folderTag + ".zip");

                Ui(() => DownloadProgress?.Invoke(0, "Скачивание архива..."));

                using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
                client.DefaultRequestHeaders.Add("User-Agent", "fsclient/1.3.0");
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
                    // И 1.10.x, и 2.0.0-rc приходят одним верхним каталогом.
                    string wrapper = entries[0];
                    MoveTree(wrapper, targetDir);
                    Directory.Delete(wrapper, true);
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

    private static void MoveTree(string source, string destinationRoot)
    {
        Directory.CreateDirectory(destinationRoot);
        foreach (string file in Directory.GetFiles(source))
        {
            File.Move(file, Path.Combine(destinationRoot, Path.GetFileName(file)), true);
        }
        foreach (string dir in Directory.GetDirectories(source))
        {
            MoveTree(dir, Path.Combine(destinationRoot, Path.GetFileName(dir)));
        }
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
        Path.GetFullPath(Path.Combine(ReleasesDir, NormalizeTag(tag ?? "")));

    public void OpenFolder(string tag)
    {
        string dir = string.IsNullOrEmpty(tag) ? ReleasesDir : ReleaseDir(tag);
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
