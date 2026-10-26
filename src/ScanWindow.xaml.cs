using System.Windows;
namespace XEnchantmentPatcher;

public partial class ScanWindow : Window
{
    private bool _finished;
    public ScanWindow(MainWindow host)
    {
        UiTheme.Attach(this); InitializeComponent(); DataContext = host;
        Closing += (_, e) => e.Cancel = !_finished;
    }
    internal void Finish() { _finished = true; Close(); }
}
