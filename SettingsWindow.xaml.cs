using System;
using System.IO;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using WinForms = System.Windows.Forms;

namespace FsClient;

public partial class SettingsWindow : Window
{
    private readonly AppSettings _working;
    private string _pickedBackgroundPath;
    private bool _busy;

    public SettingsWindow(Window owner, AppSettings settings)
    {
        InitializeComponent();
        Owner = owner;
        _working = settings ?? throw new ArgumentNullException(nameof(settings));

        DataRootBox.Text = _working.DataRoot;
        BlurSlider.Value = Math.Clamp(_working.BlurRadius, 0, 30);
        TitleBox.Text = _working.WindowTitle;
        BlurValue.Text = ((int)BlurSlider.Value).ToString();
        BackgroundBox.Text = string.IsNullOrWhiteSpace(_working.BackgroundImage)
            ? "Встроенная"
            : _working.BackgroundImage;
        GeneralHint.Text = $"Текущая папка данных: {SettingsService.DataRoot}";

        // Акрил размывает содержимое за окном. Своих слоёв с обоями нет.
        SourceInitialized += (_, _) =>
        {
            var source = PresentationSource.FromVisual(this) as HwndSource;
            if (source == null) return;

            source.CompositionTarget.BackgroundColor = Colors.Transparent;
            // ABGR: 0x66 alpha, 0x20 blue, 0x13 green, 0x14 red.
            AcrylicHelper.Enable(source.Handle, unchecked((int)0x66141320));
        };

        Loaded += (_, _) =>
        {
            if (Owner == null) return;
            Left = Owner.Left + (Owner.ActualWidth - ActualWidth) / 2;
            Top = Owner.Top + (Owner.ActualHeight - ActualHeight) / 2;
        };
    }

    private void ShowPanel(bool general)
    {
        GeneralPanel.Visibility = general ? Visibility.Visible : Visibility.Collapsed;
        AppearancePanel.Visibility = general ? Visibility.Collapsed : Visibility.Visible;
        GeneralTab.Background = general
            ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(64, 124, 92, 255))
            : System.Windows.Media.Brushes.Transparent;
        GeneralTab.Foreground = general
            ? (System.Windows.Media.Brush)FindResource("TextPrimary")
            : (System.Windows.Media.Brush)FindResource("TextSecondary");
        AppearanceTab.Background = general
            ? System.Windows.Media.Brushes.Transparent
            : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(64, 124, 92, 255));
        AppearanceTab.Foreground = general
            ? (System.Windows.Media.Brush)FindResource("TextSecondary")
            : (System.Windows.Media.Brush)FindResource("TextPrimary");
    }

    private void GeneralTab_Click(object sender, RoutedEventArgs e) => ShowPanel(true);

    private void AppearanceTab_Click(object sender, RoutedEventArgs e) => ShowPanel(false);

    private void BlurSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        BlurValue.Text = ((int)BlurSlider.Value).ToString();
        if (_working != null)
            _working.BlurRadius = BlurSlider.Value;
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new WinForms.FolderBrowserDialog
        {
            Description = "Папка для загрузок и локальных файлов fsclient",
            SelectedPath = _working.DataRoot,
            ShowNewFolderButton = true
        };

        if (dialog.ShowDialog() != WinForms.DialogResult.OK)
            return;

        _working.DataRoot = dialog.SelectedPath;
        DataRootBox.Text = dialog.SelectedPath;
        StatusText.Text = "Папка изменена. Нажмите «Сохранить» для переноса файлов.";
    }

    private void PickBackground_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new WinForms.OpenFileDialog
        {
            Title = "Выберите картинку заднего фона",
            Filter = "Изображения|*.png;*.jpg;*.jpeg;*.bmp;*.gif|Все файлы|*.*",
            CheckFileExists = true
        };

        if (dialog.ShowDialog() != WinForms.DialogResult.OK)
            return;

        _pickedBackgroundPath = dialog.FileName;
        BackgroundBox.Text = Path.GetFileName(dialog.FileName) + " (новая)";
        StatusText.Text = "Картинка будет скопирована в папку данных после сохранения.";
    }

    private void ResetBackground_Click(object sender, RoutedEventArgs e)
    {
        _pickedBackgroundPath = null;
        _working.BackgroundImage = "";
        BackgroundBox.Text = "Встроенная";
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        if (_busy)
            return;

        _busy = true;
        SaveButton.IsEnabled = false;

        _working.BlurRadius = Math.Clamp(BlurSlider.Value, 0, 30);
        _working.WindowTitle = string.IsNullOrWhiteSpace(TitleBox.Text)
            ? "fsclient 1.3.0"
            : TitleBox.Text.Trim();

        if (_pickedBackgroundPath != null)
            _working.BackgroundImage = AppearanceService.InstallBackground(_pickedBackgroundPath);

        var progress = new Progress<string>(text => StatusText.Text = text);

        try
        {
            if (!string.Equals(_working.DataRoot, SettingsService.DataRoot,
                    StringComparison.OrdinalIgnoreCase))
            {
                await SettingsService.ChangeDataRootAsync(_working.DataRoot, progress);
            }

            SettingsService.Apply(_working);
            DialogResult = true;
            Close();
        }
        catch (System.Exception ex)
        {
            StatusText.Text = ex.Message;
            System.Windows.MessageBox.Show(this,
                "Не удалось применить настройки:\n\n" + ex.Message,
                "fsclient", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _busy = false;
            SaveButton.IsEnabled = true;
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
