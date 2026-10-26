using System.Collections;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using DataGrid = System.Windows.Controls.DataGrid;
using System.Windows.Threading;

namespace XEnchantmentPatcher;

public partial class ReviewWindow : Window
{
    private readonly MainWindow _host;
    internal BusyIcon ReviewBusyIcon = null!;

    public ReviewWindow(MainWindow host)
    {
        _host = host;
        UiTheme.Attach(this);
        InitializeComponent();
        Resources["PolicyValues"] = PolicyValues.All;
        PluginBatchChoice.ItemsSource = PolicyValues.All;
        SpecializedBatchChoice.ItemsSource = SpecializedDistributionChoices.Batch;
        ReviewBusyIcon = new BusyIcon { Width = 112, Height = 112, HorizontalAlignment = System.Windows.HorizontalAlignment.Center };
        ReviewBusyIconHost.Content = ReviewBusyIcon;
        DataContext = host;
        Group(host.ItemsView, nameof(CandidateItem.Plugin));
        Group(host.SpecializedItemsView, nameof(CandidateItem.Plugin));
        Group(host.EnchantmentsView, nameof(EnchantmentSelectionOption.Provider));
        foreach (var grid in new[] { ItemGrid, SpecializedGrid, EnchantmentGrid })
            grid.AddHandler(Expander.ExpandedEvent, new RoutedEventHandler(PrepareGroupedColumns));
        ItemBatchChoice.SelectedIndex = 0;
        EnchantmentBatchChoice.SelectedIndex = 0;
        host.PropertyChanged += SearchChanged;
        // A search kept from an earlier review shows its matches expanded on reopening.
        Loaded += (_, _) =>
        {
            foreach (var (grid, text) in new[] { (ItemGrid, host.SearchText), (SpecializedGrid, host.SpecializedSearchText), (EnchantmentGrid, host.EnchantmentSearchText) })
                if (!string.IsNullOrWhiteSpace(text)) ReviewGroupExpander.RevealMatches(grid);
        };
        host.RowsReplacing += SavePlace;
        host.RowsReplaced += RestorePlace;
        Closed += (_, _) => { host.PropertyChanged -= SearchChanged; host.RowsReplacing -= SavePlace; host.RowsReplaced -= RestorePlace; };
        UpdatePluginBatchState();
        UpdateEnchantmentBatchState();
    }

    private void PrepareGroupedColumns(object sender, RoutedEventArgs e)
    {
        var grid = (System.Windows.Controls.DataGrid)sender;
        // WPF can cache minimum column widths when a grouped tab is measured while
        // hidden or its groups are collapsed. Recompute from the requested widths
        // once its viewport is loaded and whenever a group reveals its rows.
        // Pixel widths chosen by the user remain pixel widths.
        Dispatcher.BeginInvoke(() =>
        {
            if (!grid.IsVisible) return;
            foreach (var column in grid.Columns)
                column.Width = new System.Windows.Controls.DataGridLength(column.Width.Value, column.Width.UnitType);
        }, DispatcherPriority.Loaded);
    }

    private static void Group(ICollectionView view, string property)
    {
        if (view.GroupDescriptions.Count == 0) view.GroupDescriptions.Add(new PropertyGroupDescription(property));
    }
    private void SearchChanged(object? sender, PropertyChangedEventArgs e)
    {
        var grid = e.PropertyName switch
        {
            nameof(MainWindow.SearchText) => ItemGrid,
            nameof(MainWindow.SpecializedSearchText) => SpecializedGrid,
            nameof(MainWindow.EnchantmentSearchText) => EnchantmentGrid,
            _ => null
        };
        var text = e.PropertyName switch
        {
            nameof(MainWindow.SearchText) => _host.SearchText,
            nameof(MainWindow.SpecializedSearchText) => _host.SpecializedSearchText,
            nameof(MainWindow.EnchantmentSearchText) => _host.EnchantmentSearchText,
            _ => ""
        };
        if (grid is not null) Dispatcher.BeginInvoke(() => ReviewGroupExpander.RevealMatches(grid, !string.IsNullOrWhiteSpace(text)), DispatcherPriority.DataBind);
    }

    private static IEnumerable<T> Selected<T>(IList rows) => rows.Cast<T>().ToArray();

    // Rows are matched by plugin or FormKey, because a rescan creates new row objects.
    private static string? RowKey(object row) => row switch
    {
        PluginDecision plugin => plugin.Plugin,
        CandidateItem item => item.FormKey,
        EnchantmentSelectionOption enchantment => enchantment.FormKey,
        _ => null
    };
    private readonly Dictionary<DataGrid, (HashSet<string> Selected, double Offset, HashSet<object> Expanded)> _places = [];
    private IEnumerable<DataGrid> Grids() => [PluginGrid, ItemGrid, SpecializedGrid, EnchantmentGrid];
    private static ScrollViewer? Scroller(DependencyObject node)
    {
        if (node is ScrollViewer viewer) return viewer;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
            if (Scroller(VisualTreeHelper.GetChild(node, i)) is { } found) return found;
        return null;
    }
    private void SavePlace()
    {
        _places.Clear();
        foreach (var grid in Grids())
            _places[grid] = (grid.SelectedItems.Cast<object>().Select(RowKey).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase),
                Scroller(grid)?.VerticalOffset ?? 0, ReviewGroupExpander.ExpandedGroups(grid));
    }
    private void RestorePlace()
    {
        foreach (var (grid, place) in _places)
        {
            ReviewGroupExpander.RestoreExpanded(grid, place.Expanded);
            grid.SelectedItems.Clear();
            foreach (var row in grid.Items.Cast<object>().Where(row => RowKey(row) is { } key && place.Selected.Contains(key)).ToArray())
                grid.SelectedItems.Add(row);
        }
        var offsets = _places.ToDictionary(x => x.Key, x => x.Value.Offset);
        var expanded = _places.ToDictionary(x => x.Key, x => x.Value.Expanded);
        Dispatcher.BeginInvoke(() =>
        {
            foreach (var (grid, names) in expanded) ReviewGroupExpander.RestoreExpanded(grid, names);
            foreach (var (grid, offset) in offsets) Scroller(grid)?.ScrollToVerticalOffset(offset);
        }, DispatcherPriority.Loaded);
    }
    private void PluginBatchChoiceChanged(object sender, SelectionChangedEventArgs e) => UpdatePluginBatchState();
    private void PluginSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ReferenceEquals(e.OriginalSource, PluginGrid)) UpdatePluginBatchState();
    }
    private void UpdatePluginBatchState()
    {
        if (PluginGrid is null || PluginBatchChoice is null || ApplyPluginBatchButton is null || PluginBatchSelectionText is null) return;
        var count = PluginGrid.SelectedItems.Count;
        PluginBatchSelectionText.Text = $"{count} plugin{(count == 1 ? "" : "s")} selected";
        ApplyPluginBatchButton.IsEnabled = count > 0 && PluginBatchChoice.SelectedItem is PatchPolicyChoice;
        if (PluginBatchResultText is not null) PluginBatchResultText.Visibility = Visibility.Collapsed;
    }
    private void ApplyPluginBatch(object sender, RoutedEventArgs e)
    {
        if (PluginBatchChoice.SelectedItem is not PatchPolicyChoice choice) return;
        var rows = Selected<PluginDecision>(PluginGrid.SelectedItems).ToArray();
        if (rows.Length == 0) return;
        _host.SetPluginPolicies(rows, choice.Value);
        UpdatePluginBatchState();
        PluginBatchResultText.Text = $"Applied {choice.Label} to {rows.Length} plugin{(rows.Length == 1 ? "" : "s")}.";
        PluginBatchResultText.Visibility = Visibility.Visible;
    }
    private void PluginGridSorting(object sender, DataGridSortingEventArgs e)
    {
        if (!e.Column.SortMemberPath.Equals(nameof(PluginDecision.Policy), StringComparison.Ordinal)) return;
        e.Handled = true;
        var direction = e.Column.SortDirection == ListSortDirection.Ascending
            ? ListSortDirection.Descending : ListSortDirection.Ascending;
        foreach (var column in PluginGrid.Columns) column.SortDirection = null;
        e.Column.SortDirection = direction;
        using (PluginGrid.Items.DeferRefresh())
        {
            PluginGrid.Items.SortDescriptions.Clear();
            PluginGrid.Items.SortDescriptions.Add(new(nameof(PluginDecision.Policy), direction));
            PluginGrid.Items.SortDescriptions.Add(new(nameof(PluginDecision.Plugin), ListSortDirection.Ascending));
        }
    }
    private void SpecializedBatchChanged(object sender, SelectionChangedEventArgs e) => UpdateSpecializedBatchState();
    private void SpecializedSelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateSpecializedBatchState();
    private void UpdateSpecializedBatchState()
    {
        if (SpecializedGrid is null || ApplySpecializedBatchButton is null || SpecializedBatchSelectionText is null) return;
        var count = SpecializedGrid.SelectedItems.Count;
        SpecializedBatchSelectionText.Text = $"{count} item{(count == 1 ? "" : "s")} selected";
        ApplySpecializedBatchButton.IsEnabled = count > 0 && SpecializedBatchChoice.SelectedItem is SpecializedDistributionChoice choice
            && (choice.Value != SpecializedDistributionOption.OriginalLocations || Selected<CandidateItem>(SpecializedGrid.SelectedItems).Any(x => x.CanUseOriginalLocations));
    }
    private void ApplySpecializedBatch(object sender, RoutedEventArgs e)
    {
        if (SpecializedBatchChoice.SelectedItem is not SpecializedDistributionChoice choice) return;
        var rows = Selected<CandidateItem>(SpecializedGrid.SelectedItems).ToArray();
        var applied = 0;
        foreach (var row in rows)
        {
            if (choice.Value == SpecializedDistributionOption.OriginalLocations && !row.CanUseOriginalLocations) continue;
            row.SpecializedSelection = choice.Value;
            applied++;
        }
        SpecializedBatchResultText.Text = $"Applied to {applied} item(s)." + (applied < rows.Length ? $" Skipped {rows.Length - applied} item(s) with no supported original route." : "");
        SpecializedBatchResultText.Visibility = Visibility.Visible;
    }
    private void UseRecommendations(object sender, RoutedEventArgs e) => _host.UseSpecializedRecommendations(Selected<CandidateItem>(SpecializedGrid.SelectedItems));
    private void CopySpecializedEvidence(object sender, RoutedEventArgs e)
    {
        if (SpecializedGrid.SelectedItem is CandidateItem item && !string.IsNullOrWhiteSpace(item.LootReviewDetails))
            System.Windows.Clipboard.SetText(item.LootReviewDetails);
    }
    private void ItemBatchChanged(object sender, SelectionChangedEventArgs e) => UpdateItemBatchState();
    private void ItemSelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateItemBatchState();
    private void UpdateItemBatchState()
    {
        if (ItemGrid is null || ApplyItemBatchButton is null || ItemBatchSelectionText is null) return;
        var count = ItemGrid.SelectedItems.Count;
        ItemBatchSelectionText.Text = $"{count} item{(count == 1 ? "" : "s")} selected";
        ApplyItemBatchButton.IsEnabled = count > 0 && ItemBatchChoice.SelectedIndex >= 0;
    }
    private void ApplyItemBatch(object sender, RoutedEventArgs e)
    {
        if (ItemBatchChoice.SelectedIndex is 0 or 1)
            _host.SetSelectedItems(Selected<CandidateItem>(ItemGrid.SelectedItems), ItemBatchChoice.SelectedIndex == 0);
    }
    private void UseMaterialRecommendations(object sender, RoutedEventArgs e) => _host.UseMaterialFamilyRecommendations(Selected<CandidateItem>(ItemGrid.SelectedItems));
    private void EnchantmentSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ReferenceEquals(e.OriginalSource, EnchantmentGrid)) UpdateEnchantmentBatchState();
    }
    private void UpdateEnchantmentBatchState()
    {
        if (EnchantmentGrid is null || ApplyEnchantmentBatchButton is null || EnchantmentBatchSelectionText is null) return;
        var count = EnchantmentGrid.SelectedItems.Count;
        ApplyEnchantmentBatchButton.IsEnabled = count > 0 && EnchantmentBatchChoice.SelectedIndex >= 0;
        EnchantmentBatchSelectionText.Text = $"{count} enchantment{(count == 1 ? "" : "s")} selected";
    }
    private void SetSelectedEnchantments(bool? selected)
    {
        var rows = Selected<EnchantmentSelectionOption>(EnchantmentGrid.SelectedItems).ToArray();
        if (rows.Length > 0) _host.SetEnchantments(rows, selected);
    }
    private void EnchantmentBatchChanged(object sender, SelectionChangedEventArgs e) => UpdateEnchantmentBatchState();
    private void ApplyEnchantmentBatch(object sender, RoutedEventArgs e)
    {
        if (EnchantmentBatchChoice.SelectedIndex is >= 0 and <= 2)
            SetSelectedEnchantments(EnchantmentBatchChoice.SelectedIndex switch { 0 => true, 1 => false, _ => (bool?)null });
    }
    private void RestoreEnchantmentDefaults(object sender, RoutedEventArgs e) => _host.SetEnchantments(_host.Enchantments, null);
}

