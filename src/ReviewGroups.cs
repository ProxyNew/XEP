using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using DataGrid = System.Windows.Controls.DataGrid;

namespace XEnchantmentPatcher;

// Expansion belongs to this review grid, not to the engine's item selection. Plugin
// groups start collapsed; a search expands the groups it shows.
public sealed class ReviewGroupExpander : Expander
{
    private static readonly ConditionalWeakTable<DataGrid, Dictionary<object, bool>> States = new();
    // Grids showing search results: their regenerated groups start expanded.
    private static readonly ConditionalWeakTable<DataGrid, object> Searching = new();
    private bool _restoring;
    private bool _ready;
    public ReviewGroupExpander()
    {
        IsExpanded = false;
        Loaded += (_, _) => Restore();
        Unloaded += (_, _) => _ready = false;
        Expanded += (_, _) => Remember();
        Collapsed += (_, _) => Remember();
    }
    private DataGrid? Grid()
    {
        DependencyObject? node = this;
        while (node is not null && node is not DataGrid) node = VisualTreeHelper.GetParent(node);
        return node as DataGrid;
    }
    private void Restore()
    {
        if (Grid() is not { } grid || DataContext is not System.Windows.Data.CollectionViewGroup group) return;
        _restoring = true;
        IsExpanded = States.GetOrCreateValue(grid).GetValueOrDefault(group.Name, Searching.TryGetValue(grid, out _));
        _restoring = false;
        _ready = true;
    }
    private void Remember()
    {
        if (_ready && !_restoring && IsLoaded && Grid() is { } grid && DataContext is System.Windows.Data.CollectionViewGroup group)
            States.GetOrCreateValue(grid)[group.Name] = IsExpanded;
    }
    // Group names currently expanded in this grid (kept across rescans by the review).
    internal static HashSet<object> ExpandedGroups(DataGrid grid)
    {
        var names = States.GetOrCreateValue(grid).Where(x => x.Value).Select(x => x.Key).ToHashSet();
        void Visit(DependencyObject node)
        {
            if (node is ReviewGroupExpander { IsExpanded: true, DataContext: System.Windows.Data.CollectionViewGroup group }) names.Add(group.Name);
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++) Visit(VisualTreeHelper.GetChild(node, i));
        }
        Visit(grid);
        return names;
    }

    internal static void RestoreExpanded(DataGrid grid, HashSet<object> names)
    {
        var states = States.GetOrCreateValue(grid);
        foreach (var name in names) states[name] = true;
        void Visit(DependencyObject node)
        {
            if (node is ReviewGroupExpander { DataContext: System.Windows.Data.CollectionViewGroup group } expander && names.Contains(group.Name)) expander.IsExpanded = true;
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++) Visit(VisualTreeHelper.GetChild(node, i));
        }
        Visit(grid);
    }

    // A search expands the matching groups; clearing it collapses them again.
    internal static void RevealMatches(DataGrid grid, bool expand = true)
    {
        States.GetOrCreateValue(grid).Clear();
        if (expand) Searching.AddOrUpdate(grid, new()); else Searching.Remove(grid);
        void Expand(DependencyObject node)
        {
            if (node is ReviewGroupExpander expander) expander.IsExpanded = expand;
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++) Expand(VisualTreeHelper.GetChild(node, i));
        }
        Expand(grid);
    }
}
