using System.IO;
using System.Reflection;

namespace FsClient;

/// <summary>
/// Достаёт DLL бэкенда и иконку из встроенных ресурсов автономного EXE.
/// При обычной сборке файлы лежат рядом и этот класс не используется.
/// </summary>
internal static class BundledResources
{
    private static readonly object Sync = new();
    private static string _folder;

    private static string Folder
    {
        get
        {
            if (_folder != null)
                return _folder;

            lock (Sync)
            {
                if (_folder != null)
                    return _folder;

                // Рядом с уже распакованным приложением — без новых записей в LocalAppData.
                string path = AppContext.BaseDirectory;

                System.IO.Directory.CreateDirectory(path);
                _folder = path;
                return path;
            }
        }
    }

    public static string Extract(string resourceName, string fileName)
    {
        using Stream stream = typeof(BundledResources).Assembly.GetManifestResourceStream(resourceName);
        if (stream == null)
            throw new FileNotFoundException($"Встроенный ресурс не найден: {resourceName}");

        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        byte[] data = memory.ToArray();

        string target = Path.Combine(Folder, fileName);
        if (!File.Exists(target) || new FileInfo(target).Length != data.Length)
        {
            try
            {
                File.WriteAllBytes(target, data);
            }
            catch (IOException)
            {
                // Файл занят другим запущенным экземпляром — берём уникальную копию.
                string unique = Path.Combine(Path.GetTempPath(), $"fsclient-{Guid.NewGuid():N}");
                System.IO.Directory.CreateDirectory(unique);
                target = Path.Combine(unique, fileName);
                File.WriteAllBytes(target, data);
            }
        }

        return target;
    }
}
