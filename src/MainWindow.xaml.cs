using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using WpfMessageBox = System.Windows.MessageBox;

namespace XEnchantmentPatcher;

public static class PolicyValues
{
    public static IReadOnlyList<PatchPolicyChoice> All { get; } =
    [
        new(PatchPolicy.Review, "Review"),
        new(PatchPolicy.ExistingDistribution, "Existing Distribution"),
        new(PatchPolicy.Exclude, "Exclude")
    ];
}

internal sealed class DeferredScanState
{
    private long _revision;
    private long _completedRevision;

    public bool IsDirty => _revision > _completedRevision;
    public long Revision => _revision;

    public void Mark() => _revision++;

    public void Complete(long revision) => _completedRevision = Math.Max(_completedRevision, revision);
}

internal sealed class PreviewCacheState
{
    private long _revision;
    private long _previewRevision = -1;
    private string _reason = "No exact preview has been calculated.";
    private DateTime? _calculatedAt;

    public GenerationPreview? Preview { get; private set; }
    public bool IsCurrent => Preview is not null && _previewRevision == _revision;
    public string StateText => Preview is null
        ? "No exact preview calculated."
        : IsCurrent
            ? $"Preview current — calculated {_calculatedAt:HH:mm:ss}. Generate will reuse it."
            : $"Preview out of date — {_reason}";

    public bool TryGetCurrent(out GenerationPreview preview)
    {
        preview = Preview!;
        return IsCurrent;
    }

    public void Invalidate(string reason)
    {
        _revision++;
        _reason = string.IsNullOrWhiteSpace(reason) ? "settings or selections changed." : reason.Trim().TrimEnd('.') + ".";
    }

    public void Store(GenerationPreview preview)
    {
        Preview = preview;
        _previewRevision = _revision;
        _calculatedAt = DateTime.Now;
    }
}

public partial class MainWindow : Window, INotifyPropertyChanged
{
    internal BusyIcon MainBusyIcon = null!;

    internal readonly AppSettings _settings;
    private ScanResult? _scan;
    // Scan's loaded plugins and analysis, reused by Preview, Generate and settings-driven
    // rescans. A Data folder or profile change discards it; the session end releases it.
    private ScanSnapshot? _snapshot;
    private string _profileName;
    private string _status = "Loading the active load order. Launch through MO2 when using its virtual mod files.";
    private string _searchText = "";
    private string _enchantmentSearchText = "";
    private double? _operationProgress;
    private bool _isBusyVisual;
    private string _busyTitle = "Scanning load order";
    private bool _suppressItemEvents;
    private bool _suppressProviderEvents;
    private bool _suppressEnchantmentEvents;
    private System.Windows.Point _providerDragStart;
    private EnchantmentProviderOption? _draggedProvider;
    internal readonly DeferredScanState _deferredScan = new();
    internal readonly PreviewCacheState _previewCache = new();
    private bool _scanRefreshInProgress;
    // Set at startup and on a profile switch; only a successful discovery clears it.
    internal bool _discoveryOwed;

    public ObservableCollection<PluginDecision> Plugins { get; } = [];
    public ICollectionView PluginsView { get; }
    public ObservableCollection<CandidateItem> Items { get; } = [];
    public ObservableCollection<EnchantmentProviderOption> Providers { get; } = [];
    public ObservableCollection<EnchantmentSelectionOption> Enchantments { get; } = [];
    public ObservableCollection<string> ProfileNames { get; } = [];
    public ObservableCollection<CandidateItem> SpecializedItems { get; } = [];
    public ICollectionView SpecializedItemsView { get; }
    private string _specializedSearchText = "";
    public string SpecializedSearchText { get => _specializedSearchText; set { _specializedSearchText = value ?? ""; Changed(); SpecializedItemsView.Refresh(); } }
    public ICollectionView ItemsView { get; }
    public ICollectionView EnchantmentsView { get; }

    public string ProfileName { get => _profileName; set { _profileName = value; Changed(); } }
    public string? DataFolder
    {
        get => _settings.DataFolder;
    }
    public string? OutputFolder { get => _settings.OutputFolder; set { _settings.OutputFolder = EmptyToNull(value); Changed(); InvalidatePreview("output folder changed"); } }
    public bool BroadUnmatchedDistribution
    {
        get => _settings.CurrentProfile.BroadUnmatchedDistribution;
        set { _settings.CurrentProfile.BroadUnmatchedDistribution = value; Changed(); SaveSettings(); RequestScanRefresh("broader fallback"); }
    }
    public bool AllowOtherMaterialRoutes
    {
        get => _settings.CurrentProfile.AllowOtherMaterialRoutes;
        set { _settings.CurrentProfile.AllowOtherMaterialRoutes = value; Changed(); SaveSettings(); RequestScanRefresh("specialized material expansion"); }
    }
    public bool ResolveLeveledListConflicts
    {
        get => _settings.CurrentProfile.ResolveLeveledListConflicts;
        set { _settings.CurrentProfile.ResolveLeveledListConflicts = value; Changed(); SaveSettings(); RequestScanRefresh("leveled-list conflict resolution"); }
    }
    public bool KeepUndistributedVariants
    {
        get => _settings.CurrentProfile.KeepUndistributedVariants;
        set { _settings.CurrentProfile.KeepUndistributedVariants = value; Changed(); SaveSettings(); InvalidatePreview("undistributed-record retention changed"); }
    }
    public string SearchText { get => _searchText; set { _searchText = value ?? ""; Changed(); ItemsView.Refresh(); } }
    public string EnchantmentSearchText { get => _enchantmentSearchText; set { _enchantmentSearchText = value ?? ""; Changed(); EnchantmentsView.Refresh(); } }
    public string Status { get => _status; set { _status = value; SessionRuntime.Log(value); Changed(); } }
    public bool IsPreviewCurrent => _previewCache.IsCurrent;
    public string PreviewStateText => _previewCache.StateText;
    public bool IsBusyVisual { get => _isBusyVisual; internal set { _isBusyVisual = value; Changed(); } }
    public string BusyTitle { get => _busyTitle; private set { _busyTitle = value; Changed(); } }
    // Overall operation completion in percent; null until an operation reports, shown as indeterminate.
    public double? OperationProgressPercent { get => _operationProgress; internal set { _operationProgress = value; Changed(); Changed(nameof(IsOperationProgressUnknown)); } }
    public bool IsOperationProgressUnknown => _operationProgress is null;

    public MainWindow() : this(SettingsStore.Load()) { }

    public MainWindow(AppSettings settings)
    {
        _settings = settings;
        // The interactive session applies an installed mod-author rules file.
        _settings.ApplyAuthorRules = true;
        UiTheme.Apply(settings.DarkTheme);
        UiTheme.Attach(this);
        _profileName = _settings.ProfileName;
        RefreshProfileNames();
        PluginsView = new ListCollectionView(Plugins)
        {
            Filter = row => row is PluginDecision { EligibleItems: > 0, HasDistributionCandidates: true }
        };
        SpecializedItemsView = CollectionViewSource.GetDefaultView(SpecializedItems);
        SpecializedItemsView.Filter = o => o is CandidateItem c && c.IsPlayable && c.HasSpecializedEquipmentKind && IsPluginEnabledForReview(c.Plugin) &&
            ($"{c.Plugin} {c.Name} {c.EditorId} {c.FormKey} {c.Category} {c.SpecializedDecisionReason} {c.SourceDistributionSummary} {c.SpecializedOptionSummary}")
            .Contains(_specializedSearchText, StringComparison.OrdinalIgnoreCase);
        ItemsView = CollectionViewSource.GetDefaultView(Items);
        ItemsView.Filter = FilterItem;
        EnchantmentsView = CollectionViewSource.GetDefaultView(Enchantments);
        EnchantmentsView.Filter = FilterEnchantment;
        EnsureCurrentProviderOptions();
        InitializeComponent();
        MainBusyIcon = new BusyIcon { Width = 112, Height = 112, HorizontalAlignment = System.Windows.HorizontalAlignment.Center };
        MainBusyIconHost.Content = MainBusyIcon;
        if (SessionRuntime.DirectoryPath is not null)
            Title += $" — session {SessionRuntime.Id[..Math.Min(8, SessionRuntime.Id.Length)]} — {SettingsStore.PathName}";
        DataContext = this;
        Closing += (_, e) =>
        {
            // A running operation only disables the window's content: the title-bar close and
            // Alt+F4 still arrive, and closing would discard the data the operation uses.
            if (OperationInProgress)
            {
                e.Cancel = true;
                // Told after Closing returns: a box shown inside it keeps the window closing, and
                // the window's own Close() after a generation would then throw. A shutdown
                // (logoff, an unhandled exception) closes the window anyway; then there is no box.
                var running = BusyTitle;
                Dispatcher.BeginInvoke(() =>
                {
                    if (!IsVisible || !OperationInProgress || App.EndingAfterFailure) return;
                    WpfMessageBox.Show(this, $"{running} is still running. Wait for it to finish, then close the window.",
                        "Operation in progress", MessageBoxButton.OK, MessageBoxImage.Information);
                });
                return;
            }
            // An unhandled exception is ending the application; nothing here can stop that.
            if (App.EndingAfterFailure) return;
            _closing = true;
            if (!_settingsSave.HasUnsavedChanges || SaveSettings(notify: false)) return;
            e.Cancel = WpfMessageBox.Show(this, SettingsSaveNotice + "\n\nClose and discard the unsaved choices in this window?",
                "Unsaved settings", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes;
            _closing = !e.Cancel;
        };
        Closed += (_, _) =>
        {
            // Only a shutdown closes the window while an operation runs; it was interrupted.
            var interrupted = OperationInProgress;
            if (interrupted) SessionRuntime.Log($"The window closed while '{BusyTitle}' was running; the session is recorded as interrupted.");
            DiscardSnapshot();
            // The unhandled-exception handler has already recorded the session as failed.
            if (App.EndingAfterFailure) return;
            try { SessionRuntime.Finish(_settingsSave.HasUnsavedChanges || interrupted ? 1 : 0); }
            catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
            {
                SessionRuntime.ExitCode = 1;
                WpfMessageBox.Show("The window closed, but session completion could not be recorded. " + ex.Message,
                    "Session metadata error", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        };
    }

    internal ScanSnapshot Snapshot => _snapshot ?? throw new InvalidOperationException("No scan data is loaded. Wait for the scan to finish.");

    // Startup discovers the origins only; the first Patch Review or Generate scans.
    internal async Task InitializeAsync()
    {
        _discoveryOwed = true;
        _deferredScan.Mark();
        await RefreshLoadOrder(discoverOnly: true);
    }

    internal void ToggleTheme(object sender, RoutedEventArgs e)
    {
        _settings.DarkTheme = !_settings.DarkTheme;
        UiTheme.Apply(_settings.DarkTheme); SaveSettings();
    }

    private void ExportModSettings(object sender, RoutedEventArgs e)
    {
        if (ProfileActionsBlocked) return;
        try
        {
            var mods = ModSettingsExporter.Available(_settings.CurrentProfile);
            if (mods.Count == 0)
            {
                WpfMessageBox.Show(this, "Configure a plugin rule or individual equipment, material, specialized loot or enchantment choice before exporting.", "No mod settings to export");
                return;
            }
            var selection = new ModSettingsExportWindow(mods) { Owner = this };
            if (selection.ShowDialog() != true) return;
            var save = new Microsoft.Win32.SaveFileDialog
            {
                Title = "Export selected mods' rules file", Filter = "JSON rules files (*.json)|*.json",
                DefaultExt = ".json", AddExtension = true, FileName = ModSettingsExporter.SuggestedFileName(selection.SelectedPlugins), OverwritePrompt = true
            };
            if (save.ShowDialog(this) != true) return;
            ModSettingsExporter.Save(save.FileName, ModSettingsExporter.Create(_settings.CurrentProfile, selection.SelectedPlugins));
            Status = $"Exported a rules file for {selection.SelectedPlugins.Length} mod(s) to {save.FileName}.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or FormatException)
        {
            WpfMessageBox.Show(this, ex.Message, "Could not export mod settings", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void BrowseProfiles(object sender, RoutedEventArgs e)
    {
        if (ProfileActionsBlocked) return;
        var dialog = new ProfileBrowserWindow(ProfileNames, ProfileName) { Owner = this };
        if (dialog.ShowDialog() == true && dialog.SelectedProfile is { } profile)
        {
            SettingsStore.SelectExistingProfile(_settings, profile);
            RefreshSelectedProfile();
        }
    }

    private void DiscardSnapshot()
    {
        _snapshot?.Dispose();
        _snapshot = null;
    }

    private void RequestScanRefresh(string reason)
    {
        SaveDecisions();
        _deferredScan.Mark();
        Changed(nameof(HasPendingScanChanges));
        InvalidatePreview(reason + " changed");
        // Before the first scan there are no rows to be out of date.
        Status = _scan is null ? "Open Patch Review or Generate Patch to scan."
            : "Patch Review rows are out of date. Choose Update UI for current settings in Patch Review, or Generate Patch, to rescan.";
    }

    // Shows Patch Review's update button; raised where the deferred scan is marked or completed.
    public bool HasPendingScanChanges => _deferredScan.IsDirty;

    // Generate and Patch Review need a current scan. A request that cannot scan shows the
    // status line's reason; a failed scan has already shown its error box.
    internal async Task<bool> FlushPendingScanAsync()
    {
        var revision = _deferredScan.Revision;
        if (_deferredScan.IsDirty) await RefreshLoadOrder();
        // Answers to the mod-author prompt mark the scan pending again; one more scan settles them.
        if (_deferredScan.Revision > revision && !_scanFailed) await RefreshLoadOrder();
        if (_deferredScan.IsDirty && !_scanFailed) WpfMessageBox.Show(Status, Title);
        return !_deferredScan.IsDirty && _scan is not null;
    }

    // Discover only: stops before the scan and leaves it pending.
    private async Task RefreshLoadOrder(bool discoverOnly = false)
    {
        if (_scanRefreshInProgress) return;
        _scanRefreshInProgress = true;
        var scanRevision = _deferredScan.Revision;
        BusyTitle = discoverOnly ? "Discovering enchantment origins" : _scan is null ? "Scanning load order" : "Rescanning patch data";
        var scanned = false;
        try
        {
            scanned = await Busy(async () =>
            {
                if (!Directory.Exists(DataFolder))
                {
                    Status = "Choose a valid Data folder to discover enchantment origins.";
                    return;
                }
                if (_discoveryOwed)
                {
                    Status = "Discovering enchantment origins and usable templates…";
                    var discovered = await Task.Run(() => PatcherEngine.DiscoverProviders(_settings));
                    var selectedOrder = _settings.CurrentProfile.EnchantmentProviderPlugins.ToList();
                    var selectedNames = selectedOrder.ToHashSet(StringComparer.OrdinalIgnoreCase);
                    var discoveredByName = discovered.ToDictionary(x => x.Plugin, StringComparer.OrdinalIgnoreCase);
                    var visualOrder = _settings.CurrentProfile.EnchantmentProviderOrder
                        .Concat(selectedOrder).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                    _suppressProviderEvents = true;
                    Providers.Clear();
                    foreach (var name in visualOrder)
                    {
                        if (!discoveredByName.TryGetValue(name, out var provider) && !selectedNames.Contains(name)) continue;
                        provider ??= new EnchantmentProviderOption(name, 0, 0);
                        provider.PropertyChanged += ProviderChanged;
                        provider.IsSelected = selectedNames.Contains(name);
                        Providers.Add(provider);
                    }
                    foreach (var provider in discovered.Where(x => Providers.All(p => !p.Plugin.Equals(x.Plugin, StringComparison.OrdinalIgnoreCase))))
                    {
                        provider.PropertyChanged += ProviderChanged;
                        provider.IsSelected = false;
                        Providers.Add(provider);
                    }
                    _suppressProviderEvents = false;
                    _discoveryOwed = false;
                }
                // A saved origin missing from the load order has no usable template.
                if (!Providers.Any(x => x.IsSelected) || Providers.Any(x => x.IsSelected && x.TemplateCount == 0))
                {
                    Status = Providers.All(x => x.TemplateCount == 0)
                        ? "No usable enchantment origins are available in the active load order."
                        : "Select available enchantment origins, then open Patch Review or Generate Patch to scan. Deselect any unavailable saved origins.";
                    return;
                }
                if (discoverOnly)
                {
                    Status = "Enchantment origins found. Open Patch Review or Generate Patch to scan.";
                    return;
                }
                Status = "Scanning equipment, enchantments, availability and leveled-list routes…";
                // The plugins load only for the first Scan or after a Data folder or profile
                // change; a settings-driven rescan analyses the loaded plugins again.
                ScanResult scan;
                if (_snapshot is null)
                {
                    (scan, _snapshot) = await Task.Run(() => RunWithProgress(() =>
                    {
                        var result = PatcherEngine.Scan(_settings, out var created);
                        return (result, created);
                    }, kind: ProgressKind.Scan));
                }
                else
                {
                    var current = _snapshot;
                    scan = await Task.Run(() => RunWithProgress(
                        () => PatcherEngine.Scan(_settings, current), kind: ProgressKind.Scan));
                }
                _scan = scan;
                _deferredScan.Complete(scanRevision);
                RowsReplacing?.Invoke();
                Plugins.Clear();
                Items.Clear();
                SpecializedItems.Clear();
                Enchantments.Clear();
                foreach (var row in scan.Plugins)
                {
                    row.PropertyChanged += PluginChanged;
                    Plugins.Add(row);
                }
                _suppressItemEvents = true;
                foreach (var item in scan.Items)
                {
                    item.PropertyChanged += ItemChanged;
                    Items.Add(item);
                    if (item.RequiresLootReview && item.IsPlayable && item.HasSpecializedEquipmentKind) SpecializedItems.Add(item);
                }
                _suppressItemEvents = false;
                _suppressEnchantmentEvents = true;
                foreach (var enchantment in scan.Enchantments)
                {
                    enchantment.PropertyChanged += EnchantmentChanged;
                    Enchantments.Add(enchantment);
                }
                _suppressEnchantmentEvents = false;
                ItemsView.Refresh();
                SpecializedItemsView.Refresh();
                EnchantmentsView.Refresh();
                RowsReplaced?.Invoke();
                Status = scan.Warnings.Count == 0 ? "Scan complete. Review plugins or individual items, then choose Generate Patch."
                    : $"Scan complete with {scan.Warnings.Count} load-order warning(s). Review plugins or individual items, then choose Generate Patch.";
                if (scan.ScriptAnalysisWarning is not null)
                    Status += " Script analysis is unavailable; unresolved script contexts remain protected.";
                // Answers that change scan inputs leave the rows out of date; the status says so.
                if (AskAuthorConflicts()) RequestScanRefresh("mod-author rules answers");
            }, kind: ProgressKind.Scan);
        }
        finally
        {
            _scanRefreshInProgress = false;
            // A failed scan leaves its inputs unscanned, so the next Generate, update button or
            // first Patch Review scans again; nothing retries on its own.
            _scanFailed = !scanned;
            if (!scanned)
            {
                _deferredScan.Mark();
                Status += " Choose Generate Patch, or Patch Review and its update button, to scan again.";
            }
            Changed(nameof(HasPendingScanChanges));
        }
    }

    private async void Generate(object sender, RoutedEventArgs e)
    {
        if (!await FlushPendingScanAsync()) return;
        SaveDecisions();
        var review = CurrentPolicies().Count(x => x.Value == PatchPolicy.Review);
        if (review > 0) { WpfMessageBox.Show($"{review} plugins still require a decision.", Title); return; }

        BusyTitle = "Generating and validating ESPFE output";
        var generated = false;
        await Busy(async () =>
        {
            var reusedPreview = _previewCache.TryGetCurrent(out var currentPreview);
            Status = reusedPreview ? "Using the current exact preview before generation…" : "Checking the exact output before generation…";
            var preview = reusedPreview ? currentPreview : await CreateAndCachePreview(ProgressKind.Generation);
            // Always confirm: the output layout is chosen here.
            var confirmation = new GenerationConfirmationWindow(preview) { Owner = this };
            if (confirmation.ShowDialog() != true)
            {
                Status = "No files written. Generation confirmation was cancelled or closed; choose Generate to try again.";
                return;
            }
            // The plan does not depend on the layout, so the preview stays reusable.
            _settings.CurrentProfile.FullOutputPlugin = confirmation.FullOutputPlugin;
            BusyTitle = confirmation.FullOutputPlugin ? "Generating and validating the full plugin" : "Generating and validating ESPFE output";

            Status = "Generating enchanted variants and leveled-list overrides…";
            var policies = CurrentPolicies();
            var snapshot = Snapshot;
            var reusePreview = _previewCache.IsCurrent;
            var initialFraction = (OperationProgressPercent ?? 0) / 100;
            PatchReport report;
            // An unhandled exception is already ending the application: start no write it could cut short.
            if (App.EndingAfterFailure) return;
            WritingOutput = true;
            try
            {
                report = await Task.Run(() => RunWithProgress(
                    () => PatcherEngine.Generate(_settings, policies, snapshot, reusePreview),
                    ProgressKind.Generation, initialFraction: initialFraction));
            }
            finally
            {
                WritingOutput = false;
                // An unhandled exception during the write postponed the shutdown until now.
                if (App.EndingAfterFailure) System.Windows.Application.Current?.Shutdown(1);
            }
            if (App.EndingAfterFailure) return;
            SaveSettings();
            Status = $"Complete: {report.Variants} new, {report.ReusedVariants} reused variants; {report.UndistributedVariants} undistributed; {report.LeveledListsOverridden} lists updated.";
            // Writing output changes no scan input, so the snapshot stays valid.
            InvalidatePreview("generated output changed");
            var backup = report.BackupFolder is null ? "" : $"\nPrevious output backed up to:\n{report.BackupFolder}\n";
            WpfMessageBox.Show($"{report.Outputs.Count} {(_settings.CurrentProfile.FullOutputPlugin ? "full plugin(s)" : "ESPFE file(s)")} written and fully validated.\n\n{string.Join(Environment.NewLine, report.Outputs)}\n{backup}\nVariants: {report.Variants}\nSource items: {report.SourceItems}\nLeveled lists: {report.LeveledListsOverridden}", Title);
            generated = true;
        }, kind: ProgressKind.Generation);
        if (generated && !_closing) Close();
    }

    private async Task<GenerationPreview> CreateAndCachePreview(ProgressKind kind = ProgressKind.Preview)
    {
        SaveDecisions();
        var snapshot = Snapshot;
        var policies = CurrentPolicies();
        var preview = await Task.Run(() => RunWithProgress(
            () => PatcherEngine.Preview(_settings, policies, snapshot), kind,
            complete: kind != ProgressKind.Generation));
        _previewCache.Store(preview);
        var bySource = preview.Placements.ToDictionary(x => x.Source, StringComparer.OrdinalIgnoreCase);
        foreach (var item in Items)
        {
            item.ApplyPlacementPreview(bySource.GetValueOrDefault(item.FormKey));
        }
        Changed(nameof(IsPreviewCurrent));
        Changed(nameof(PreviewStateText));
        return preview;
    }

    internal Dictionary<string, PatchPolicy> CurrentPolicies() =>
        // Hidden plugins must not require a review decision. Keep their saved
        // decisions, however: a plugin with only direct script equipment can
        // still have productive distribution despite having no list items.
        Plugins.Where(x => x.EligibleItems > 0 && (x.HasDistributionCandidates || x.Policy != PatchPolicy.Review))
            .ToDictionary(x => x.Plugin, x => x.Policy, StringComparer.OrdinalIgnoreCase);

    private T RunWithProgress<T>(Func<T> operation, ProgressKind kind = ProgressKind.Preview,
        bool complete = true, double initialFraction = 0)
    {
        using var progress = OperationProgress.Begin(update => Dispatcher.Invoke(() =>
        {
            Status = update.Message;
            OperationProgressPercent = update.Fraction * 100;
        }), kind, initialFraction);
        using var scan = kind == ProgressKind.Scan ? ScanProgress.Begin(OperationProgress.Fraction) : null;
        var result = operation();
        if (complete) OperationProgress.Fraction(1);
        return result;
    }

    internal bool OperationInProgress => IsBusyVisual || _scanRefreshInProgress;
    // True while a close request is past the operation check: WPF refuses Close() then.
    private bool _closing;
    // True while the engine writes and installs a generation's files (several moves).
    internal bool WritingOutput { get; private set; }
    // The last scan failed; its error box has already explained why.
    private bool _scanFailed;
    private bool ProfileActionsBlocked => OperationInProgress;

    private void ManageProfile(object sender, RoutedEventArgs e)
    {
        if (ProfileActionsBlocked) return;
        var action = Enum.Parse<ProfileAction>((string)((System.Windows.Controls.Button)sender).Tag);
        var dialog = new ProfileActionDialog(action, _settings.ProfileName,
            name => SettingsStore.ValidateProfileName(_settings, name)) { Owner = this };
        if (dialog.ShowDialog() == true) ApplyProfileAction(action, dialog.AcceptedName);
    }

    internal bool ApplyProfileAction(ProfileAction action, string name = "")
    {
        if (ProfileActionsBlocked) return false;
        switch (action)
        {
            case ProfileAction.New: SettingsStore.NewProfile(_settings, name); break;
            case ProfileAction.Duplicate: SettingsStore.DuplicateProfile(_settings, name); break;
        }
        RefreshSelectedProfile();
        return true;
    }

    private void RefreshSelectedProfile()
    {
        RefreshProfileNames();
        _profileName = _settings.ProfileName;
        Changed(nameof(ProfileName));
        EnsureCurrentProviderOptions();
        Changed(nameof(AllowOtherMaterialRoutes));
        Changed(nameof(BroadUnmatchedDistribution));
        Changed(nameof(ResolveLeveledListConflicts));
        Changed(nameof(KeepUndistributedVariants));
        _scan = null;
        DiscardSnapshot();
        Plugins.Clear();
        Items.Clear();
        SpecializedItems.Clear();
        Enchantments.Clear();
        SaveSettings();
        _discoveryOwed = true;
        RequestScanRefresh("profile");
        _ = RefreshLoadOrder(discoverOnly: true);
    }

    internal void SetSelectedItems(IEnumerable<CandidateItem> rows, bool selected)
    {
        foreach (var item in rows)
        {
            // Include skips the rows the engine's rule will not select, as a tick there does not stick.
            if (selected && !EngineSelection(item, true)) continue;
            if (selected == DefaultSelected(item)) _settings.CurrentProfile.ItemOverrides.Remove(item.FormKey);
            else _settings.CurrentProfile.ItemOverrides[item.FormKey] = selected;
            _suppressItemEvents = true;
            item.Selected = selected;
            _suppressItemEvents = false;
        }
        SaveSettings();
        InvalidatePreview("item inclusion changed");
    }

    internal void PluginChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not PluginDecision row || e.PropertyName != nameof(PluginDecision.Policy)) return;
        StorePolicy(row);
        if (Items.Any(x => x.AuthorInclude is not null && x.Plugin.Equals(row.Plugin, StringComparison.OrdinalIgnoreCase)))
        {
            // Whether author inclusions apply depends on this policy (an exclusion asks first).
            SaveSettings();
            RequestScanRefresh("plugin rule with mod-author defaults");
            return;
        }
        _suppressItemEvents = true;
        foreach (var item in Items.Where(x => x.Plugin.Equals(row.Plugin, StringComparison.OrdinalIgnoreCase)))
            if (!_settings.CurrentProfile.ItemOverrides.ContainsKey(item.FormKey)) item.Selected = DefaultSelected(item);
        _suppressItemEvents = false;
        ItemsView.Refresh();
        SpecializedItemsView.Refresh();
        SaveSettings();
        InvalidatePreview("plugin distribution rule changed");
    }

    internal void ItemChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_suppressItemEvents || sender is not CandidateItem item) return;
        if (e.PropertyName == nameof(CandidateItem.MaterialFamily) && item.MaterialFamilyChoices.Count > 1)
        {
            if (!item.MaterialFamilyChoices.Contains(item.MaterialFamily, StringComparer.OrdinalIgnoreCase)) return;
            _suppressItemEvents = true;
            if (item.MaterialFamily.Equals(item.RecommendedMaterialFamily, StringComparison.OrdinalIgnoreCase))
            {
                _settings.CurrentProfile.MaterialFamilyOverrides.Remove(item.FormKey);
                item.HasMaterialFamilyOverride = false;
            }
            else
            {
                _settings.CurrentProfile.MaterialFamilyOverrides[item.FormKey] = item.MaterialFamily;
                item.HasMaterialFamilyOverride = true;
            }
            _suppressItemEvents = false;
            SaveSettings(); RequestScanRefresh("material family"); return;
        }
        if (e.PropertyName == nameof(CandidateItem.IgnoreAuthorDestinationList))
        {
            var ignored = _settings.CurrentProfile.IgnoredAuthorDestinationLists;
            if (item.IgnoreAuthorDestinationList) ignored.Add(item.FormKey); else ignored.Remove(item.FormKey);
            SaveSettings(); RequestScanRefresh("mod-author destination list"); return;
        }
        if (e.PropertyName == nameof(CandidateItem.IgnoreAuthorEnchantmentList))
        {
            var ignored = _settings.CurrentProfile.IgnoredAuthorEnchantmentLists;
            if (item.IgnoreAuthorEnchantmentList) ignored.Add(item.FormKey); else ignored.Remove(item.FormKey);
            SaveSettings(); RequestScanRefresh("mod-author enchantment list"); return;
        }
        if (e.PropertyName == nameof(CandidateItem.SpecializedMode))
        {
            // Choosing the author's value again saves no override.
            var authorValue = item.AuthorMode && item.SpecializedMode == item.RecommendedSpecializedMode;
            if (authorValue) _settings.CurrentProfile.SpecializedLootOverrides.Remove(item.FormKey);
            else _settings.CurrentProfile.SpecializedLootOverrides[item.FormKey] = item.SpecializedMode;
            item.HasSpecializedOverride = !authorValue;
            ShowSelection(item);
            SaveSettings(); InvalidatePreview("specialized distribution choice changed"); return;
        }
        if (e.PropertyName != nameof(CandidateItem.Selected)) return;
        // A tick the engine's rule will not honour does not stick and saves nothing: the item is
        // ineligible, or in no loot list and not following script distribution. The row's
        // evidence (Patch and Base distribution) says why.
        if (item.Selected && !EngineSelection(item, true))
        {
            ShowSelection(item);
            return;
        }
        if (item.Selected == DefaultSelected(item)) _settings.CurrentProfile.ItemOverrides.Remove(item.FormKey);
        else _settings.CurrentProfile.ItemOverrides[item.FormKey] = item.Selected;
        SaveSettings();
        InvalidatePreview("item inclusion changed");
    }

    internal void UseSpecializedRecommendations(IEnumerable<CandidateItem> rows)
    {
        _suppressItemEvents = true;
        try
        {
            foreach (var item in rows)
            {
                _settings.CurrentProfile.SpecializedLootOverrides.Remove(item.FormKey);
                item.HasSpecializedOverride = false;
                item.SpecializedMode = item.RecommendedSpecializedMode;
                ShowSelection(item);
            }
        }
        finally { _suppressItemEvents = false; }
        SaveSettings(); InvalidatePreview("specialized recommendations restored");
    }

    internal void UseMaterialFamilyRecommendations(IEnumerable<CandidateItem> rows)
    {
        var changed = false;
        _suppressItemEvents = true;
        try
        {
            foreach (var item in rows.Where(x => x.MaterialFamilyChoices.Count > 1))
            {
                changed |= _settings.CurrentProfile.MaterialFamilyOverrides.Remove(item.FormKey);
                item.HasMaterialFamilyOverride = false;
                item.MaterialFamily = item.RecommendedMaterialFamily;
            }
        }
        finally { _suppressItemEvents = false; }
        if (!changed) return;
        SaveSettings(); RequestScanRefresh("material family");
    }

    internal void EnchantmentChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_suppressEnchantmentEvents || sender is not EnchantmentSelectionOption enchantment
            || e.PropertyName != nameof(EnchantmentSelectionOption.IsSelected)) return;
        if (!enchantment.Available && enchantment.IsSelected)
        {
            _suppressEnchantmentEvents = true;
            enchantment.IsSelected = false;
            _suppressEnchantmentEvents = false;
            return;
        }
        ApplyEnchantmentFamily(enchantment, enchantment.IsSelected);
        SaveSettings();
        RequestScanRefresh("enchantment selection");
    }

    // A row is an enchantment family: the choice applies to every tier. Overrides
    // stay per tier so the planner and saved profiles keep their existing format.
    private void ApplyEnchantmentFamily(EnchantmentSelectionOption enchantment, bool? selected)
    {
        var overrides = _settings.CurrentProfile.EnchantmentOverrides;
        foreach (var tier in enchantment.Tiers)
        {
            if (selected is null || !tier.Available || tier.AuthorDefault && selected == tier.DefaultSelected) overrides.Remove(tier.FormKey);
            else overrides[tier.FormKey] = selected.Value;
        }
        _suppressEnchantmentEvents = true;
        enchantment.HasManualOverride = enchantment.Tiers.Any(t => overrides.ContainsKey(t.FormKey));
        enchantment.SelectedTierCount = enchantment.Tiers.Count(t => t.Available && overrides.GetValueOrDefault(t.FormKey, t.DefaultSelected));
        enchantment.IsSelected = enchantment.SelectedTierCount > 0;
        _suppressEnchantmentEvents = false;
    }

    internal void SetEnchantments(IEnumerable<EnchantmentSelectionOption> rows, bool? selected)
    {
        foreach (var enchantment in rows) ApplyEnchantmentFamily(enchantment, selected);
        SaveSettings();
        RequestScanRefresh("enchantment selection");
    }

    // The engine's selection of the item under its plugin's rule, given the user's choice
    // (null: none). Every selected state the window shows or saves comes from here.
    private bool EngineSelection(CandidateItem item, bool? choice)
    {
        var policy = Plugins.FirstOrDefault(x => x.Plugin.Equals(item.Plugin, StringComparison.OrdinalIgnoreCase))?.Policy ?? PatchPolicy.Review;
        // Author inclusions are decided at Scan, where the effective profile hands them to the
        // engine as item choices; an exclusion made since then waits for the rescan.
        var author = item.AuthorInclude is { } include && !(include && policy == PatchPolicy.Exclude) ? include : (bool?)null;
        return PatcherEngine.Selection(item, policy, choice ?? author);
    }

    // The engine's selection when the user has made no choice for the item.
    internal bool DefaultSelected(CandidateItem item) => EngineSelection(item, null);

    // Shows the engine's selection with the user's saved choice on the row.
    private void ShowSelection(CandidateItem item)
    {
        var suppressed = _suppressItemEvents;
        _suppressItemEvents = true;
        item.Selected = EngineSelection(item, _settings.CurrentProfile.ItemOverrides.TryGetValue(item.FormKey, out var choice) ? choice : null);
        _suppressItemEvents = suppressed;
    }

    private void SaveDecisions()
    {
        foreach (var row in Plugins) StorePolicy(row);
        SaveSettings();
    }

    // The rules file's plugin policy is a default, not a saved choice.
    private void StorePolicy(PluginDecision row)
    {
        if (row.AuthorPolicy is { } author && (row.Policy == author || row.Policy == PatchPolicy.Review))
        {
            // Review is not a decision while the rules file supplies one; show it.
            _settings.CurrentProfile.Policies.Remove(row.Plugin);
            row.Policy = author;
        }
        else _settings.CurrentProfile.Policies[row.Plugin] = row.Policy;
    }

    // A rescan replaces the row objects; the review window keeps its place across it.
    internal event Action? RowsReplacing;
    internal event Action? RowsReplaced;
    internal void ReplaceRows(Action replace) { RowsReplacing?.Invoke(); replace(); RowsReplaced?.Invoke(); }

    // After a scan, asks about saved choices that differ from the rules file.
    private bool AskAuthorConflicts()
    {
        if (_snapshot is null || PatcherEngine.AuthorOverlayOf(_snapshot) is not { } overlay) return false;
        var conflicts = AuthorDefaults.Unanswered(_settings.CurrentProfile, overlay);
        if (conflicts.Count == 0) return false;
        var names = Items.GroupBy(i => i.FormKey, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First().Name, StringComparer.OrdinalIgnoreCase);
        foreach (var enchantment in Enchantments)
            foreach (var tier in enchantment.Tiers) names.TryAdd(tier.FormKey, $"{enchantment.Name} ({tier.EditorId})");
        var window = new AuthorConflictWindow(conflicts, key => names.TryGetValue(key, out var name) ? $"{name} [{key}]" : key) { Owner = this };
        if (window.ShowDialog() != true) return false;
        AuthorDefaults.Answer(_settings.CurrentProfile, window.Answers);
        // Plugin rows whose saved policy was removed now show the author's policy,
        // so a later SaveDecisions does not write the old value back.
        foreach (var row in Plugins.Where(r => r.AuthorPolicy is not null && !_settings.CurrentProfile.Policies.ContainsKey(r.Plugin)))
            row.Policy = row.AuthorPolicy!.Value;
        SaveSettings();
        return true;
    }

    internal void SetPluginPolicies(IEnumerable<PluginDecision> rows, PatchPolicy policy)
    {
        foreach (var row in rows) row.Policy = policy;
        SaveDecisions();
        InvalidatePreview("plugin distribution rules changed");
        var label = PolicyValues.All.FirstOrDefault(choice => choice.Value == policy)?.Label ?? policy.ToString();
        Status = $"Applied {label} to the selected plugin rows.";
    }

    private readonly SettingsSaveState _settingsSave = new();
    private string _shownSettingsError = "";
    public string SettingsSaveNotice => _settingsSave.Notice;
    public string SettingsPersistenceHint => _settingsSave.HasUnsavedChanges
        ? "Your choices are in memory but have not been saved. Keep this window open and retry after restoring write access."
        : "Controls will return when the scan finishes.";
    internal bool SaveSettings(bool notify = true)
    {
        var saved = _settingsSave.TrySave(_settings);
        Changed(nameof(SettingsSaveNotice)); Changed(nameof(SettingsPersistenceHint));
        if (SettingsSaveNotice.Length > 0)
        {
            Status = SettingsSaveNotice;
            if (notify && _shownSettingsError != SettingsSaveNotice)
            {
                _shownSettingsError = SettingsSaveNotice;
                WpfMessageBox.Show(this, SettingsSaveNotice, saved ? "Session metadata warning" : "Settings not saved",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        else _shownSettingsError = "";
        return saved;
    }

    private void ProviderChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_suppressProviderEvents || e.PropertyName != nameof(EnchantmentProviderOption.IsSelected)) return;
        SaveProviderOrder();
        SaveSettings();
        RequestScanRefresh("enchantment origins");
    }

    private void ProviderListMouseDown(object sender, MouseButtonEventArgs e)
    {
        _providerDragStart = e.GetPosition(ProviderList);
        _draggedProvider = FindAncestor<System.Windows.Controls.ListBoxItem>(e.OriginalSource as DependencyObject)?.DataContext as EnchantmentProviderOption;
    }

    private void ProviderListMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _draggedProvider is null) return;
        var current = e.GetPosition(ProviderList);
        if (Math.Abs(current.X - _providerDragStart.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(current.Y - _providerDragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        var dragged = _draggedProvider;
        _draggedProvider = null;
        System.Windows.DragDrop.DoDragDrop(ProviderList, dragged, System.Windows.DragDropEffects.Move);
    }

    private void ProviderListDragOver(object sender, System.Windows.DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(typeof(EnchantmentProviderOption)) ? System.Windows.DragDropEffects.Move : System.Windows.DragDropEffects.None;
        e.Handled = true;
    }

    private void ProviderListDrop(object sender, System.Windows.DragEventArgs e)
    {
        if (e.Data.GetData(typeof(EnchantmentProviderOption)) is not EnchantmentProviderOption dragged) return;
        var sourceIndex = Providers.IndexOf(dragged);
        if (sourceIndex < 0) return;
        var targetContainer = FindAncestor<System.Windows.Controls.ListBoxItem>(e.OriginalSource as DependencyObject);
        var targetIndex = targetContainer?.DataContext is EnchantmentProviderOption target
            ? Providers.IndexOf(target) + (e.GetPosition(targetContainer).Y > targetContainer.ActualHeight / 2 ? 1 : 0)
            : Providers.Count;
        if (sourceIndex < targetIndex) targetIndex--;
        targetIndex = Math.Clamp(targetIndex, 0, Providers.Count - 1);
        if (sourceIndex == targetIndex) return;
        Providers.Move(sourceIndex, targetIndex);
        ProviderList.SelectedItem = dragged;
        SaveProviderOrder();
        SaveSettings();
        // Origin priority decides overlap recommendations, so the review rows are out of date.
        RequestScanRefresh("enchantment-origin priority");
        e.Handled = true;
    }

    private static T? FindAncestor<T>(DependencyObject? node) where T : DependencyObject
    {
        while (node is not null && node is not T) node = VisualTreeHelper.GetParent(node);
        return node as T;
    }

    private void SaveProviderOrder()
    {
        _settings.CurrentProfile.UseLoadOrderProviderDefaults = false;
        _settings.CurrentProfile.EnchantmentProviderPlugins = Providers.Where(x => x.IsSelected).Select(x => x.Plugin)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        _settings.CurrentProfile.EnchantmentProviderOrder = Providers.Select(x => x.Plugin)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private bool IsPluginEnabledForReview(string plugin) => Plugins.Any(row =>
        row.Plugin.Equals(plugin, StringComparison.OrdinalIgnoreCase)
        && row.Policy == PatchPolicy.ExistingDistribution);

    internal bool FilterItem(object value)
    {
        if (value is not CandidateItem item || !IsPluginEnabledForReview(item.Plugin)) return false;
        // Already-enchanted records can never be patch sources; keep them out of the item list.
        if (item.AlreadyEnchanted) return false;
        if (string.IsNullOrWhiteSpace(SearchText)) return true;
        var text = SearchText.Trim();
        return item.Plugin.Contains(text, StringComparison.OrdinalIgnoreCase)
            || item.Name.Contains(text, StringComparison.OrdinalIgnoreCase)
            || item.EditorId.Contains(text, StringComparison.OrdinalIgnoreCase)
            || item.Category.Contains(text, StringComparison.OrdinalIgnoreCase)
            || item.FormKey.Contains(text, StringComparison.OrdinalIgnoreCase)
            || item.Reason.Contains(text, StringComparison.OrdinalIgnoreCase);
    }

    private bool FilterEnchantment(object value)
    {
        if (value is not EnchantmentSelectionOption enchantment || string.IsNullOrWhiteSpace(EnchantmentSearchText)) return true;
        var text = EnchantmentSearchText.Trim();
        return enchantment.Provider.Contains(text, StringComparison.OrdinalIgnoreCase)
            || enchantment.Name.Contains(text, StringComparison.OrdinalIgnoreCase)
            || enchantment.EditorId.Contains(text, StringComparison.OrdinalIgnoreCase)
            || enchantment.FormKey.Contains(text, StringComparison.OrdinalIgnoreCase)
            || enchantment.Effects.Contains(text, StringComparison.OrdinalIgnoreCase)
            || enchantment.Reason.Contains(text, StringComparison.OrdinalIgnoreCase);
    }

    private ReviewWindow? _reviewWindow;
    internal async void OpenReview(object sender, RoutedEventArgs e)
    {
        if (_reviewWindow is not null) { _reviewWindow.Activate(); return; }
        // Rows out of date open at once with the update button; only a first opening scans.
        if (_scan is null && !await FlushPendingScanAsync()) return;
        RegisterReviewWindow(new ReviewWindow(this) { Owner = this }).Show();
    }

    internal ReviewWindow RegisterReviewWindow(ReviewWindow window)
    {
        _reviewWindow = window;
        window.Closed += (_, _) => { if (ReferenceEquals(_reviewWindow, window)) _reviewWindow = null; };
        return window;
    }

    // Every choice invalidates the preview. The snapshot checks its own analysis
    // inputs at the next operation, preserving analysis for planning-only changes.
    private void InvalidatePreview(string reason = "settings or selections changed")
    {
        _previewCache.Invalidate(reason);
        foreach (var item in Items) item.ClearPlacementPreview();
        Changed(nameof(IsPreviewCurrent));
        Changed(nameof(PreviewStateText));
    }

    private void EnsureCurrentProviderOptions()
    {
        var selectedOrder = _settings.CurrentProfile.EnchantmentProviderPlugins.ToList();
        var selected = selectedOrder.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var existing = Providers.ToDictionary(x => x.Plugin, StringComparer.OrdinalIgnoreCase);
        var ordered = new List<EnchantmentProviderOption>();
        foreach (var name in _settings.CurrentProfile.EnchantmentProviderOrder.Concat(selectedOrder).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!existing.TryGetValue(name, out var option))
            {
                option = new EnchantmentProviderOption(name, 0, 0);
                option.PropertyChanged += ProviderChanged;
            }
            ordered.Add(option);
        }
        ordered.AddRange(Providers.Where(x => !selected.Contains(x.Plugin)));
        _suppressProviderEvents = true;
        Providers.Clear();
        foreach (var option in ordered)
        {
            option.IsSelected = selected.Contains(option.Plugin);
            Providers.Add(option);
        }
        _suppressProviderEvents = false;
    }

    private void RefreshProfileNames()
    {
        ProfileNames.Clear();
        foreach (var name in _settings.Profiles.Keys.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
            ProfileNames.Add(name);
    }

    // Returns false when the operation failed. The user sees its message when the session log
    // keeps the full exception, and all of it otherwise.
    internal async Task<bool> Busy(Func<Task> operation, ProgressKind kind = ProgressKind.Preview)
    {
        var review = _reviewWindow;
        var reviewWasEnabled = review?.IsEnabled == true;
        try
        {
            IsBusyVisual = true;
            IsEnabled = false;
            if (review is not null) review.IsEnabled = false;
            OperationProgressPercent = kind is ProgressKind.Scan or ProgressKind.Generation ? 0 : null;
            SaveDecisions();
            await operation();
            return true;
        }
        catch (Exception ex)
        {
            SessionRuntime.Log(ex.ToString());
            Status = "Failed: " + ex.Message.Split('\n')[0];
            WpfMessageBox.Show(SessionRuntime.ShownText(ex), Title, MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
        finally
        {
            IsEnabled = true;
            if (reviewWasEnabled && review is { IsLoaded: true }) review.IsEnabled = true;
            IsBusyVisual = false;
        }
    }

    private static string? EmptyToNull(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
}
