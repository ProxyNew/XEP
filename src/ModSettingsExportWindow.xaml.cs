using System.ComponentModel;
using System.Windows;
using System.Windows.Data;

namespace XEnchantmentPatcher;

internal sealed class ModExportChoice(string plugin, int count) : INotifyPropertyChanged
{
    private bool selected;
    public string Plugin { get; } = plugin;
    public string ChoiceSummary => $"{count} configured choice{(count == 1 ? "" : "s")}";
    public bool IsSelected
    {
        get => selected;
        set { if (selected == value) return; selected = value; PropertyChanged?.Invoke(this, new(nameof(IsSelected))); }
    }
    public event PropertyChangedEventHandler? PropertyChanged;
}

public partial class ModSettingsExportWindow : Window
{
    private readonly ModExportChoice[] choices;
    public ICollectionView ModsView { get; }
    internal string[] SelectedPlugins => choices.Where(choice => choice.IsSelected).Select(choice => choice.Plugin).ToArray();

    internal ModSettingsExportWindow(IReadOnlyList<ExportedModSettings> mods)
    {
        choices = mods.Select(mod => new ModExportChoice(mod.Plugin, mod.ChoiceCount)).ToArray();
        ModsView = CollectionViewSource.GetDefaultView(choices);
        ModsView.Filter = row => ((ModExportChoice)row).Plugin.Contains(SearchInput?.Text.Trim() ?? "", StringComparison.OrdinalIgnoreCase);
        UiTheme.Attach(this);
        InitializeComponent();
        DataContext = this;
        foreach (var choice in choices) choice.PropertyChanged += (_, _) => UpdateSelection();
        UpdateSelection();
        Loaded += (_, _) => SearchInput.Focus();
    }

    private void UpdateSelection()
    {
        var count = choices.Count(choice => choice.IsSelected);
        SelectionCount.Text = $"{count} mod{(count == 1 ? "" : "s")} selected";
        ExportButton.IsEnabled = count > 0;
    }
    private void SearchChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        ModsView.Refresh();
        if (EmptyMessage is not null) EmptyMessage.Visibility = ModsView.IsEmpty ? Visibility.Visible : Visibility.Collapsed;
    }
    private void SelectAll(object sender, RoutedEventArgs e) { foreach (var choice in choices) choice.IsSelected = true; }
    private void Clear(object sender, RoutedEventArgs e) { foreach (var choice in choices) choice.IsSelected = false; }
    private void Accept(object sender, RoutedEventArgs e) { if (SelectedPlugins.Length > 0) DialogResult = true; }
}
