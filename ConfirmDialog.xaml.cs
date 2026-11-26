using System.Windows;

namespace FsClient;

public partial class ConfirmDialog : Window
{
    public ConfirmDialog(Window owner, string currentService, string newStrategy)
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

        CurrentServiceRun.Text = currentService;
        NewStrategyRun.Text = newStrategy;
    }

    private void Confirm_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
