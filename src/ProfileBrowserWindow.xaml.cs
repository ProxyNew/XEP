using System.Windows;
namespace XEnchantmentPatcher;

public partial class ProfileBrowserWindow : Window
{
    public string? SelectedProfile => Profiles.SelectedItem as string;
    public ProfileBrowserWindow(IEnumerable<string> profiles, string current)
    {
        UiTheme.Attach(this); InitializeComponent(); Profiles.ItemsSource = profiles; Profiles.SelectedItem = current;
    }
    private void Select(object sender, RoutedEventArgs e) { if (SelectedProfile is not null) DialogResult = true; }
}
