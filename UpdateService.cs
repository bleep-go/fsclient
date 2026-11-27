using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;

namespace FsClient;

public sealed class LauncherUpdate
{
    public string Tag { get; init; } = "";
    public Version Version { get; init; } = new(0, 0);
    public string Url { get; init; } = "";
    public string Digest { get; init; } = "";
    public string Notes { get; init; } = "";
}

/// <summary>
/// Проверяет релизы самого лаунчера на GitHub и скачивает новый fsclient.exe.
/// Обновление ставит updater.exe — он заменяет файл и перезапускает программу.
/// </summary>
public static class UpdateService
{
    private const string AssetName = "fsclient.exe";
    private const string UserAgent = "fsclient/1.3.0";

    public static Version CurrentVersion { get; } =
        Assembly.GetExecutingAssembly().GetName().Version ?? new Version(1, 3, 0);

    private static string Repo
    {
        get
        {
            string repo = SettingsService.Current?.UpdateRepo;
            return string.IsNullOrWhiteSpace(repo)
                ? "BleepGo/fsclient"
                : repo.Trim().Trim('/');
        }
    }

    public static async Task<LauncherUpdate> CheckAsync(CancellationToken ct = default)
    {
        string repo = Repo;
        int slash = repo.IndexOf('/');
        if (slash <= 0 || slash == repo.Length - 1 || repo.IndexOf('/', slash + 1) >= 0)
            return null; // Репозиторий обновлений не настроен.

        string api = $"https://api.github.com/repos/{repo}/releases/latest";
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        client.DefaultRequestHeaders.Add("User-Agent", UserAgent);
        client.DefaultRequestHeaders.Add("Accept", "application/vnd.github+json");

        string json;
        try
        {
            json = await client.GetStringAsync(api, ct);
        }
        catch (HttpRequestException ex) when (
            ex.StatusCode == HttpStatusCode.NotFound ||
            ex.StatusCode == HttpStatusCode.Unauthorized ||
            ex.StatusCode == HttpStatusCode.Forbidden)
        {
            // Репозиторий ещё не создан или приватный — это не ошибка для
            // пользователя, просто обновлений пока нет.
            return null;
        }

        using var doc = JsonDocument.Parse(json);
        JsonElement root = doc.RootElement;

        string tag = root.TryGetProperty("tag_name", out var tagElement)
            ? tagElement.GetString() ?? ""
            : "";
        Version version = ParseVersion(tag);
        if (version == null || version <= CurrentVersion)
            return null;

        string url = "";
        string digest = "";
        if (root.TryGetProperty("assets", out var assets) &&
            assets.ValueKind == JsonValueKind.Array)
        {
            foreach (var asset in assets.EnumerateArray())
            {
                string name = asset.TryGetProperty("name", out var nameElement)
                    ? nameElement.GetString() ?? ""
                    : "";
                if (!string.Equals(name, AssetName, StringComparison.OrdinalIgnoreCase))
                    continue;

                url = asset.TryGetProperty("browser_download_url", out var urlElement)
                    ? urlElement.GetString() ?? ""
                    : "";
                digest = asset.TryGetProperty("digest", out var digestElement)
                    ? digestElement.GetString() ?? ""
                    : "";
                break;
            }
        }

        if (string.IsNullOrWhiteSpace(url))
            return null;

        string notes = root.TryGetProperty("body", out var bodyElement)
            ? bodyElement.GetString() ?? ""
            : "";

        return new LauncherUpdate
        {
            Tag = tag.Trim(),
            Version = version,
            Url = url,
            Digest = digest,
            Notes = notes.Trim()
        };
    }

    public static async Task<string> DownloadAsync(
        LauncherUpdate update, IProgress<int> progress, CancellationToken ct = default)
    {
        if (update == null)
            throw new ArgumentNullException(nameof(update));

        string dir = Path.Combine(
            Path.GetTempPath(), "fsclient-update-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, AssetName);

        using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        client.DefaultRequestHeaders.Add("User-Agent", UserAgent);

        using var response = await client.GetAsync(
            update.Url, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        long? total = response.Content.Headers.ContentLength;
        await using (var file = File.Create(path))
        {
            using var stream = await response.Content.ReadAsStreamAsync(ct);
            byte[] buffer = new byte[81920];
            long received = 0;
            int read;
            while ((read = await stream.ReadAsync(buffer, ct)) > 0)
            {
                await file.WriteAsync(buffer.AsMemory(0, read), ct);
                received += read;
                int percent = total.HasValue && total.Value > 0
                    ? (int)Math.Min(100, received * 100 / total.Value)
                    : 0;
                progress?.Report(percent);
            }
        }

        VerifyExecutable(path);
        VerifyDigest(path, update.Digest);
        return path;
    }

    private static Version ParseVersion(string tag)
    {
        string text = tag.Trim().TrimStart('v', 'V');
        int dash = text.IndexOf('-');
        if (dash > 0)
            text = text[..dash];
        return Version.TryParse(text, out var version) ? version : null;
    }

    private static void VerifyExecutable(string path)
    {
        using var stream = File.OpenRead(path);
        byte[] header = new byte[2];
        if (stream.Read(header, 0, 2) != 2 || header[0] != (byte)'M' || header[1] != (byte)'Z')
            throw new InvalidDataException("Скачанный файл не является Windows-исполняемым.");
    }

    private static void VerifyDigest(string path, string digest)
    {
        if (string.IsNullOrWhiteSpace(digest) ||
            !digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
            return;

        string expected = digest["sha256:".Length..].Trim();
        string actual = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
        if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("SHA-256 скачанного файла не совпал с релизом.");
    }
}
