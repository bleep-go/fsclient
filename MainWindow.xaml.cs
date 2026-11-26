using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Brush = System.Windows.Media.Brush;
using WinForms = System.Windows.Forms;

namespace FsClient;

public partial class MainWindow : Window
{
    private static readonly List<string> DefaultStrategies = new()
    {
        "general.bat",
        "general (ALT).bat",
        "general (ALT2).bat",
        "general (ALT3).bat",
        "general (ALT4).bat",
    };

    private readonly DispatcherTimer _stateTimer;
    private WinForms.NotifyIcon _tray;

    private bool _downloading;
    private bool _exitRequested;
    private int _runtimeState = Backend.FsStateIdle;
    private string _runtimeDetail = "";

    private sealed class ReleaseVm
    {
        public ReleaseInfo Source { get; init; }
        public string Tag => Source.Tag;
        public string Display { get; set; } = "";
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    private static string ResolveIconPath()
    {
        string local = Path.Combine(AppContext.BaseDirectory, "assets", "fsclient.ico");
        return File.Exists(local) ? local : BundledResources.Extract("FsClient.fsclient.ico", "fsclient.ico");
    }

    public MainWindow()
    {
        InitializeComponent();

        Icon = new BitmapImage(new Uri(ResolveIconPath()));

        var backend = Backend.Instance;
        backend.Log += AppendLog;
        backend.ReleasesReady += OnReleasesReady;
        backend.DownloadProgress += OnDownloadProgress;
        backend.DownloadDone += OnDownloadDone;
        backend.DownloadFailed += OnDownloadFailed;
        backend.ServiceChanged += _ => UpdateRuntimeUi();
        backend.StrategyStarted += OnStrategyStarted;
        backend.StateChanged += OnNativeState;

        _runtimeState = backend.NativeState;
        _runtimeDetail = backend.NativeDetail;

        _stateTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.8) };
        _stateTimer.Tick += (_, _) => backend.PollState();
        _stateTimer.Start();

        Loaded += MainWindow_Loaded;
        SetupTray();

        if (ZapretMenuButton.ContextMenu != null)
            ZapretMenuButton.ContextMenu.Opened += (_, _) =>
                AnimateContextMenu(ZapretMenuButton.ContextMenu);

        AppendLog("fsclient запущен. Загрузка релизов...");
        backend.FetchReleasesAsync();
        SetActiveProduct("zapret");
        UpdateRuntimeUi();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        IntPtr hwnd = new WindowInteropHelper(this).Handle;
        int dark = 1;
        int caption = 0x001A1112; // #12111A в COLORREF
        int text = 0x00FAF2F4;    // #F4F2FA
        int border = 0x001A1112;
        DwmSetWindowAttribute(hwnd, 20, ref dark, sizeof(int));
        DwmSetWindowAttribute(hwnd, 35, ref caption, sizeof(int));
        DwmSetWindowAttribute(hwnd, 36, ref text, sizeof(int));
        DwmSetWindowAttribute(hwnd, 34, ref border, sizeof(int));
    }

    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        // Матовое стекло внутри шаблона ComboBox не попадает в namescope окна.
        ReleaseCombo.ApplyTemplate();
        if (ReleaseCombo.Template?.FindName("Bd", ReleaseCombo) is FrostedBorder comboGlass)
            comboGlass.BlurSource = BlurSource;

        ShowSplash();
    }

    private void ShowSplash()
    {
        const string text = "FSCLIENT";
        SplashLetters.Children.Clear();

        for (int i = 0; i < text.Length; i++)
        {
            var letter = new TextBlock
            {
                Text = text[i].ToString(),
                FontFamily = new System.Windows.Media.FontFamily("Segoe UI"),
                FontSize = 64,
                FontWeight = FontWeights.Black,
                Foreground = System.Windows.Media.Brushes.White,
                Margin = new Thickness(0, 0, 8, 0)
            };
            var transform = new TranslateTransform(0, 80);
            letter.RenderTransform = transform;
            SplashLetters.Children.Add(letter);

            transform.BeginAnimation(TranslateTransform.YProperty,
                new DoubleAnimation(80, 0, TimeSpan.FromMilliseconds(520))
                {
                    BeginTime = TimeSpan.FromMilliseconds(i * 60),
                    EasingFunction = new BackEase
                    {
                        EasingMode = EasingMode.EaseOut,
                        Amplitude = 1
                    }
                });
        }

        int total = 80 + (text.Length - 1) * 60 + 520 + 200;
        var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(400))
        {
            BeginTime = TimeSpan.FromMilliseconds(total)
        };
        fade.Completed += (_, _) => SplashOverlay.Visibility = Visibility.Collapsed;
        SplashOverlay.BeginAnimation(UIElement.OpacityProperty, fade);
    }

    // ---------- Лог ----------

    private void AppendLog(string message)
    {
        string stamp = DateTime.Now.ToString("HH:mm:ss");
        ConsoleText.Text = $"[{stamp}] {message}";
        ConsoleText.ToolTip = ConsoleText.Text;
    }

    private void SetConsoleDot(Brush brush) => ConsoleDot.Fill = brush;

    // ---------- Релизы ----------

    private static string MakeDisplay(ReleaseInfo release)
    {
        string text = release.Tag + (release.IsLatest ? "  •  Latest" : "");
        if (Backend.Instance.IsDownloaded(release.Tag))
            text += "   ✓";
        return text;
    }

    private void OnReleasesReady(List<ReleaseInfo> releases)
    {
        var items = releases.Select(r => new ReleaseVm
        {
            Source = r,
            Display = MakeDisplay(r)
        }).ToList();

        ReleaseCombo.ItemsSource = items;
        if (items.Count > 0)
            ReleaseCombo.SelectedIndex = 0;
    }

    private void RefreshDownloads()
    {
        if (ReleaseCombo.ItemsSource is IEnumerable<ReleaseVm> items)
        {
            foreach (var item in items)
                item.Display = MakeDisplay(item.Source);
        }
        ReleaseCombo.Items.Refresh();
    }

    private string CurrentTag() => (ReleaseCombo.SelectedItem as ReleaseVm)?.Tag;

    private void ReleaseCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        RefreshDownloads();
        UpdateRuntimeUi();
    }

    // ---------- Состояние ----------

    private string ActiveLabel(int state, string detail)
    {
        var backend = Backend.Instance;

        if (state == Backend.FsStateRunningProcess)
        {
            string text = detail ?? "";
            if (text.Contains(" — ", StringComparison.Ordinal))
                return text.Split(new[] { " — " }, 2, StringSplitOptions.None)[1];
            if (text.StartsWith("winws.exe", StringComparison.OrdinalIgnoreCase))
                return !string.IsNullOrEmpty(backend.RunningStrategy) ? backend.RunningStrategy : "winws.exe";
            if (!string.IsNullOrEmpty(text))
                return text;
            return !string.IsNullOrEmpty(backend.RunningStrategy) ? backend.RunningStrategy : "winws.exe";
        }

        if (state == Backend.FsStateRunningService)
        {
            if (!string.IsNullOrEmpty(backend.InstalledService))
                return backend.InstalledService;
            if (!string.IsNullOrEmpty(detail))
                return detail;
            return "zapret";
        }

        return "";
    }

    private static string ShortLabel(string text, int limit = 22)
    {
        text ??= "";
        return text.Length <= limit ? text : text[..(limit - 1)] + "…";
    }

    private void OnNativeState(int state, string detail)
    {
        _runtimeState = state;
        _runtimeDetail = detail ?? "";
        UpdateRuntimeUi();
    }

    private void OnStrategyStarted(string bat)
    {
        _runtimeState = Backend.FsStateRunningProcess;
        _runtimeDetail = bat;
        Backend.Instance.RunningStrategy = bat;
        UpdateRuntimeUi();
    }

    private void SetMainIcon(string kind)
    {
        MainIcon.Data = kind switch
        {
            "play" => Geometry.Parse("M8 4 L20 12 L8 20 Z"),
            "stop" => Geometry.Parse("M7 7 H21 V21 H7 Z"),
            _ => Geometry.Parse("M12 4 V16 M7 12 L12 17 L17 12 M5 20 H19"),
        };
    }

    private void SetZapretStatus(string text, Brush color)
    {
        ZapretStatus.Text = text;
        ZapretStatus.Foreground = color;
        ZapretDot.Fill = color;
    }

    private void UpdateRuntimeUi()
    {
        if (_downloading)
            return;

        var muted = (Brush)FindResource("TextMuted");
        var success = (Brush)FindResource("SuccessBrush");
        var danger = (Brush)FindResource("DangerSolid");

        if (_runtimeState == Backend.FsStateIdle)
        {
            string tag = CurrentTag();
            bool downloaded = !string.IsNullOrEmpty(tag) && Backend.Instance.IsDownloaded(tag);

            MainButtonText.Text = downloaded ? "Запустить" : "Скачать";
            SetMainIcon(downloaded ? "play" : "download");
            MainButton.Background = (Brush)FindResource("AccentBrush");
            MainButton.ToolTip = null;
            StopButton.Visibility = Visibility.Collapsed;

            SetZapretStatus(downloaded ? "Готово" : "Не скачано", downloaded ? success : muted);
            SetConsoleDot(muted);
            return;
        }

        string label = ActiveLabel(_runtimeState, _runtimeDetail);
        string shortLabel = ShortLabel(label);

        MainButton.Background = (Brush)FindResource("DangerBrush");
        SetMainIcon("stop");
        StopButton.Visibility = Visibility.Visible;

        if (_runtimeState == Backend.FsStateRunningService)
        {
            MainButtonText.Text = $"Стоп службы · {shortLabel}";
            MainButton.ToolTip = $"Активна служба zapret: {label}";
            SetZapretStatus("Служба активна", success);
            SetConsoleDot(success);
        }
        else
        {
            MainButtonText.Text = $"Остановить · {shortLabel}";
            MainButton.ToolTip = $"Активный процесс winws.exe: {label}";
            SetZapretStatus("Обход активен", danger);
            SetConsoleDot(danger);
        }
    }

    // ---------- Действия ----------

    private void MainButton_Click(object sender, RoutedEventArgs e)
    {
        if (_downloading)
            return;

        if (_runtimeState != Backend.FsStateIdle)
        {
            StopAll();
            return;
        }

        string tag = CurrentTag();
        if (string.IsNullOrEmpty(tag))
        {
            AppendLog("Релиз не выбран");
            return;
        }

        if (Backend.Instance.IsDownloaded(tag))
        {
            OpenStrategyModal(tag);
            return;
        }

        var release = Backend.Instance.Releases.FirstOrDefault(r => r.Tag == tag);
        if (release == null || string.IsNullOrEmpty(release.Url))
        {
            AppendLog("Ссылка для скачивания недоступна (демо-режим). Попробуйте другой релиз или проверьте соединение.");
            return;
        }

        StartDownload(tag, release.Url);
    }

    private void StartDownload(string tag, string url)
    {
        _downloading = true;
        MainButton.IsEnabled = false;
        MainButtonText.Text = "Скачивание...";
        SetMainIcon("download");
        ProgressPanel.Visibility = Visibility.Visible;
        DownloadProgress.Value = 0;
        ProgressLabel.Text = "Подготовка...";
        AppendLog($"Начинаю скачивание {tag}...");
        Backend.Instance.DownloadRelease(tag, url);
    }

    private void OnDownloadProgress(int percent, string stage)
    {
        DownloadProgress.Value = percent;
        ProgressLabel.Text = $"{stage}  {percent}%";
    }

    private void OnDownloadDone(string tag)
    {
        _downloading = false;
        MainButton.IsEnabled = true;
        DownloadProgress.Value = 100;
        ProgressLabel.Text = "Готово";
        AppendLog($"Релиз {tag} успешно скачан");
        RefreshDownloads();
        UpdateRuntimeUi();
        HideProgressAfter(TimeSpan.FromMilliseconds(600));
    }

    private void OnDownloadFailed(string error)
    {
        _downloading = false;
        MainButton.IsEnabled = true;
        AppendLog($"Ошибка скачивания: {error}");
        ProgressLabel.Text = $"Ошибка: {error}";
        HideProgressAfter(TimeSpan.FromMilliseconds(1500));
    }

    private void HideProgressAfter(TimeSpan delay)
    {
        var timer = new DispatcherTimer { Interval = delay };
        timer.Tick += (sender, _) =>
        {
            ProgressPanel.Visibility = Visibility.Collapsed;
            ((DispatcherTimer)sender).Stop();
        };
        timer.Start();
    }

    private void StopButton_Click(object sender, RoutedEventArgs e) => StopAll();

    private void StopAll()
    {
        Backend.Instance.StopStrategy();
        Backend.Instance.RemoveService();
        _runtimeState = Backend.FsStateIdle;
        _runtimeDetail = "";
        UpdateRuntimeUi();
    }

    private void FolderButton_Click(object sender, RoutedEventArgs e) =>
        Backend.Instance.OpenFolder(CurrentTag());

    private void OpenStrategyModal(string tag)
    {
        var strategies = Backend.Instance.ListStrategies(tag);
        if (strategies.Count == 0)
            strategies = DefaultStrategies.ToList();

        var dialog = new StrategyDialog(this, strategies, Backend.Instance.InstalledService);
        if (dialog.ShowDialog() == true)
            OnStrategyChosen(tag, dialog.Strategy, dialog.Action);
    }

    private void OnStrategyChosen(string tag, string bat, string action)
    {
        string installed = Backend.Instance.InstalledService;
        if (!string.IsNullOrEmpty(installed) && installed != bat)
        {
            var confirm = new ConfirmDialog(this, installed, bat);
            if (confirm.ShowDialog() == true)
                ApplyStrategy(tag, bat, action);
            return;
        }

        ApplyStrategy(tag, bat, action);
    }

    private void ApplyStrategy(string tag, string bat, string action)
    {
        if (!string.IsNullOrEmpty(Backend.Instance.InstalledService))
            Backend.Instance.RemoveService();

        if (action == "run")
            Backend.Instance.RunStrategy(tag, bat);
        else
            Backend.Instance.InstallService(tag, bat);
    }

    // ---------- Боковая панель ----------

    private static void AnimateContextMenu(System.Windows.Controls.ContextMenu menu)
    {
        menu.ApplyTemplate();
        if (VisualTreeHelper.GetChild(menu, 0) is not System.Windows.Controls.Border border)
            return;

        var scale = new ScaleTransform(0.94, 0.94);
        border.RenderTransformOrigin = new System.Windows.Point(0, 0);
        border.RenderTransform = scale;

        var easing = new CubicEase { EasingMode = EasingMode.EaseOut };
        var duration = TimeSpan.FromMilliseconds(180);

        border.BeginAnimation(UIElement.OpacityProperty,
            new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(140)));
        scale.BeginAnimation(ScaleTransform.ScaleXProperty,
            new DoubleAnimation(0.94, 1, duration) { EasingFunction = easing });
        scale.BeginAnimation(ScaleTransform.ScaleYProperty,
            new DoubleAnimation(0.94, 1, duration) { EasingFunction = easing });
    }

    private void SetActiveProduct(string product)
    {
        bool zapret = product == "zapret";
        var activeTint = new SolidColorBrush(System.Windows.Media.Color.FromArgb(145, 20, 19, 32));
        var idleTint = new SolidColorBrush(System.Windows.Media.Color.FromArgb(115, 20, 19, 32));
        var activeBorder = new SolidColorBrush(System.Windows.Media.Color.FromArgb(60, 255, 255, 255));
        var idleBorder = new SolidColorBrush(System.Windows.Media.Color.FromArgb(36, 255, 255, 255));

        ZapretCard.Tint = zapret ? activeTint : idleTint;
        TgCard.Tint = zapret ? idleTint : activeTint;
        ZapretCard.FrostBorderBrush = zapret ? activeBorder : idleBorder;
        TgCard.FrostBorderBrush = zapret ? idleBorder : activeBorder;

        ZapretPage.Visibility = zapret ? Visibility.Visible : Visibility.Collapsed;
        PlaceholderPage.Visibility = zapret ? Visibility.Collapsed : Visibility.Visible;

        var page = zapret ? (UIElement)ZapretPage : PlaceholderPage;
        var slide = new TranslateTransform(0, 10);
        page.RenderTransform = slide;
        page.BeginAnimation(UIElement.OpacityProperty,
            new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220)));
        slide.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(10, 0, TimeSpan.FromMilliseconds(260))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            });
    }

    private void ZapretCard_Click(object sender, System.Windows.Input.MouseButtonEventArgs e) =>
        SetActiveProduct("zapret");

    private void TgCard_Click(object sender, System.Windows.Input.MouseButtonEventArgs e) =>
        SetActiveProduct("tgws");

    private void ZapretMenuButton_Click(object sender, RoutedEventArgs e)
    {
        var menu = ZapretMenuButton.ContextMenu;
        menu.PlacementTarget = ZapretMenuButton;
        menu.IsOpen = true;
    }

    private void MenuOpenFolder_Click(object sender, RoutedEventArgs e) =>
        Backend.Instance.OpenFolder(CurrentTag());

    private void MenuCheckUpdates_Click(object sender, RoutedEventArgs e)
    {
        AppendLog("Проверка обновлений...");
        Backend.Instance.FetchReleasesAsync();
    }

    private void MenuSettings_Click(object sender, RoutedEventArgs e) =>
        AppendLog("Настройки продукта (в разработке)");

    private void GitHub_Click(object sender, RoutedEventArgs e)
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = "https://github.com/Flowseal/zapret-discord-youtube",
            UseShellExecute = true
        });
    }

    // ---------- Трей ----------

    private void SetupTray()
    {
        _tray = new WinForms.NotifyIcon
        {
            Icon = new Icon(ResolveIconPath()),
            Text = "fsclient 1.1.0",
            Visible = true
        };

        var menu = new WinForms.ContextMenuStrip
        {
            BackColor = System.Drawing.Color.FromArgb(28, 25, 43),
            ForeColor = System.Drawing.Color.FromArgb(244, 242, 250),
            Font = new Font("Segoe UI", 9F),
            Padding = new Padding(6, 8, 6, 8),
            ShowImageMargin = false,
            ShowCheckMargin = false,
            Renderer = new DarkTrayMenuRenderer()
        };
        menu.VisibleChanged += (_, _) =>
        {
            if (!menu.Visible || menu.Width <= 0 || menu.Height <= 0)
                return;

            menu.Region?.Dispose();
            using var path = new GraphicsPath();
            path.AddRoundedRectangle(new Rectangle(0, 0, menu.Width, menu.Height), new System.Drawing.Size(12, 12));
            menu.Region = new Region(path);
        };
        menu.Items.Add("Открыть", null, (_, _) => ShowFromTray());
        menu.Items.Add("Статус", null, (_, _) => TrayStatus());
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add("Выход", null, (_, _) => ExitApp());

        _tray.ContextMenuStrip = menu;
        _tray.MouseDoubleClick += (_, _) => ShowFromTray();
    }

    private void ShowFromTray()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    private void TrayStatus()
    {
        string text = !string.IsNullOrEmpty(Backend.Instance.InstalledService)
            ? $"Служба активна: {Backend.Instance.InstalledService}"
            : "Лаунчер работает, служба не установлена";
        _tray.ShowBalloonTip(3000, "fsclient", text, WinForms.ToolTipIcon.Info);
    }

    private void ExitApp()
    {
        _exitRequested = true;
        _stateTimer.Stop();
        if (_tray != null)
        {
            _tray.Visible = false;
            _tray.Dispose();
            _tray = null;
        }
        Close();
        System.Windows.Application.Current.Shutdown();
    }

    private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_exitRequested)
            return;

        e.Cancel = true;
        Hide();
    }
}
