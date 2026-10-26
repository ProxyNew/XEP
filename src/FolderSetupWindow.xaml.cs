using System.IO;
using System.Windows;
using Forms = System.Windows.Forms;

namespace XEnchantmentPatcher;

public partial class FolderSetupWindow : Window
{
    private readonly AppSettings _settings;
    public FolderSetupWindow(AppSettings settings)
    {
        _settings = settings;
        UiTheme.Attach(this);
        InitializeComponent();
        DataInput.Text = settings.DataFolder ?? "";
        OutputInput.Text = settings.OutputFolder ?? "";
    }

    internal static string? ValidateFolders(string data, string output)
    {
        if (!Directory.Exists(data)) return "Choose an existing Skyrim Data folder.";
        if (string.IsNullOrWhiteSpace(output) || !Path.IsPathFullyQualified(output)) return "Choose an absolute path for the patch output folder.";
        try
        {
            var path = Path.GetFullPath(output);
            if (File.Exists(path)) return "The output path is a file. Choose a folder.";
            if (Path.TrimEndingDirectorySeparator(path).Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(data)), StringComparison.OrdinalIgnoreCase))
                return "Choose a separate output folder so the generated patch can be installed as its own mod.";
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return "Choose a valid output folder path."; }
        return null;
    }

    private void Continue(object sender, RoutedEventArgs e)
    {
        var data = DataInput.Text.Trim(); var output = OutputInput.Text.Trim();
        if (ValidateFolders(data, output) is { } error) { ValidationMessage.Text = error; return; }
        _settings.DataFolder = Path.GetFullPath(data); _settings.OutputFolder = Path.GetFullPath(output);
        try { SettingsStore.Save(_settings); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { ValidationMessage.Text = ex.Message; return; }
        DialogResult = true;
    }
    private void BrowseData(object sender, RoutedEventArgs e) => Browse(DataInput, "Select the Skyrim Special Edition Data folder");
    private void BrowseOutput(object sender, RoutedEventArgs e) => Browse(OutputInput, "Select the patch output folder");
    private static void Browse(System.Windows.Controls.TextBox field, string description)
    {
        using var dialog = new Forms.FolderBrowserDialog { Description = description, SelectedPath = Directory.Exists(field.Text) ? field.Text : "", ShowNewFolderButton = true };
        if (dialog.ShowDialog() == Forms.DialogResult.OK) field.Text = dialog.SelectedPath;
    }
}
