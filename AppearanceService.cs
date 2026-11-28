using System;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace FsClient;

/// <summary>
/// Работа с фоном окна. Картинка грузится через stream с CacheOption.OnLoad,
/// чтобы не блокировать файл — его можно заменить из настроек без перезапуска.
/// </summary>
public static class AppearanceService
{
    public static ImageSource DefaultBackground()
    {
        var image = new BitmapImage();
        image.BeginInit();
        image.UriSource = new Uri("pack://application:,,,/assets/background.jpg", UriKind.Absolute);
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.EndInit();
        image.Freeze();
        return image;
    }

    public static ImageSource LoadBackground(string relativeName)
    {
        if (!string.IsNullOrWhiteSpace(relativeName))
        {
            try
            {
                string path = Path.Combine(SettingsService.AssetsDir, relativeName);
                if (File.Exists(path))
                {
                    var image = new BitmapImage();
                    using var stream = File.OpenRead(path);
                    image.BeginInit();
                    image.CacheOption = BitmapCacheOption.OnLoad;
                    image.StreamSource = stream;
                    image.EndInit();
                    image.Freeze();
                    return image;
                }
            }
            catch
            {
                // Битый или занятый файл — возвращаем встроенный фон.
            }
        }

        return DefaultBackground();
    }

    /// <summary>Копирует выбранную картинку в папку данных и возвращает имя файла.</summary>
    public static string InstallBackground(string sourcePath)
    {
        if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
            return "";

        string extension = Path.GetExtension(sourcePath);
        if (string.IsNullOrWhiteSpace(extension))
            extension = ".png";

        Directory.CreateDirectory(SettingsService.AssetsDir);
        string name = "background-user" + extension;
        string target = Path.Combine(SettingsService.AssetsDir, name);
        File.Copy(sourcePath, target, true);
        return name;
    }
}
