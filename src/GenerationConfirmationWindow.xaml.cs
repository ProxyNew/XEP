using System.Windows;

namespace XEnchantmentPatcher;

public partial class GenerationConfirmationWindow : Window
{
    // The output layout the user chose; valid when DialogResult is true.
    public bool FullOutputPlugin { get; private set; }

    public GenerationConfirmationWindow(GenerationPreview preview)
    {
        UiTheme.Attach(this);
        InitializeComponent();
        Summary.Text = $"{preview.Variants:N0} new enchanted variants; {preview.ReusedVariants:N0} existing variants reused.";
        if (preview.SkippedSelectedItems > 0)
            Summary.Text += $"\n{preview.SkippedSelectedItems:N0} selected items could not be patched. See the report for details.";
        if (preview.UndistributedVariants > 0)
            Summary.Text += $"\n{preview.UndistributedVariants:N0} combinations will not appear in loot. " +
                (preview.KeepUndistributedVariants
                    ? "New variants for these combinations will be kept in the patch."
                    : "New variants for these combinations will be left out of the patch.");
        var lightFiles = OutputLayout.Light.PatchCount(preview.NewRecords);
        GenerateLightButton.Content = $"Generate {lightFiles:N0} light plugin{(lightFiles == 1 ? "" : "s")}";
        GenerateFullButton.IsEnabled = preview.NewRecords <= OutputLayout.Full.IdCount;
        FullOutputNotice.Visibility = GenerateFullButton.IsEnabled ? Visibility.Collapsed : Visibility.Visible;
        // The last choice is the default action.
        (preview.FullOutputPlugin ? GenerateFullButton : GenerateLightButton).IsDefault = true;
        var lightIdentityChanged = preview.FullOutputPlugin ? preview.AlternateLayoutIdentityChanged : preview.IdentityChanged;
        var fullIdentityChanged = GenerateFullButton.IsEnabled &&
            (preview.FullOutputPlugin ? preview.IdentityChanged : preview.AlternateLayoutIdentityChanged);
        IdentityNotice.Visibility = lightIdentityChanged || fullIdentityChanged ? Visibility.Visible : Visibility.Collapsed;
        IdentityNotice.Text = $"Light ESP output {(lightIdentityChanged ? "changes" : "preserves")} record identities.";
        if (GenerateFullButton.IsEnabled)
            IdentityNotice.Text += $"\nNon-light output {(fullIdentityChanged ? "changes" : "preserves")} record identities.";
        IdentityNotice.Text += "\nExisting saves and patches referencing changed records may need attention.";
        // Detailed diagnostics stay in the report; keep the choices visible on small screens.
        MaxHeight = SystemParameters.WorkArea.Height;
        MaxWidth = SystemParameters.WorkArea.Width;
    }

    private void GenerateLight(object sender, RoutedEventArgs e) { FullOutputPlugin = false; DialogResult = true; }
    private void GenerateFull(object sender, RoutedEventArgs e) { FullOutputPlugin = true; DialogResult = true; }
    private void Cancel(object sender, RoutedEventArgs e) => DialogResult = false;
}
