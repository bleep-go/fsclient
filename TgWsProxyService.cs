using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FsClient;

public sealed class TgReleaseInfo
{
    public string Tag { get; init; } = "";
    public string Url { get; init; } = "";
    public string Digest { get; init; } = "";
    public bool IsLatest { get; init; }
}

/// <summary>
/// Формат совпадает с %APPDATA%\TgWsProxy\config.json оригинального
/// приложения Flowseal. Лаунчер пишет его сам, чтобы не открывать Python-GUI.
/// </summary>
public sealed class TgWsConfig
{
    [JsonPropertyName("host")] public string Host { get; set; } = "127.0.0.1";
    [JsonPropertyName("port")] public int Port { get; set; } = 1443;
    [JsonPropertyName("secret")] public string Secret { get; set; } = TgWsProxyService.NewSecret();
    [JsonPropertyName("dc_ip")] public string[] DcIp { get; set; } =
        { "2:149.154.167.220", "4:149.154.167.220" };
    [JsonPropertyName("verbose")] public bool Verbose { get; set; }
    [JsonPropertyName("check_updates")] public bool CheckUpdates { get; set; }
    [JsonPropertyName("log_max_mb")] public int LogMaxMb { get; set; } = 5;
    [JsonPropertyName("buf_kb")] public int BufKb { get; set; } = 256;
    [JsonPropertyName("pool_size")] public int PoolSize { get; set; } = 4;
    [JsonPropertyName("cfproxy")] public bool CfProxy { get; set; } = true;
    [JsonPropertyName("h2")] public bool H2 { get; set; } = true;
    [JsonPropertyName("cfproxy_user_domain_enabled")] public bool CfProxyUserDomainEnabled { get; set; }
    [JsonPropertyName("cfproxy_user_domain")] public string[] CfProxyUserDomain { get; set; } = Array.Empty<string>();
    [JsonPropertyName("cfproxy_worker_enabled")] public bool CfProxyWorkerEnabled { get; set; }
    [JsonPropertyName("cfproxy_worker_domain")] public string[] CfProxyWorkerDomain { get; set; } = Array.Empty<string>();
    [JsonPropertyName("force_test_dc")] public bool ForceTestDc { get; set; }
    [JsonPropertyName("no_secure")] public bool NoSecure { get; set; }
    [JsonPropertyName("appearance")] public string Appearance { get; set; } = "auto";
    [JsonPropertyName("language")] public string Language { get; set; } = "ru";
    [JsonPropertyName("autostart")] public bool Autostart { get; set; }
}

/// <summary>
/// Управляет официальными сборками Flowseal: версии, конфиг и процесс.
/// Свой MTProto-код здесь не реализован — работает скачанный exe.
/// </summary>
public sealed class TgWsProxyService
{
    private const string ReleasesApi =
        "https://api.github.com/repos/Flowseal/tg-ws-proxy/releases?per_page=15";
    private const string LegacyConfigName = "config.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    public static TgWsProxyService Instance { get; } = new();

    private TgWsProxyService() { }

    public event Action<List<TgReleaseInfo>> ReleasesReady;
    public event Action<string> Log;
    public event Action<int, string> DownloadProgress;
    public event Action<string> DownloadDone;
    public event Action<string> DownloadFailed;

    public List<TgReleaseInfo> Releases { get; private set; } = new();

    public static string Root => Path.Combine(SettingsService.DataRoot, "tg-ws-proxy");
    public static string ReleasesDir => Path.Combine(Root, "releases");

    public static string ReleaseDir(string tag) =>
        Path.Combine(ReleasesDir, NormalizeTag(tag));

    public static string DataDir(string tag) =>
        Path.Combine(ReleaseDir(tag), "TgWsProxy_data");

    public static string ConfigPath(string tag) =>
        Path.Combine(DataDir(tag), LegacyConfigName);

    public static string LogPath(string tag) =>
        Path.Combine(DataDir(tag), "proxy.log");

    // GetHexString(length) возвращает количество СИМВОЛОВ, а не байт.
    // MTProto-секрет — это 16 байт = 32 hex-символа.
    public static string NewSecret() => RandomNumberGenerator.GetHexString(32).ToLowerInvariant();

    private static bool IsValidSecret(string secret)
    {
        if (secret == null || secret.Length != 32)
            return false;
        foreach (char c in secret)
        {
            if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') ||
                  (c >= 'A' && c <= 'F')))
                return false;
        }
        return true;
    }

    public static void Initialize()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(ReleasesDir);
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

    private static string NormalizeTag(string tag)
    {
        if (string.IsNullOrWhiteSpace(tag))
            return "";
        tag = tag.Trim();
        if (tag.Length > 1 && (tag[0] == 'v' || tag[0] == 'V') && char.IsDigit(tag[1]))
            return tag[1..];
        return tag;
    }

    private static string[] PreferredAssets()
    {
        return RuntimeInformation.ProcessArchitecture == Architecture.Arm64
            ? new[] { "TgWsProxy_windows_arm64.exe", "TgWsProxy_windows.exe" }
            : new[] { "TgWsProxy_windows.exe", "TgWsProxy_windows_7_64bit.exe" };
    }

    public bool IsDownloaded(string tag)
    {
        return FindExecutable(tag) != null;
    }

    public string FindExecutable(string tag)
    {
        string dir = ReleaseDir(tag);
        foreach (string name in PreferredAssets())
        {
            string path = Path.Combine(dir, name);
            if (File.Exists(path) && new FileInfo(path).Length > 0)
                return path;
        }
        return null;
    }

    public void FetchReleasesAsync()
    {
        _ = Task.Run(async () =>
        {
            try
            {
                using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
                client.DefaultRequestHeaders.Add("User-Agent", "fsclient/1.2.0");
                client.DefaultRequestHeaders.Add("Accept", "application/vnd.github+json");

                string json = await client.GetStringAsync(ReleasesApi);
                using var doc = JsonDocument.Parse(json);
                string[] preferred = PreferredAssets();
                var result = new List<TgReleaseInfo>();

                foreach (var release in doc.RootElement.EnumerateArray())
                {
                    if (result.Count >= 15)
                        break;

                    string tag = release.TryGetProperty("tag_name", out var tagElement)
                        ? tagElement.GetString() ?? ""
                        : "";
                    if (string.IsNullOrWhiteSpace(tag))
                        continue;

                    string url = "";
                    string digest = "";
                    if (release.TryGetProperty("assets", out var assets) &&
                        assets.ValueKind == JsonValueKind.Array)
                    {
                        foreach (string wanted in preferred)
                        {
                            foreach (var asset in assets.EnumerateArray())
                            {
                                string name = asset.TryGetProperty("name", out var nameElement)
                                    ? nameElement.GetString() ?? ""
                                    : "";
                                if (!string.Equals(name, wanted, StringComparison.OrdinalIgnoreCase))
                                    continue;

                                url = asset.TryGetProperty("browser_download_url", out var urlElement)
                                    ? urlElement.GetString() ?? ""
                                    : "";
                                digest = asset.TryGetProperty("digest", out var digestElement)
                                    ? digestElement.GetString() ?? ""
                                    : "";
                                break;
                            }
                            if (!string.IsNullOrEmpty(url))
                                break;
                        }
                    }

                    if (string.IsNullOrEmpty(url))
                        continue;

                    result.Add(new TgReleaseInfo
                    {
                        Tag = NormalizeTag(tag),
                        Url = url,
                        Digest = digest,
                        IsLatest = result.Count == 0
                    });
                }

                if (result.Count == 0)
                    throw new InvalidOperationException("В релизах нет Windows-сборки");

                Releases = result;
                Ui(() => ReleasesReady?.Invoke(result));
            }
            catch (Exception ex)
            {
                Releases = FallbackReleases();
                Ui(() =>
                {
                    ReleasesReady?.Invoke(Releases);
                    Log?.Invoke("TG WS Proxy: GitHub недоступен, показан резервный список — " + ex.Message);
                });
            }
        });
    }

    private static List<TgReleaseInfo> FallbackReleases()
    {
        string[] tags = { "1.12.0", "1.11.4", "1.11.3", "1.11.2", "1.11.1", "1.11.0" };
        string asset = PreferredAssets()[0];
        return tags.Select((tag, index) => new TgReleaseInfo
        {
            Tag = tag,
            Url = $"https://github.com/Flowseal/tg-ws-proxy/releases/download/v{tag}/{asset}",
            IsLatest = index == 0
        }).ToList();
    }

    public void DownloadRelease(TgReleaseInfo release)
    {
        if (release == null || string.IsNullOrEmpty(release.Url))
        {
            Ui(() => DownloadFailed?.Invoke("Нет ссылки на сборку"));
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                string tag = NormalizeTag(release.Tag);
                Directory.CreateDirectory(ReleasesDir);
                string temp = Path.Combine(ReleasesDir, tag + ".exe.tmp");

                Ui(() => DownloadProgress?.Invoke(0, "Скачивание сборки..."));

                using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
                client.DefaultRequestHeaders.Add("User-Agent", "fsclient/1.2.0");
                using var response = await client.GetAsync(
                    release.Url, HttpCompletionOption.ResponseHeadersRead);
                response.EnsureSuccessStatusCode();

                long total = response.Content.Headers.ContentLength ?? 0;
                long downloaded = 0;
                var buffer = new byte[65536];
                await using (var source = await response.Content.ReadAsStreamAsync())
                await using (var target = File.Create(temp))
                {
                    int read;
                    while ((read = await source.ReadAsync(buffer)) > 0)
                    {
                        await target.WriteAsync(buffer.AsMemory(0, read));
                        downloaded += read;
                        if (total > 0)
                        {
                            int pct = (int)Math.Min(90, downloaded * 90 / total);
                            Ui(() => DownloadProgress?.Invoke(pct, "Скачивание сборки..."));
                        }
                    }
                }

                VerifyDigest(temp, release.Digest);
                Ui(() => DownloadProgress?.Invoke(95, "Подготовка конфигурации..."));

                string dir = ReleaseDir(tag);
                Directory.CreateDirectory(dir);
                string name = Path.GetFileName(new Uri(release.Url).LocalPath);
                if (string.IsNullOrEmpty(name))
                    name = PreferredAssets()[0];
                string destination = Path.Combine(dir, name);
                File.Move(temp, destination, true);
                EnsureConfig(tag);

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

    private static void VerifyDigest(string path, string digest)
    {
        if (string.IsNullOrWhiteSpace(digest) ||
            !digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
            return;

        string expected = digest["sha256:".Length..].Trim();
        if (expected.Length != 64)
            return;

        using var sha = SHA256.Create();
        using var stream = File.OpenRead(path);
        string actual = Convert.ToHexString(sha.ComputeHash(stream));
        if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
            throw new IOException("SHA-256 скачанной сборки не совпал с GitHub");
    }

    /// <summary>
    /// Готовит портативную папку: конфиг, secret и маркеры, чтобы оригинальное
    /// приложение не показывало свой Python-интерфейс первого запуска.
    /// </summary>
    public void EnsureConfig(string tag)
    {
        string dir = DataDir(tag);
        Directory.CreateDirectory(dir);

        string config = ConfigPath(tag);
        TgWsConfig cfg;
        if (File.Exists(config))
        {
            cfg = LoadConfig(tag);
        }
        else
        {
            string legacy = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "TgWsProxy", LegacyConfigName);
            cfg = File.Exists(legacy) ? LoadConfigFile(legacy) : new TgWsConfig();
        }

        // Секрет из старого конфига мог быть записан коротким (16 символов).
        // Telegram принимает только 32 hex-символа, поэтому чиним и существующий конфиг.
        if (!IsValidSecret(cfg.Secret))
            cfg.Secret = NewSecret();

        // Обновления и диалоги оригинального приложения полностью ведёт лаунчер.
        cfg.CheckUpdates = false;
        cfg.Verbose = false;
        cfg.Autostart = false;
        cfg.DcIp ??= new[] { "2:149.154.167.220", "4:149.154.167.220" };
        cfg.CfProxyUserDomain ??= Array.Empty<string>();
        cfg.CfProxyWorkerDomain ??= Array.Empty<string>();
        cfg.Port = Math.Clamp(cfg.Port, 1, 65535);
        SaveConfig(tag, cfg);

        Touch(Path.Combine(dir, ".first_run_done_mtproto"));
        Touch(Path.Combine(dir, ".ipv6_warned"));
    }

    private static void Touch(string path)
    {
        if (File.Exists(path))
            return;
        try
        {
            using var stream = File.Create(path);
            stream.Close();
        }
        catch
        {
            // Маркеры не критичны для запуска прокси.
        }
    }

    private static TgWsConfig LoadConfigFile(string path)
    {
        try
        {
            return JsonSerializer.Deserialize<TgWsConfig>(
                File.ReadAllText(path), JsonOptions) ?? new TgWsConfig();
        }
        catch
        {
            return new TgWsConfig();
        }
    }

    public TgWsConfig LoadConfig(string tag) => LoadConfigFile(ConfigPath(tag));

    public void SaveConfig(string tag, TgWsConfig cfg)
    {
        Directory.CreateDirectory(DataDir(tag));
        File.WriteAllText(ConfigPath(tag),
            JsonSerializer.Serialize(cfg, JsonOptions));
    }

    public string RegenerateSecret(string tag)
    {
        var cfg = LoadConfig(tag);
        cfg.Secret = NewSecret();
        SaveConfig(tag, cfg);
        return cfg.Secret;
    }

    public string Link(string tag)
    {
        // На всякий случай нормализуем конфиг: ссылка всегда строится из
        // валидного 32-символьного секрета, даже если прокси ещё не запускался.
        if (!string.IsNullOrEmpty(tag))
            EnsureConfig(tag);
        var cfg = LoadConfig(tag);
        string host = cfg.Host;
        if (string.Equals(host, "0.0.0.0", StringComparison.Ordinal))
            host = LocalAddress();
        return $"tg://proxy?server={host}&port={cfg.Port}&secret=dd{cfg.Secret}";
    }

    private static string LocalAddress()
    {
        try
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            socket.Connect("8.8.8.8", 80);
            return (socket.LocalEndPoint as System.Net.IPEndPoint)?.Address.ToString() ?? "127.0.0.1";
        }
        catch
        {
            return "127.0.0.1";
        }
    }

    public (bool Ok, string Error) Start(string tag)
    {
        string exe = FindExecutable(tag);
        if (exe == null)
            return (false, "Сначала скачайте сборку " + tag);

        EnsureConfig(tag);
        return Backend.Instance.StartTgWs(exe, ReleaseDir(tag));
    }

    public void Stop() => Backend.Instance.StopTgWs(Root);

    public (int State, string Detail) State()
    {
        Directory.CreateDirectory(Root);
        return Backend.Instance.GetTgWsState(Root);
    }

    public string TailLog(string tag, int maxLines = 200)
    {
        string path = LogPath(tag);
        if (!File.Exists(path))
            return "";

        try
        {
            var lines = new Queue<string>();
            foreach (string line in File.ReadLines(path))
            {
                lines.Enqueue(line);
                if (lines.Count > maxLines)
                    lines.Dequeue();
            }
            return string.Join(Environment.NewLine, lines);
        }
        catch
        {
            return "";
        }
    }

    public static void OpenFolder(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true
            });
        }
        catch
        {
            // Пользователь может закрыть окно проводника — это не ошибка лаучера.
        }
    }

    public static void OpenLog(string tag)
    {
        string path = LogPath(tag);
        if (!File.Exists(path))
            return;
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true
            });
        }
        catch
        {
            // Лог может быть заблокирован прокси.
        }
    }

    public static void OpenUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return;
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true
            });
        }
        catch
        {
            // Нет ассоциированного приложения — ссылка остаётся в поле.
        }
    }

    public static void CopyToClipboard(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return;
        try
        {
            System.Windows.Forms.Clipboard.SetText(text);
        }
        catch
        {
            // Буфер обмена может быть занят другим процессом.
        }
    }
}
