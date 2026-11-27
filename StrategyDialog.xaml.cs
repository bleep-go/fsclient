using System.Windows;

namespace FsClient;

public partial class StrategyDialog : Window
{
    public string Strategy { get; private set; }
    public string Action { get; private set; }

    public StrategyDialog(Window owner, List<string> strategies, string currentService)
    {
        InitializeComponent();
        Owner = owner;
        Loaded += (_, _) =>
        {
            if (Owner == null) return;
            Left = Owner.Left;
            Top = Owner.Top;
            Width = Owner.ActualWidth;
            Height = Owner.ActualHeight;
        };

        StrategiesList.ItemsSource = strategies;
        if (strategies.Count > 0)
            StrategiesList.SelectedIndex = 0;

        if (!string.IsNullOrEmpty(currentService))
        {
            ServiceHint.Visibility = Visibility.Visible;
            ServiceHintText.Text = $"Активна служба: {currentService}";
        }
    }

    private void Emit(string action)
    {
        if (StrategiesList.SelectedItem is not string strategy)
            return;

        Strategy = strategy;
        Action = action;
        DialogResult = true;
        Close();
    }

    private void Run_Click(object sender, RoutedEventArgs e) => Emit("run");
    private void Install_Click(object sender, RoutedEventArgs e) => Emit("install");
    private void Close_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
