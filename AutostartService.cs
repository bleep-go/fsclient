using System;
using System.Diagnostics;
using System.IO;

namespace FsClient;

/// <summary>
/// Автозапуск через Планировщик задач с высокими привилегиями.
/// Обычный ярлык в Autorun упирается в UAC из-за requireAdministrator
/// в манифесте лаунчера; задача с /RL HIGHEST стартует без запроса.
/// </summary>
public static class AutostartService
{
    public const string TaskName = "fsclient autostart";

    // Метка передаётся launcher.cpp в ядро только при запуске задачей.
    // По ней лаунчер понимает, что это автозапуск, а не ручное открытие.
    public const string ScheduledArg = "--scheduled";

    public static bool IsEnabled()
    {
        var start = new ProcessStartInfo("schtasks.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        start.ArgumentList.Add("/Query");
        start.ArgumentList.Add("/TN");
        start.ArgumentList.Add(TaskName);

        using var process = Process.Start(start);
        if (process == null)
            return false;

        string output = process.StandardOutput.ReadToEnd();
        process.StandardError.ReadToEnd();
        process.WaitForExit();
        return process.ExitCode == 0 &&
               output.IndexOf(TaskName, StringComparison.OrdinalIgnoreCase) >= 0;
    }

    /// <summary>
    /// Проверяет, что задача создана новой версией лаунчера и передаёт метку
    /// автозапуска. Старые задачи без метки пересоздаём при сохранении настроек.
    /// </summary>
    public static bool HasScheduledArg()
    {
        var start = new ProcessStartInfo("schtasks.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        start.ArgumentList.Add("/Query");
        start.ArgumentList.Add("/TN");
        start.ArgumentList.Add(TaskName);
        start.ArgumentList.Add("/XML");

        using var process = Process.Start(start);
        if (process == null)
            return false;

        string output = process.StandardOutput.ReadToEnd();
        process.StandardError.ReadToEnd();
        process.WaitForExit();
        return process.ExitCode == 0 &&
               output.IndexOf(ScheduledArg, StringComparison.OrdinalIgnoreCase) >= 0;
    }

    public static void SetEnabled(bool enabled, string launcherPath)
    {
        if (enabled)
        {
            if (string.IsNullOrWhiteSpace(launcherPath) || !File.Exists(launcherPath))
                throw new FileNotFoundException(
                    "Не найден fsclient.exe — некуда добавлять автозапуск.",
                    launcherPath);

            Run("/Create",
                "/TN", TaskName,
                "/TR", "\"" + Path.GetFullPath(launcherPath) + "\" " + ScheduledArg,
                "/SC", "ONLOGON",
                "/RL", "HIGHEST",
                "/F");
        }
        else
        {
            Run("/Delete", "/TN", TaskName, "/F");
        }
    }

    private static void Run(params string[] arguments)
    {
        var start = new ProcessStartInfo("schtasks.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (string argument in arguments)
            start.ArgumentList.Add(argument);

        using var process = Process.Start(start);
        if (process == null)
            throw new InvalidOperationException("Не удалось запустить schtasks.exe.");

        string output = process.StandardOutput.ReadToEnd();
        string error = process.StandardError.ReadToEnd();
        process.WaitForExit();

        if (process.ExitCode != 0)
        {
            string message = string.IsNullOrWhiteSpace(error) ? output : error;
            throw new InvalidOperationException(
                "Планировщик задач: " + message.Trim());
        }
    }
}
