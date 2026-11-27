using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;

namespace FsClient;

/// <summary>
/// Настройки лаунчера. settings.json лежит в bootstrap-папке рядом с
/// распакованным приложением — оттуда лаунчер узнаёт, где искать данные.
/// </summary>
public sealed class AppSettings
{
    public string DataRoot { get; set; } = "";
    public double BlurRadius { get; set; } = 18;
    public string WindowTitle { get; set; } = "fsclient 1.2.0";

    /// <summary>Имя файла в папке assets внутри DataRoot. Пусто = встроенный фон.</summary>
    public string BackgroundImage { get; set; } = "";

    public AppSettings Clone() => new()
    {
        DataRoot = DataRoot,
        BlurRadius = BlurRadius,
        WindowTitle = WindowTitle,
        BackgroundImage = BackgroundImage
    };
}

public static class SettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    /// <summary>Старая папка: здесь остаётся только конфиг и бинарники лаучера.</summary>
    public static string BootstrapDir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "fsclient", "1.1.0");

    private static string SettingsPath => Path.Combine(BootstrapDir, "settings.json");

    public static AppSettings Current { get; private set; }

    public static string DataRoot => Current.DataRoot;
    public static string ReleasesDir => Path.Combine(DataRoot, "releases");
    public static string PluginsDir => Path.Combine(DataRoot, "plugins");
    public static string LogsDir => Path.Combine(DataRoot, "logs");
    public static string AssetsDir => Path.Combine(DataRoot, "assets");

    public static void Initialize()
    {
        Directory.CreateDirectory(BootstrapDir);

        bool firstRun = !File.Exists(SettingsPath);
        if (firstRun)
        {
            Current = new AppSettings { DataRoot = DefaultDataRoot() };
            MigrateLegacyData();
            Save();
        }
        else
        {
            try
            {
                Current = JsonSerializer.Deserialize<AppSettings>(
                    File.ReadAllText(SettingsPath), JsonOptions) ?? new AppSettings();
            }
            catch
            {
                Current = new AppSettings { DataRoot = DefaultDataRoot() };
            }

            if (string.IsNullOrWhiteSpace(Current.DataRoot))
                Current.DataRoot = DefaultDataRoot();
            if (Current.BlurRadius < 0 || Current.BlurRadius > 30)
                Current.BlurRadius = 18;
            if (string.IsNullOrWhiteSpace(Current.WindowTitle))
                Current.WindowTitle = "fsclient 1.2.0";
        }

        EnsureDirectories();
    }

    private static string DefaultDataRoot()
    {
        string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        if (string.IsNullOrWhiteSpace(programFiles))
            programFiles = Path.Combine(BootstrapDir, "data");
        return Path.Combine(programFiles, "fsclient", "1.1.0");
    }

    private static void EnsureDirectories()
    {
        Directory.CreateDirectory(DataRoot);
        Directory.CreateDirectory(ReleasesDir);
        Directory.CreateDirectory(PluginsDir);
        Directory.CreateDirectory(LogsDir);
        Directory.CreateDirectory(AssetsDir);
    }

    /// <summary>
    /// При первом запуске новой версии забираем уже скачанные релизы из
    /// %LOCALAPPDATA%\fsclient\1.1.0\app, чтобы не скачивать всё заново.
    /// </summary>
    private static void MigrateLegacyData()
    {
        string legacyApp = Path.Combine(BootstrapDir, "app");
        foreach (string name in new[] { "releases", "plugins", "logs", "tg-ws-proxy" })
        {
            string source = Path.Combine(legacyApp, name);
            string target = Path.Combine(DataRoot, name);
            if (!Directory.Exists(source))
                continue;

            try
            {
                if (HasAnyEntry(source))
                    CopyTreeVerified(source, target);
                Directory.Delete(source, true);
            }
            catch
            {
                // Миграция best effort: при ошибке старые данные остаются на месте.
            }
        }
    }

    private static bool HasAnyEntry(string dir) =>
        Directory.EnumerateFileSystemEntries(dir).Any();

    private static List<(string Relative, long Length)> Manifest(string root)
    {
        var result = new List<(string, long)>();
        foreach (string file in Directory.GetFiles(root, "*", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(root, file);
            result.Add((relative, new FileInfo(file).Length));
        }
        return result;
    }

    /// <summary>Копирует дерево и проверяет результат до удаления источника.</summary>
    private static void CopyTreeVerified(string source, string destination,
        IProgress<string> progress = null)
    {
        Directory.CreateDirectory(destination);

        foreach (string dir in Directory.GetDirectories(source, "*", SearchOption.AllDirectories))
        {
            string target = Path.Combine(destination, Path.GetRelativePath(source, dir));
            Directory.CreateDirectory(target);
        }

        var sourceFiles = new List<(string Relative, long Length)>();
        foreach (string file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(source, file);
            string target = Path.Combine(destination, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target));
            File.Copy(file, target, true);
            sourceFiles.Add((relative, new FileInfo(file).Length));
            progress?.Report(relative);
        }

        var targetManifest = Manifest(destination).ToDictionary(x => x.Relative, x => x.Length);
        foreach (var (relative, length) in sourceFiles)
        {
            if (!targetManifest.TryGetValue(relative, out long copied) || copied != length)
                throw new IOException($"Файл не скопировался: {relative}");
        }
    }

    private static bool IsInside(string child, string parent)
    {
        string c = Path.GetFullPath(child).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string p = Path.GetFullPath(parent).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return c.StartsWith(p, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Копирует всё в новую папку, проверяет копию и только потом удаляет старую.
    /// Конфиг остаётся в BootstrapDir и переносу не подвергается.
    /// </summary>
    public static async Task ChangeDataRootAsync(string newRoot, IProgress<string> progress)
    {
        if (string.IsNullOrWhiteSpace(newRoot))
            throw new ArgumentException("Путь не указан");

        string target = Path.GetFullPath(newRoot.Trim());
        string old = DataRoot;
        if (string.Equals(old, target, StringComparison.OrdinalIgnoreCase))
            return;

        if (IsInside(target, old) || IsInside(old, target))
            throw new InvalidOperationException(
                "Новая папка не должна лежать внутри старой (и наоборот).");

        Directory.CreateDirectory(target);

        await Task.Run(() =>
        {
            progress?.Report("Копирование файлов...");
            CopyTreeVerified(old, target, progress);
        });

        bool deleted = false;
        await Task.Run(() =>
        {
            progress?.Report("Удаление старой папки...");
            for (int attempt = 0; attempt < 3 && !deleted; attempt++)
            {
                try
                {
                    Directory.Delete(old, true);
                    deleted = true;
                }
                catch (IOException)
                {
                    Thread.Sleep(250);
                }
                catch (UnauthorizedAccessException)
                {
                    Thread.Sleep(250);
                }
            }
        });

        Current.DataRoot = target;
        EnsureDirectories();
        Save();

        if (!deleted)
            throw new IOException(
                $"Файлы скопированы, но старую папку удалить не удалось: {old}");
    }

    /// <summary>Применяет изменённые настройки и сохраняет settings.json.</summary>
    public static void Apply(AppSettings settings)
    {
        Current = settings ?? throw new ArgumentNullException(nameof(settings));
        if (string.IsNullOrWhiteSpace(Current.DataRoot))
            Current.DataRoot = DefaultDataRoot();
        EnsureDirectories();
        Save();
    }

    private static void Save()
    {
        Directory.CreateDirectory(BootstrapDir);
        File.WriteAllText(SettingsPath, JsonSerializer.Serialize(Current, JsonOptions));
    }
}
