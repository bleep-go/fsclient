using System.Windows;

namespace FsClient;

public partial class App : System.Windows.Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        try
        {
            SettingsService.Initialize();
            LaunchLog.Start();
            Backend.Initialize();
            TgWsProxyService.Initialize();
        }
        catch (System.Exception ex)
        {
            LaunchLog.Write("Ошибка запуска: " + ex.Message);
            System.Windows.MessageBox.Show(
                "Не удалось загрузить бэкенд fs_backend.dll.\n\n" + ex.Message,
                "fsclient",
                MessageBoxButton.OK,
                MessageBoxImage.Error
            );
            Shutdown(1);
            return;
        }

        var (ok, _) = Backend.Instance.AcquireSingleInstance();
        if (!ok)
        {
            System.Windows.MessageBox.Show(
                "Другой экземпляр fsclient уже запущен.\nПроверьте значок приложения в трее.",
                "fsclient",
                MessageBoxButton.OK,
                MessageBoxImage.Warning
            );
            Backend.Shutdown();
            Shutdown(1);
            return;
        }

        var window = new MainWindow();
        MainWindow = window;
        window.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        LaunchLog.Shutdown();
        Backend.Shutdown();
        base.OnExit(e);
    }
}
