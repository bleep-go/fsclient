using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace FsClient;

/// <summary>
/// Общий лог лаунчера: в файле остаются последние три запуска.
/// Путь берётся из текущей папки данных, поэтому при смене папки лог едет вместе с ней.
/// </summary>
public static class LaunchLog
{
    private const string SessionMarker = "=== ЗАПУСК ";
    private const int KeepSessions = 3;
    private static readonly object Sync = new();

    private static string LogPath
    {
        get
        {
            try
            {
                return Path.Combine(SettingsService.LogsDir, "launch.log");
            }
            catch
            {
                // Конфиг ещё не загрузился — пишем во временную папку, чтобы
                // ошибка запуска не терялась из-за отсутствия лога.
                return Path.Combine(
                    Path.GetTempPath(), "fsclient", "logs", "launch.log");
            }
        }
    }

    public static void Start()
    {
        lock (Sync)
        {
            string path = LogPath;
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            AppendRaw($"{SessionMarker}{DateTime.Now:yyyy-MM-dd HH:mm:ss} ===");
            // Обрезаем уже с учётом только что добавленной сессии.
            TrimToLastSessions();
        }
    }

    public static void Write(string message)
    {
        AppendRaw($"[{DateTime.Now:HH:mm:ss}] {message}");
    }

    public static void Shutdown()
    {
        AppendRaw("=== ЗАВЕРШЕНИЕ ===");
    }

    private static void AppendRaw(string line)
    {
        lock (Sync)
        {
            try
            {
                string path = LogPath;
                string text = line + Environment.NewLine;

                // Если файл оборвался без перевода строки, отделяем новую
                // запись — иначе маркер сессии склеится с предыдущей строкой.
                if (File.Exists(path) && new FileInfo(path).Length > 0)
                {
                    using var stream = File.Open(path, FileMode.Open, FileAccess.Read,
                        FileShare.ReadWrite);
                    stream.Seek(-1, SeekOrigin.End);
                    int last = stream.ReadByte();
                    if (last != '\n' && last != '\r')
                        text = Environment.NewLine + text;
                }

                File.AppendAllText(path, text, Encoding.UTF8);
            }
            catch
            {
                // Логирование не должно валить приложение.
            }
        }
    }

    private static void TrimToLastSessions()
    {
        try
        {
            if (!File.Exists(LogPath))
                return;

            string[] lines = File.ReadAllLines(LogPath, Encoding.UTF8);
            var starts = new List<int>();
            for (int i = 0; i < lines.Length; i++)
            {
                if (lines[i].StartsWith(SessionMarker, StringComparison.Ordinal))
                    starts.Add(i);
            }

            if (starts.Count <= KeepSessions)
                return;

            int cut = starts[starts.Count - KeepSessions];
            File.WriteAllLines(LogPath, lines.Skip(cut), Encoding.UTF8);
        }
        catch
        {
            // best effort
        }
    }
}
