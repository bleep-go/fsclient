using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
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

    // Режим сна в трее: выгружаем тяжёлый WPF-слой, но оставляем живыми
    // бэкенд, таймер опроса состояния и закачки.
    private readonly List<(IBlurHost Host, UIElement Source)> _blurHosts = new();
    private ImageSource _blurImageSource;
    private ImageSource _backgroundImageSource;
    private DispatcherTimer _trimTimer;
    private bool _traySleep;

    private bool _downloading;
    private bool _exitRequested;
    private int _runtimeState = Backend.FsStateIdle;
    private string _runtimeDetail = "";

    // Временный диагностический флаг для проверки запуска из лаунчера.
    private string _autoRunTag;
    private string _autoRunBat;
    private readonly StringBuilder _diagLog = new();

    private sealed class ReleaseVm
    {
        public ReleaseInfo Source { get; init; }
        public string Tag => Source.Tag;
        public string Display { get; set; } = "";
    }

    private sealed class TgReleaseVm
    {
        public TgReleaseInfo Source { get; init; }
        public string Tag => Source.Tag;
        public string Display { get; set; } = "";
    }

    private bool _tgDownloading;
    private int _tgState = Backend.TgwsStateIdle;
    private string _tgDetail = "";

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    [DllImport("psapi.dll")]
    private static extern bool EmptyWorkingSet(IntPtr hProcess);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    private static string ResolveIconPath()
    {
        string local = Path.Combine(AppContext.BaseDirectory, "assets", "fsclient.ico");
        return File.Exists(local) ? local : BundledResources.Extract("FsClient.fsclient.ico", "fsclient.ico");
    }

    public MainWindow()
    {
        InitializeComponent();

        Icon = new BitmapImage(new Uri(ResolveIconPath()));
        ApplyAppearance();

        var backend = Backend.Instance;
        backend.Log += AppendLog;
        backend.ReleasesReady += OnReleasesReady;
        backend.DownloadProgress += OnDownloadProgress;
        backend.DownloadDone += OnDownloadDone;
        backend.DownloadFailed += OnDownloadFailed;
        backend.ServiceChanged += _ => UpdateRuntimeUi();
        backend.StrategyStarted += OnStrategyStarted;
        backend.StateChanged += OnNativeState;

        var tg = TgWsProxyService.Instance;
        tg.ReleasesReady += OnTgReleasesReady;
        tg.Log += AppendLog;
        tg.DownloadProgress += OnTgDownloadProgress;
        tg.DownloadDone += OnTgDownloadDone;
        tg.DownloadFailed += OnTgDownloadFailed;

        _runtimeState = backend.NativeState;
        _runtimeDetail = backend.NativeDetail;

        _stateTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.8) };
        _stateTimer.Tick += (_, _) =>
        {
            backend.PollState();
            RefreshTgState();
        };
        _stateTimer.Start();

        Loaded += MainWindow_Loaded;
        SetupTray();
        IsVisibleChanged += MainWindow_IsVisibleChanged;

        if (ZapretMenuButton.ContextMenu != null)
            ZapretMenuButton.ContextMenu.Opened += (_, _) =>
                AnimateContextMenu(ZapretMenuButton.ContextMenu);

        AppendLog("fsclient запущен. Загрузка релизов...");
        backend.FetchReleasesAsync();
        tg.FetchReleasesAsync();
        SetActiveProduct("zapret");
        UpdateRuntimeUi();
        RefreshTgDownloads();
        RefreshTgState();

        string flag = Path.Combine(Path.GetTempPath(), "fsclient_autorun.flag");
        if (File.Exists(flag))
        {
            var parts = File.ReadAllLines(flag);
            if (parts.Length >= 2)
            {
                _autoRunTag = parts[0].Trim();
                _autoRunBat = parts[1].Trim();
            }
        }
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

        if (!string.IsNullOrEmpty(_autoRunTag) && !string.IsNullOrEmpty(_autoRunBat))
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                AppendLog($"Автотест: запуск {_autoRunTag} / {_autoRunBat}");
                ApplyStrategy(_autoRunTag, _autoRunBat, "run");

                var dump = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
                dump.Tick += (_, _) =>
                {
                    File.WriteAllText(
                        Path.Combine(Path.GetTempPath(), "fsclient_autorun.log"),
                        _diagLog.ToString());
                    dump.Stop();
                };
                dump.Start();
            }), DispatcherPriority.Background);
        }
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
        _diagLog.AppendLine($"[{stamp}] {message}");
        ConsoleText.Text = $"[{stamp}] {message}";
        ConsoleText.ToolTip = ConsoleText.Text;
        LaunchLog.Write(message);
    }

    /// <summary>Применяет заголовок, размытие и фон из настроек.</summary>
    private void ApplyAppearance()
    {
        var settings = SettingsService.Current;
        string title = string.IsNullOrWhiteSpace(settings.WindowTitle)
            ? "fsclient 1.2.0"
            : settings.WindowTitle;

        Title = title;
        if (_tray != null)
            _tray.Text = title;

        BackgroundBlur.Radius = Math.Clamp(settings.BlurRadius, 0, 30);

        var image = AppearanceService.LoadBackground(settings.BackgroundImage);
        if (_traySleep)
        {
            _blurImageSource = image;
            _backgroundImageSource = image;
        }
        else
        {
            BlurSource.Source = image;
            BackgroundImage.Source = image;
        }
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
        TgWsPage.Visibility = zapret ? Visibility.Collapsed : Visibility.Visible;

        var page = zapret ? (UIElement)ZapretPage : TgWsPage;
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

    private void TgMenuButton_Click(object sender, RoutedEventArgs e)
    {
        var menu = TgMenuButton.ContextMenu;
        menu.PlacementTarget = TgMenuButton;
        menu.IsOpen = true;
        if (menu != null)
            AnimateContextMenu(menu);
    }

    private void TgMenuOpenFolder_Click(object sender, RoutedEventArgs e) =>
        OpenTgFolder();

    private void TgMenuCheckUpdates_Click(object sender, RoutedEventArgs e)
    {
        AppendLog("Проверка релизов TG WS Proxy...");
        TgWsProxyService.Instance.FetchReleasesAsync();
    }

    private void TgMenuCopyLink_Click(object sender, RoutedEventArgs e)
    {
        string tag = CurrentTgTag();
        if (string.IsNullOrEmpty(tag))
            return;
        TgWsProxyService.CopyToClipboard(TgWsProxyService.Instance.Link(tag));
        AppendLog("Ссылка TG WS Proxy скопирована");
    }

    private void MenuOpenFolder_Click(object sender, RoutedEventArgs e) =>
        Backend.Instance.OpenFolder(CurrentTag());

    private void MenuCheckUpdates_Click(object sender, RoutedEventArgs e)
    {
        AppendLog("Проверка обновлений...");
        Backend.Instance.FetchReleasesAsync();
    }

    private void SettingsButton_Click(object sender, RoutedEventArgs e) => OpenSettings();

    private void MenuSettings_Click(object sender, RoutedEventArgs e) => OpenSettings();

    private void OpenSettings()
    {
        if (_downloading || _runtimeState != Backend.FsStateIdle ||
            _tgDownloading || _tgState != Backend.TgwsStateIdle)
        {
            AppendLog("Настройки недоступны во время закачки или активного прокси");
            return;
        }

        var dialog = new SettingsWindow(this, SettingsService.Current.Clone());
        if (dialog.ShowDialog() != true)
            return;

        ApplyAppearance();
        RefreshDownloads();
        RefreshTgDownloads();
        LoadTgConfigIntoUi(CurrentTgTag());
        AppendLog($"Настройки сохранены. Папка данных: {SettingsService.DataRoot}");
    }

    private void GitHub_Click(object sender, RoutedEventArgs e)
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = "https://github.com/Flowseal/zapret-discord-youtube",
            UseShellExecute = true
        });
    }

    // ---------- TG WS Proxy ----------

    private static string MakeTgDisplay(TgReleaseInfo release)
    {
        string text = release.Tag + (release.IsLatest ? "  •  Latest" : "");
        if (TgWsProxyService.Instance.IsDownloaded(release.Tag))
            text += "   ✓";
        return text;
    }

    private void OnTgReleasesReady(List<TgReleaseInfo> releases)
    {
        var items = releases.Select(r => new TgReleaseVm
        {
            Source = r,
            Display = MakeTgDisplay(r)
        }).ToList();

        TgReleaseCombo.ItemsSource = items;
        if (items.Count > 0)
            TgReleaseCombo.SelectedIndex = 0;
        RefreshTgDownloads();
    }

    private void RefreshTgDownloads()
    {
        if (TgReleaseCombo.ItemsSource is IEnumerable<TgReleaseVm> items)
        {
            foreach (var item in items)
                item.Display = MakeTgDisplay(item.Source);
        }
        TgReleaseCombo.Items.Refresh();
        UpdateTgUi();
    }

    private string CurrentTgTag() => (TgReleaseCombo.SelectedItem as TgReleaseVm)?.Tag;

    private void TgReleaseCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        RefreshTgDownloads();
        LoadTgConfigIntoUi(CurrentTgTag());
    }

    private void OnTgDownloadProgress(int percent, string stage)
    {
        TgDownloadProgress.Value = percent;
        TgProgressLabel.Text = $"{stage}  {percent}%";
    }

    private void OnTgDownloadDone(string tag)
    {
        _tgDownloading = false;
        TgDownloadProgress.Value = 100;
        TgProgressLabel.Text = "Готово";
        AppendLog($"TG WS Proxy {tag} скачан");
        RefreshTgDownloads();
        LoadTgConfigIntoUi(tag);
        HideTgProgressAfter(TimeSpan.FromMilliseconds(600));
    }

    private void OnTgDownloadFailed(string error)
    {
        _tgDownloading = false;
        AppendLog($"TG WS Proxy: {error}");
        TgProgressLabel.Text = $"Ошибка: {error}";
        HideTgProgressAfter(TimeSpan.FromMilliseconds(1800));
    }

    private void HideTgProgressAfter(TimeSpan delay)
    {
        var timer = new DispatcherTimer { Interval = delay };
        timer.Tick += (sender, _) =>
        {
            TgProgressPanel.Visibility = Visibility.Collapsed;
            ((DispatcherTimer)sender).Stop();
        };
        timer.Start();
    }

    private void TgMainButton_Click(object sender, RoutedEventArgs e)
    {
        if (_tgDownloading)
            return;

        string tag = CurrentTgTag();
        if (string.IsNullOrEmpty(tag))
        {
            AppendLog("Выберите версию TG WS Proxy");
            return;
        }

        var service = TgWsProxyService.Instance;
        if (_tgState == Backend.TgwsStateRunning)
        {
            RestartTg(tag);
            return;
        }

        if (service.IsDownloaded(tag))
        {
            StartTg(tag);
            return;
        }

        var release = service.Releases.FirstOrDefault(r => r.Tag == tag);
        if (release == null || string.IsNullOrEmpty(release.Url))
        {
            AppendLog("Нет ссылки на сборку. Проверьте обновления.");
            return;
        }

        _tgDownloading = true;
        TgProgressPanel.Visibility = Visibility.Visible;
        TgDownloadProgress.Value = 0;
        TgProgressLabel.Text = "Подготовка...";
        AppendLog($"Скачивание TG WS Proxy {tag}...");
        service.DownloadRelease(release);
    }

    private void StartTg(string tag)
    {
        var service = TgWsProxyService.Instance;
        service.EnsureConfig(tag);
        var (ok, error) = service.Start(tag);
        if (!ok)
        {
            AppendLog("Не удалось запустить TG WS Proxy: " + error);
            return;
        }

        AppendLog($"TG WS Proxy {tag} запущен");
        LoadTgConfigIntoUi(tag);
        RefreshTgState();
    }

    private void RestartTg(string tag)
    {
        TgWsProxyService.Instance.Stop();
        AppendLog("TG WS Proxy перезапускается...");
        StartTg(tag);
    }

    private void TgStopButton_Click(object sender, RoutedEventArgs e)
    {
        TgWsProxyService.Instance.Stop();
        AppendLog("TG WS Proxy остановлен");
        RefreshTgState();
    }

    private void RefreshTgState()
    {
        var (state, detail) = TgWsProxyService.Instance.State();
        bool changed = state != _tgState || detail != _tgDetail;
        _tgState = state;
        _tgDetail = detail;

        UpdateTgUi();

        string tag = CurrentTgTag();
        if (!string.IsNullOrEmpty(tag))
        {
            string tail = TgWsProxyService.Instance.TailLog(tag);
            if (TgLogText.Text != tail)
                TgLogText.Text = tail;
        }

        if (changed && state == Backend.TgwsStateRunning)
            AppendLog($"TG WS Proxy активен ({detail})");
    }

    private void SetTgStatus(string text, Brush color)
    {
        TgStatusText.Text = text;
        TgStatusText.Foreground = color;
        TgStatusDot.Fill = color;
    }

    private void UpdateTgUi()
    {
        var muted = (Brush)FindResource("TextMuted");
        var success = (Brush)FindResource("SuccessBrush");
        var danger = (Brush)FindResource("DangerSolid");

        string tag = CurrentTgTag();
        bool downloaded = !string.IsNullOrEmpty(tag) &&
                          TgWsProxyService.Instance.IsDownloaded(tag);
        bool running = _tgState == Backend.TgwsStateRunning;

        TgMainButtonText.Text = running ? "Перезапустить"
            : downloaded ? "Запустить" : "Скачать";
        TgStopButton.Visibility = running ? Visibility.Visible : Visibility.Collapsed;

        if (running)
        {
            SetTgStatus($"Работает · {_tgDetail}", success);
            TgCardStatus.Text = "Процесс активен";
            TgCardStatus.Foreground = success;
        }
        else
        {
            SetTgStatus(downloaded ? "Готов к запуску" : "Сборка не скачана",
                downloaded ? muted : danger);
            TgCardStatus.Text = downloaded ? "MTProto-прокси" : "Не скачано";
            TgCardStatus.Foreground = (Brush)FindResource("TextMuted");
        }
    }

    private void LoadTgConfigIntoUi(string tag)
    {
        if (string.IsNullOrEmpty(tag))
            return;

        var service = TgWsProxyService.Instance;
        if (!service.IsDownloaded(tag))
        {
            TgHostBox.Text = "127.0.0.1";
            TgPortBox.Text = "1443";
            TgSecretBox.Text = "";
            TgLinkBox.Text = "";
            TgLogText.Text = "";
            return;
        }

        service.EnsureConfig(tag);
        var cfg = service.LoadConfig(tag);
        TgHostBox.Text = cfg.Host;
        TgPortBox.Text = cfg.Port.ToString();
        TgCfProxyCheck.IsChecked = cfg.CfProxy;
        TgH2Check.IsChecked = cfg.H2;
        TgNoSecureCheck.IsChecked = cfg.NoSecure;
        TgForceTestCheck.IsChecked = cfg.ForceTestDc;
        TgSecretBox.Text = cfg.Secret;
        TgLinkBox.Text = service.Link(tag);
    }

    private void TgSaveConfig_Click(object sender, RoutedEventArgs e)
    {
        string tag = CurrentTgTag();
        if (string.IsNullOrEmpty(tag))
            return;
        if (!int.TryParse(TgPortBox.Text.Trim(), out int port) || port < 1 || port > 65535)
        {
            AppendLog("Порт должен быть числом от 1 до 65535");
            return;
        }

        var service = TgWsProxyService.Instance;
        var cfg = service.LoadConfig(tag);
        cfg.Host = string.IsNullOrWhiteSpace(TgHostBox.Text) ? "127.0.0.1" : TgHostBox.Text.Trim();
        cfg.Port = port;
        cfg.CfProxy = TgCfProxyCheck.IsChecked == true;
        cfg.H2 = TgH2Check.IsChecked == true;
        cfg.NoSecure = TgNoSecureCheck.IsChecked == true;
        cfg.ForceTestDc = TgForceTestCheck.IsChecked == true;
        service.SaveConfig(tag, cfg);

        TgSecretBox.Text = cfg.Secret;
        TgLinkBox.Text = service.Link(tag);
        AppendLog(_tgState == Backend.TgwsStateRunning
            ? "Конфиг сохранён. Перезапустите прокси, чтобы применить."
            : "Конфиг TG WS Proxy сохранён");
    }

    private void TgRegenSecret_Click(object sender, RoutedEventArgs e)
    {
        string tag = CurrentTgTag();
        if (string.IsNullOrEmpty(tag))
            return;
        var service = TgWsProxyService.Instance;
        service.EnsureConfig(tag);
        TgSecretBox.Text = service.RegenerateSecret(tag);
        TgLinkBox.Text = service.Link(tag);
        AppendLog("Сгенерирован новый secret. Перезапустите прокси и подключите Telegram заново.");
    }

    private void TgCopyLink_Click(object sender, RoutedEventArgs e)
    {
        string tag = CurrentTgTag();
        if (string.IsNullOrEmpty(tag))
            return;
        TgWsProxyService.CopyToClipboard(TgWsProxyService.Instance.Link(tag));
        AppendLog("Ссылка TG WS Proxy скопирована");
    }

    private void TgOpenTelegram_Click(object sender, RoutedEventArgs e)
    {
        string tag = CurrentTgTag();
        if (string.IsNullOrEmpty(tag))
            return;
        string link = TgWsProxyService.Instance.Link(tag);
        TgWsProxyService.OpenUrl(link);
        AppendLog("Открыта ссылка подключения Telegram");
    }

    private void TgRestart_Click(object sender, RoutedEventArgs e)
    {
        string tag = CurrentTgTag();
        if (!string.IsNullOrEmpty(tag))
            RestartTg(tag);
    }

    private void OpenTgFolder()
    {
        string tag = CurrentTgTag();
        TgWsProxyService.OpenFolder(string.IsNullOrEmpty(tag)
            ? TgWsProxyService.Root
            : TgWsProxyService.ReleaseDir(tag));
    }

    private void TgOpenFolder_Click(object sender, RoutedEventArgs e) => OpenTgFolder();

    private void TgOpenLog_Click(object sender, RoutedEventArgs e)
    {
        string tag = CurrentTgTag();
        if (string.IsNullOrEmpty(tag))
            return;
        TgWsProxyService.OpenLog(tag);
    }

    private void TgReleasesPage_Click(object sender, RoutedEventArgs e) =>
        TgWsProxyService.OpenUrl("https://github.com/Flowseal/tg-ws-proxy/releases");

    // ---------- Экономия памяти в трее ----------

    private static void WalkVisuals(DependencyObject root, Action<DependencyObject> visit)
    {
        if (root == null)
            return;

        visit(root);
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
            WalkVisuals(VisualTreeHelper.GetChild(root, i), visit);
    }

    private void MainWindow_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_exitRequested)
            return;

        if (e.NewValue is bool visible)
        {
            if (visible)
                LeaveTrayMode();
            else
                EnterTrayMode();
        }
    }

    private void EnterTrayMode()
    {
        if (_traySleep || _exitRequested)
            return;

        _traySleep = true;
        _stateTimer.Interval = TimeSpan.FromSeconds(10);

        _blurHosts.Clear();
        WalkVisuals(this, node =>
        {
            if (node is IBlurHost host && host.BlurSource != null)
                _blurHosts.Add((host, host.BlurSource));
        });

        foreach (var (host, _) in _blurHosts)
            host.ReleaseBlur();

        _blurImageSource = BlurSource.Source;
        _backgroundImageSource = BackgroundImage.Source;
        BlurSource.Source = null;
        BackgroundImage.Source = null;
        BlurHost.Visibility = Visibility.Collapsed;

        ScheduleMemoryTrim();
    }

    private void LeaveTrayMode()
    {
        if (!_traySleep)
            return;

        _traySleep = false;
        _trimTimer?.Stop();
        _trimTimer = null;

        BlurSource.Source = _blurImageSource;
        BackgroundImage.Source = _backgroundImageSource;
        BlurHost.Visibility = Visibility.Visible;

        foreach (var (host, source) in _blurHosts)
            host.RestoreBlur(source);
        _blurHosts.Clear();

        _stateTimer.Interval = TimeSpan.FromSeconds(1.8);
        Backend.Instance.PollState();
    }

    private void ScheduleMemoryTrim()
    {
        _trimTimer?.Stop();
        _trimTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
        _trimTimer.Tick += (sender, _) =>
        {
            ((DispatcherTimer)sender).Stop();
            _trimTimer = null;
            TrimMemory();
        };
        _trimTimer.Start();
    }

    private static void TrimMemory()
    {
        try
        {
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
            EmptyWorkingSet(GetCurrentProcess());
        }
        catch
        {
            // Экономия памяти — best effort, не валим приложение из-за ошибки trim.
        }
    }

    // ---------- Трей ----------

    private void SetupTray()
    {
        _tray = new WinForms.NotifyIcon
        {
            Icon = new Icon(ResolveIconPath()),
            Text = Title,
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
        Backend.Instance.PollState();
        string zapret = !string.IsNullOrEmpty(Backend.Instance.InstalledService)
            ? $"Служба активна: {Backend.Instance.InstalledService}"
            : "zapret: служба не установлена";
        var (tgState, tgDetail) = TgWsProxyService.Instance.State();
        string tg = tgState == Backend.TgwsStateRunning
            ? $"TG WS Proxy: работает ({tgDetail})"
            : "TG WS Proxy: остановлен";
        string text = zapret + "\n" + tg;
        _tray.ShowBalloonTip(3000, "fsclient", text, WinForms.ToolTipIcon.Info);
    }

    private void ExitApp()
    {
        _exitRequested = true;
        _stateTimer.Stop();
        _trimTimer?.Stop();
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
