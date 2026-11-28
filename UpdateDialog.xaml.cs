using System;
using System.Diagnostics;
using System.Windows;

namespace FsClient;

public partial class UpdateDialog : Window
{
    private readonly LauncherUpdate _update;

    public bool InstallRequested { get; private set; }

    public UpdateDialog(Window owner, LauncherUpdate update, bool showInstallButton)
    {
        InitializeComponent();
        Owner = owner;
        _update = update ?? throw new ArgumentNullException(nameof(update));

        TitleText.Text = update.IsPrerelease
            ? $"Доступен пре-релиз {update.Version}"
            : $"Доступно обновление {update.Version}";
        SubtitleText.Text = $"{update.Tag} · " +
            (update.IsPrerelease ? "пре-релиз · " : "") +
            "ченджлог загружен из GitHub Releases";
        VersionBadge.Text = update.IsPrerelease ? "PRE " + update.Tag : update.Tag;

        NotesViewer.Document = MarkdownRenderer.Render(update.Notes);

        InstallButton.Visibility = showInstallButton ? Visibility.Visible : Visibility.Collapsed;
        AutoHint.Visibility = showInstallButton ? Visibility.Collapsed : Visibility.Visible;
        CancelButton.Content = showInstallButton ? "Позже" : "Закрыть";

        if (update.IsPrerelease && !showInstallButton)
            AutoHint.Text = "Пре-релиз не устанавливается автоматически. Нажмите «Обновить», чтобы установить его вручную.";

        string repo = string.IsNullOrWhiteSpace(SettingsService.Current?.UpdateRepo)
            ? "bleep-go/fsclient"
            : SettingsService.Current.UpdateRepo.Trim().Trim('/');
        string tag = Uri.EscapeDataString(update.Tag ?? "");
        if (Uri.TryCreate(
                $"https://github.com/{repo}/releases/tag/{tag}",
                UriKind.Absolute,
                out Uri releaseUri))
        {
            ReleaseLink.NavigateUri = releaseUri;
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        InstallRequested = false;
        DialogResult = false;
        Close();
    }

    private void Install_Click(object sender, RoutedEventArgs e)
    {
        InstallRequested = true;
        DialogResult = true;
        Close();
    }

    private void ReleaseLink_Click(object sender, RoutedEventArgs e)
    {
        Uri uri = ReleaseLink.NavigateUri;
        if (uri == null)
            return;

        try
        {
            Process.Start(new ProcessStartInfo(uri.ToString())
            {
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            AppendDialogError(ex.Message);
        }
        e.Handled = true;
    }

    private void AppendDialogError(string message)
    {
        SubtitleText.Text = "Не удалось открыть ссылку: " + message;
    }
}
