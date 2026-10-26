using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
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

public static class DistributionValues
{
    public static IReadOnlyList<DistributionTarget> All { get; } = Enum.GetValues<DistributionTarget>();
}

[Flags]
internal enum ScanRefreshScope
{
    None = 0,
    Scan = 1,
    DiscoverProviders = 2,
    Full = Scan | DiscoverProviders
}

internal sealed class DeferredScanState
{
    private long _revision;
    private long _completedRevision;
    private readonly List<(long Revision, string Reason, ScanRefreshScope Scope)> _changes = [];

    public bool IsDirty => _revision > _completedRevision;
    public long Revision => _revision;
    public string Reasons
    {
        get
        {
            var reasons = _changes.Where(x => x.Revision > _completedRevision).Select(x => x.Reason)
                .Where(x => x.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray();
            return reasons.Length == 0 ? "settings changed" : string.Join(", ", reasons);
        }
    }
    public ScanRefreshScope Scope => _changes.Where(x => x.Revision > _completedRevision)
        .Aggregate(ScanRefreshScope.None, (scope, change) => scope | change.Scope);

    public void Mark(string reason, ScanRefreshScope scope)
    {
        _revision++;
        _changes.Add((_revision, reason.Trim(), scope));
    }

    public void Complete(long revision)
    {
        _completedRevision = Math.Max(_completedRevision, revision);
        _changes.RemoveAll(x => x.Revision <= _completedRevision);
    }
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

    private readonly AppSettings _settings;
    private ScanResult? _scan;
    // Scan's loaded plugins and analysis, reused by Preview, Generate and settings-driven
    // rescans. A Data folder or profile change discards it; the session end releases it.
    private ScanSnapshot? _snapshot;
    private string _profileName;
    private string _status = "Loading the active load order. Launch through MO2 when using its virtual mod files.";
    private string _summary = "No scan yet";
    private string _searchText = "";
    private string _enchantmentSearchText = "";
    private string _previewDetails = "Choose Generate Patch after scanning to calculate output files, variant counts, and leveled-list growth before confirming generation.";
    private string _previewReportDetails = "Choose Generate Patch in the main window to calculate the full output report before confirming generation.";
    private string _rescanStatus = "Updating review data…";
    private double? _operationProgress;
    private bool _isRescanning;
    private bool _isBusyVisual;
    private string _busyTitle = "Scanning load order";
    private bool _suppressItemEvents;
    private bool _suppressProviderEvents;
    private bool _suppressEnchantmentEvents;
    private System.Windows.Point _providerDragStart;
    private EnchantmentProviderOption? _draggedProvider;
    private bool _startupComplete;
    private readonly DeferredScanState _deferredScan = new();
    private readonly PreviewCacheState _previewCache = new();
    private bool _scanRefreshInProgress;
    private readonly System.Windows.Threading.DispatcherTimer _refreshTimer = new()
        { Interval = TimeSpan.FromMilliseconds(600) };

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
        set
        {
            var previous = _settings.DataFolder;
            _settings.DataFolder = EmptyToNull(value);
            if (_settings.DataFolder != previous) DiscardSnapshot();
            Changed(); RequestScanRefresh("Data folder", ScanRefreshScope.Full);
        }
    }
    public string? OutputFolder { get => _settings.OutputFolder; set { _settings.OutputFolder = EmptyToNull(value); Changed(); InvalidatePreview("output folder changed"); } }
    public DistributionTarget DistributionTarget
    {
        get => _settings.CurrentProfile.DistributionTarget;
        set { _settings.CurrentProfile.DistributionTarget = value; Changed(); SaveSettings(); InvalidatePreview("distribution target changed"); }
    }
    public int WarningListEntries
    {
        get => _settings.CurrentProfile.WarningListEntries;
        set { _settings.CurrentProfile.WarningListEntries = Math.Clamp(value, 1, 255); Changed(); SaveSettings(); InvalidatePreview("list-size warning threshold changed"); }
    }
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
    // The layout is picked at generation, so previews show both outcomes.
    private static string OutputChoices(GenerationPreview preview)
    {
        var light = OutputLayout.Light.PatchCount(preview.NewRecords);
        return $"{light:N0} light ESP plugin{(light == 1 ? "" : "s")} or 1 non-light plugin ({preview.NewRecords:N0} new records)";
    }
    public string SearchText { get => _searchText; set { _searchText = value ?? ""; Changed(); ItemsView.Refresh(); } }
    public string EnchantmentSearchText { get => _enchantmentSearchText; set { _enchantmentSearchText = value ?? ""; Changed(); EnchantmentsView.Refresh(); } }
    public string Status { get => _status; set { _status = value; SessionRuntime.Log(value); Changed(); } }
    public string Summary { get => _summary; set { _summary = value; Changed(); } }
    public string PreviewDetails { get => _previewDetails; set { _previewDetails = value; Changed(); } }
    public string PreviewReportDetails { get => _previewReportDetails; private set { _previewReportDetails = value; Changed(); } }
    public bool IsPreviewCurrent => _previewCache.IsCurrent;
    public string PreviewStateText => _previewCache.StateText;
    public string RescanStatus { get => _rescanStatus; private set { _rescanStatus = value; Changed(); } }
    public bool IsRescanning { get => _isRescanning; private set { _isRescanning = value; Changed(); } }
    public bool IsBusyVisual { get => _isBusyVisual; private set { _isBusyVisual = value; Changed(); } }
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
        ContentRendered += (_, _) =>
        {
            if (!_authorPromptPending) return;
            _authorPromptPending = false;
            if (AskAuthorConflicts()) RequestScanRefresh("mod-author rules answers");
        };
        UiTheme.Apply(settings.DarkTheme);
        UiTheme.Attach(this);
        _profileName = _settings.ProfileName;
        RefreshProfileNames();
        PluginsView = new ListCollectionView(Plugins)
        {
            Filter = row => row is PluginDecision { EligibleItems: > 0, HasDistributionCandidates: true }
        };
        SpecializedItemsView = CollectionViewSource.GetDefaultView(SpecializedItems);
        SpecializedItemsView.Filter = o => o is CandidateItem c && c.IsPlayable && c.HasSpecializedEquipmentKind && IsPluginEnabledForSpecializedReview(c.Plugin) &&
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
        _refreshTimer.Tick += async (_, _) =>
        {
            _refreshTimer.Stop();
            await FlushPendingScanAsync("automatic refresh");
        };
        Closing += (_, e) =>
        {
            if (!_settingsSave.HasUnsavedChanges || SaveSettings(notify: false)) return;
            e.Cancel = WpfMessageBox.Show(this, SettingsSaveNotice + "\n\nClose and discard the unsaved choices in this window?",
                "Unsaved settings", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes;
        };
        Closed += (_, _) =>
        {
            _refreshTimer.Stop();
            DiscardSnapshot();
            try { SessionRuntime.Finish(_settingsSave.HasUnsavedChanges ? 1 : 0); }
            catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
            {
                SessionRuntime.ExitCode = 1;
                WpfMessageBox.Show("The window closed, but session completion could not be recorded. " + ex.Message,
                    "Session metadata error", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        };
    }

    private ScanSnapshot Snapshot => _snapshot ?? throw new InvalidOperationException("No scan data is loaded. Wait for the scan to finish.");

    internal async Task InitializeAsync()
    {
        if (_startupComplete) return;
        _startupComplete = true;
        await RefreshLoadOrder();
    }

    private void ToggleTheme(object sender, RoutedEventArgs e)
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

    private void RequestScanRefresh(string reason, ScanRefreshScope scope = ScanRefreshScope.Scan)
    {
        SaveDecisions();
        _deferredScan.Mark(reason, scope);
        InvalidatePreview(reason + " changed");
        if (!_startupComplete) return;
        SchedulePendingScan();
    }

    private void SchedulePendingScan()
    {
        if (!_startupComplete || !_deferredScan.IsDirty) return;
        Status = $"Waiting to rescan because {_deferredScan.Reasons} changed…";
        _refreshTimer.Stop();
        _refreshTimer.Start();
    }

    internal bool HasPendingScanChanges => _deferredScan.IsDirty;

    internal async Task<bool> FlushPendingScanAsync(string boundary)
    {
        if (!_deferredScan.IsDirty) return _scan is not null;
        if (!_startupComplete) return false;
        _refreshTimer.Stop();
        RescanStatus = $"Rescanning because {_deferredScan.Reasons} changed…";
        Status = $"Rescanning because {_deferredScan.Reasons} changed ({boundary})…";
        await RefreshLoadOrder();
        return !_deferredScan.IsDirty && _scan is not null;
    }

    private async Task RefreshLoadOrder()
    {
        if (_scanRefreshInProgress) return;
        _scanRefreshInProgress = true;
        var scanRevision = _deferredScan.Revision;
        var refreshScope = _deferredScan.Scope;
        var showRescan = _deferredScan.IsDirty;
        if (showRescan) IsRescanning = true;
        BusyTitle = _scan is null ? "Scanning load order" : "Rescanning patch data";
        try
        {
            await Busy(async () =>
            {
                if (!Directory.Exists(DataFolder))
                {
                    Status = "Choose a valid Data folder to discover origins and scan automatically.";
                    return;
                }
                var selectedOrder = _settings.CurrentProfile.EnchantmentProviderPlugins.ToList();
                var selectedNames = selectedOrder.ToHashSet(StringComparer.OrdinalIgnoreCase);
                if (_scan is null || refreshScope.HasFlag(ScanRefreshScope.DiscoverProviders))
                {
                    RescanStatus = "Discovering enchantment origins and usable templates…";
                    Status = RescanStatus;
                    var discovered = await Task.Run(() => PatcherEngine.DiscoverProviders(_settings));
                    selectedOrder = _settings.CurrentProfile.EnchantmentProviderPlugins.ToList();
                    selectedNames = selectedOrder.ToHashSet(StringComparer.OrdinalIgnoreCase);
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
                    if (selectedNames.Count == 0 || selectedNames.Any(name => !discovered.Any(x => x.Plugin.Equals(name, StringComparison.OrdinalIgnoreCase))))
                    {
                        Status = discovered.Count == 0
                            ? "No usable enchantment origins are available in the active load order."
                            : "Select available enchantment origins to scan automatically. Deselect any unavailable saved origins.";
                        return;
                    }
                }
                else if (selectedNames.Count == 0)
                {
                    Status = "Select available enchantment origins to scan automatically. Deselect any unavailable saved origins.";
                    return;
                }
                RescanStatus = "Scanning equipment, enchantments, availability and leveled-list routes…";
                Status = RescanStatus;
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
                UpdateSummary();
                if (AskAuthorConflicts()) RequestScanRefresh("mod-author rules answers");
                Status = scan.Warnings.Count == 0 ? "Scan complete. Review plugins or individual items, then choose Generate Patch."
                    : $"Scan complete with {scan.Warnings.Count} load-order warning(s). Review plugins or individual items, then choose Generate Patch.";
                if (scan.ScriptAnalysisWarning is not null)
                    Status += " Script analysis is unavailable; unresolved script contexts remain protected.";
            }, kind: ProgressKind.Scan);
        }
        finally
        {
            _scanRefreshInProgress = false;
            if (showRescan) IsRescanning = false;
            // A setting can change while a scan is still running. Its timer may
            // fire against the in-progress guard, so re-arm the debounce after
            // this scan completes rather than leaving the newer revision stale.
            if (_deferredScan.Revision > scanRevision) SchedulePendingScan();
        }
    }

    private async void Generate(object sender, RoutedEventArgs e)
    {
        if (!await FlushPendingScanAsync("generation requested")) { WpfMessageBox.Show("Wait for the automatic scan to finish. Check the Data folder and selected origins if it has not started.", Title); return; }
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
            ShowPreview(preview);
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
            var report = await Task.Run(() => RunWithProgress(
                () => PatcherEngine.Generate(_settings, policies, snapshot, reusePreview),
                ProgressKind.Generation, initialFraction: initialFraction));
            SaveSettings();
            Status = $"Complete: {report.Variants} new, {report.ReusedVariants} reused variants; {report.UndistributedVariants} undistributed; {report.LeveledListsOverridden} lists updated.";
            // Writing output changes no scan input, so the snapshot stays valid.
            InvalidatePreview("generated output changed");
            var backup = report.BackupFolder is null ? "" : $"\nPrevious output backed up to:\n{report.BackupFolder}\n";
            WpfMessageBox.Show($"{report.Outputs.Count} {(_settings.CurrentProfile.FullOutputPlugin ? "full plugin(s)" : "ESPFE file(s)")} written and fully validated.\n\n{string.Join(Environment.NewLine, report.Outputs)}\n{backup}\nVariants: {report.Variants}\nSource items: {report.SourceItems}\nLeveled lists: {report.LeveledListsOverridden}", Title);
            generated = true;
        }, kind: ProgressKind.Generation);
        if (generated) Close();
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

    private Dictionary<string, PatchPolicy> CurrentPolicies() =>
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

    public bool CanDeleteProfile => _settings.Profiles.Count > 1;

    private bool ProfileActionsBlocked => IsBusyVisual || IsRescanning || _scanRefreshInProgress;

    private void ManageProfile(object sender, RoutedEventArgs e)
    {
        if (ProfileActionsBlocked) return;
        var action = Enum.Parse<ProfileAction>((string)((System.Windows.Controls.Button)sender).Tag);
        if (action == ProfileAction.Delete && !CanDeleteProfile) return;
        var dialog = new ProfileActionDialog(action, _settings.ProfileName, action == ProfileAction.Delete ? null
            : name => SettingsStore.ValidateProfileName(_settings, name, rename: action == ProfileAction.Rename)) { Owner = this };
        if (dialog.ShowDialog() == true) ApplyProfileAction(action, dialog.AcceptedName);
    }

    internal bool ApplyProfileAction(ProfileAction action, string name = "")
    {
        if (ProfileActionsBlocked) return false;
        switch (action)
        {
            case ProfileAction.New: SettingsStore.NewProfile(_settings, name); break;
            case ProfileAction.Duplicate: SettingsStore.DuplicateProfile(_settings, name); break;
            case ProfileAction.Rename: SettingsStore.RenameProfile(_settings, name); break;
            case ProfileAction.Delete: SettingsStore.DeleteProfile(_settings); break;
        }
        RefreshSelectedProfile();
        return true;
    }

    private void RefreshSelectedProfile()
    {
        RefreshProfileNames();
        _profileName = _settings.ProfileName;
        Changed(nameof(ProfileName));
        Changed(nameof(CanDeleteProfile));
        EnsureCurrentProviderOptions();
        Changed(nameof(DistributionTarget));
        Changed(nameof(AllowOtherMaterialRoutes));
        Changed(nameof(BroadUnmatchedDistribution));
        Changed(nameof(ResolveLeveledListConflicts));
        Changed(nameof(KeepUndistributedVariants));
        Changed(nameof(WarningListEntries));
        _scan = null;
        DiscardSnapshot();
        Plugins.Clear();
        Items.Clear();
        SpecializedItems.Clear();
        Enchantments.Clear();
        Summary = $"Profile '{ProfileName}' loaded with origins {ProviderSummary()}.";
        SaveSettings();
        RequestScanRefresh("profile", ScanRefreshScope.Full);
    }

    internal void SetSelectedPlugins(IEnumerable<PluginDecision> rows, PatchPolicy policy)
    {
        foreach (var row in rows) row.Policy = policy;
        SaveDecisions();
    }

    internal void SetSelectedItems(IEnumerable<CandidateItem> rows, bool selected)
    {
        foreach (var item in rows)
        {
            if (!item.Eligible && selected) continue;
            if (selected == DefaultSelected(item)) _settings.CurrentProfile.ItemOverrides.Remove(item.FormKey);
            else _settings.CurrentProfile.ItemOverrides[item.FormKey] = selected;
            _suppressItemEvents = true;
            item.Selected = selected;
            _suppressItemEvents = false;
        }
        SaveSettings();
        UpdateSummary();
        InvalidatePreview("item inclusion changed");
    }

    internal void ClearItemOverrides(IEnumerable<CandidateItem> rows)
    {
        _suppressItemEvents = true;
        foreach (var item in rows)
        {
            _settings.CurrentProfile.ItemOverrides.Remove(item.FormKey);
            item.Selected = DefaultSelected(item);
        }
        _suppressItemEvents = false;
        SaveSettings();
        UpdateSummary();
        InvalidatePreview("item inclusion reset");
    }

    private void PluginChanged(object? sender, PropertyChangedEventArgs e)
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
        UpdateSummary();
        InvalidatePreview("plugin distribution rule changed");
    }

    private void ItemChanged(object? sender, PropertyChangedEventArgs e)
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
            SaveSettings(); InvalidatePreview("specialized distribution choice changed"); return;
        }
        if (e.PropertyName != nameof(CandidateItem.Selected)) return;
        if (!item.Eligible && item.Selected)
        {
            _suppressItemEvents = true;
            item.Selected = false;
            _suppressItemEvents = false;
            return;
        }
        if (item.Selected == DefaultSelected(item)) _settings.CurrentProfile.ItemOverrides.Remove(item.FormKey);
        else _settings.CurrentProfile.ItemOverrides[item.FormKey] = item.Selected;
        SaveSettings();
        UpdateSummary();
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

    private void EnchantmentChanged(object? sender, PropertyChangedEventArgs e)
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

    private bool DefaultSelected(CandidateItem item)
    {
        if (!item.Eligible) return false;
        var policy = Plugins.FirstOrDefault(x => x.Plugin.Equals(item.Plugin, StringComparison.OrdinalIgnoreCase))?.Policy ?? PatchPolicy.Review;
        // Author inclusions are decided at Scan; an exclusion made since then waits for the rescan.
        if (item.AuthorInclude is { } include && !(include && policy == PatchPolicy.Exclude))
            return include && (item.AlreadyDistributed || item.SpecializedMode == SpecializedLootMode.ScriptDistribution);
        return policy switch
        {
            PatchPolicy.ExistingDistribution => item.AlreadyDistributed,
            _ => false
        };
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

    // After a scan, asks about saved choices that differ from the rules file. The first
    // scan runs before this window is shown; the prompt then waits until it appears.
    private bool _authorPromptPending;
    private bool AskAuthorConflicts()
    {
        if (!IsVisible) { _authorPromptPending = true; return false; }
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
        UpdateSummary();
        // Origin priority decides overlap recommendations; refresh the review rows now
        // (a reselection on the loaded analysis) rather than only at the next preview.
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
        _settings.CurrentProfile.ProviderOrderMigrated = true;
        _settings.CurrentProfile.LegacyDistributionProviderPlugins = null;
    }

    private string ProviderSummary() => _settings.CurrentProfile.EnchantmentProviderPlugins.Count == 0
        ? "none" : string.Join(", ", _settings.CurrentProfile.EnchantmentProviderPlugins);

    private void UpdateSummary()
    {
        if (_scan is null) return;
        Summary = $"{_scan.ActivePluginCount} active · origins {ProviderSummary()} · {Enchantments.Count(x => x.IsSelected)}/{Enchantments.Count} enchantments · {_scan.Plugins.Count} equipment plugins · {Items.Count(x => x.Eligible)} eligible · {Items.Count(x => x.Selected)} selected";
    }

    private bool IsPluginEnabledForReview(string plugin) => Plugins.Any(row =>
        row.Plugin.Equals(plugin, StringComparison.OrdinalIgnoreCase)
        && row.Policy == PatchPolicy.ExistingDistribution);

    private bool IsPluginEnabledForSpecializedReview(string plugin) => Plugins.Any(row =>
        row.Plugin.Equals(plugin, StringComparison.OrdinalIgnoreCase)
        && row.Policy == PatchPolicy.ExistingDistribution);

    private bool FilterItem(object value)
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
    private async void OpenReview(object sender, RoutedEventArgs e)
    {
        if (_reviewWindow is not null) { _reviewWindow.Activate(); return; }
        if (!await FlushPendingScanAsync("Patch Review opened"))
        {
            WpfMessageBox.Show("Wait for the automatic scan to finish. Check the Data folder and selected origins if it has not started.", Title);
            return;
        }
        RegisterReviewWindow(new ReviewWindow(this) { Owner = this }).Show();
    }

    internal ReviewWindow RegisterReviewWindow(ReviewWindow window)
    {
        _reviewWindow = window;
        window.Closed += async (_, _) =>
        {
            if (ReferenceEquals(_reviewWindow, window)) _reviewWindow = null;
            await FlushPendingScanAsync("Patch Review closed");
        };
        return window;
    }

    private void ShowPreview(GenerationPreview preview)
    {
        PreviewDetails = FormatPreview(preview);
        PreviewReportDetails = FormatPreviewReport(preview);
    }

    private static string FormatPreview(GenerationPreview preview)
    {
        static string FirstNames(IEnumerable<string> values, int limit)
        {
            var names = values.ToArray();
            return string.Join(", ", names.Take(limit)) + (names.Length > limit ? $" (+{names.Length - limit} more)" : "");
        }
        var text = new StringBuilder();
        text.AppendLine($"Output: {OutputChoices(preview)}; chosen when you generate");
        text.AppendLine($"Equipment: {preview.SelectedSourceItems:N0} selected; {preview.SkippedSelectedItems:N0} skipped.");
        text.AppendLine($"Variants: {preview.Variants:N0} new records, {preview.ReusedVariants:N0} reused.");
        text.AppendLine($"Undistributed combinations: {preview.UndistributedVariants:N0}; new records without loot routes are {(preview.KeepUndistributedVariants ? "retained and remain absent from loot" : "omitted")}.");
        text.AppendLine($"Leveled lists: {preview.TargetLists:N0} affected; {preview.GeneratedSublists:N0} new sublists.");
        text.AppendLine($"Enchantments: {preview.EnabledEnchantments:N0} enabled. Origin priority: {FirstNames(preview.EnchantmentProviderPlugins, 4)}.");
        if (preview.ResolveLeveledListConflicts)
            text.AppendLine($"Conflict resolution: {preview.ConflictRestoredEntries:N0} entries restored in {preview.ConflictResolvedLists:N0} lists.");
        if (preview.IdentityChanged) text.AppendLine("Record identities changed since the previous output; generation requires confirmation.");
        if (preview.Warnings.Count > 0)
        {
            text.AppendLine().AppendLine($"Warnings ({preview.Warnings.Count:N0}):");
            foreach (var warning in preview.Warnings.Take(5))
            {
                var line = warning.Replace('\r', ' ').Replace('\n', ' ');
                text.AppendLine("• " + (line.Length > 240 ? line[..240] + "…" : line));
            }
            if (preview.Warnings.Count > 5) text.AppendLine($"{preview.Warnings.Count - 5:N0} more warnings in the full report.");
        }
        if (preview.Lists.Count > 0)
        {
            text.AppendLine().AppendLine("Largest resulting lists:");
            foreach (var list in preview.Lists.OrderByDescending(x => x.After).Take(5))
                text.AppendLine($"{list.EditorId}: {list.Before:N0} + {list.Added:N0} = {list.After:N0}{(list.GeneratedSublists > 0 ? $"; split into {list.GeneratedSublists:N0} sublists" : "")}{(list.OverWarningLimit ? " (size warning)" : "")}");
        }
        text.AppendLine().Append("Expand the full report for all warnings, list changes and per-item destinations.");
        return text.ToString();
    }

    private static string FormatPreviewReport(GenerationPreview preview)
    {
        var text = new StringBuilder();
        text.AppendLine($"Origins / priority   : {string.Join(" → ", preview.EnchantmentProviderPlugins)}");
        text.AppendLine($"Enabled enchantments : {preview.EnabledEnchantments}");
        text.AppendLine($"Strict subtype matching: {(preview.StrictSubtypeMatching ? "enabled" : "disabled")}");
        text.AppendLine($"Broad unmatched fallback: {(preview.BroadUnmatchedDistribution ? "enabled" : "disabled")}");
        text.AppendLine($"LVLI conflict resolution: {(preview.ResolveLeveledListConflicts ? "enabled" : "disabled")} ({preview.ConflictResolvedLists} lists, {preview.ConflictRestoredEntries} restored entries)");
        text.AppendLine($"Keep undistributed variants: {(preview.KeepUndistributedVariants ? "enabled" : "disabled")}");
        text.AppendLine($"Selected source items : {preview.SelectedSourceItems}");
        text.AppendLine($"Generated variants    : {preview.Variants}");
        text.AppendLine($"Reused variants       : {preview.ReusedVariants}");
        text.AppendLine($"Undistributed variants: {preview.UndistributedVariants}");
        text.AppendLine($"Generated sublists    : {preview.GeneratedSublists}");
        text.AppendLine($"Source-item pools    : {preview.FallbackSublists}");
        text.AppendLine($"Output choices        : {OutputChoices(preview)}");
        text.AppendLine($"Target leveled lists  : {preview.TargetLists}");
        text.AppendLine($"Output names          : {string.Join(", ", preview.OutputNames)}");
        text.AppendLine($"Identity fingerprint  : {preview.Fingerprint}");
        text.AppendLine($"Changed since output  : {(preview.IdentityChanged ? "YES" : "no")}");
        if (preview.SkippedSelectedItems > 0) text.AppendLine($"Selected but skipped: {preview.SkippedSelectedItems}");
        if (preview.FallbackConnections.Count > 0)
        {
            text.AppendLine().AppendLine("Source-item pool connections:");
            foreach (var connection in preview.FallbackConnections) text.AppendLine("  " + connection);
        }
        if (preview.Warnings.Count > 0)
        {
            text.AppendLine().AppendLine("Warnings:");
            foreach (var warning in preview.Warnings) text.AppendLine("  " + warning);
        }
        if (preview.Information.Count > 0)
        {
            text.AppendLine().AppendLine("Information:");
            foreach (var information in preview.Information) text.AppendLine("  " + information);
        }
        text.AppendLine().AppendLine("Leveled-list impact (largest first):");
        foreach (var list in preview.Lists.OrderByDescending(x => x.After))
            text.AppendLine($"  {list.Before,3} + {list.Added,3} = {list.After,3}  {list.EditorId}  [{list.FormKey}]{(list.GeneratedSublists > 0 ? $" → {list.EntriesAfterSplitting} parent entries, {list.GeneratedSublists} sublists" : "")}{(list.OverWarningLimit ? "  WARNING" : "")}");
        text.AppendLine().AppendLine("Per-item archetypes and permitted destinations:");
        foreach (var item in preview.Placements)
        {
            text.AppendLine($"{item.Name} [{item.Source}] — {item.Family}; {item.Variants} variants");
            text.AppendLine($"  Enchantment archetype: {item.Archetype}");
            foreach (var destination in item.Destinations) text.AppendLine("  Destination: " + destination);
        }
        return text.ToString();
    }

    // Every choice invalidates the preview. The snapshot checks its own analysis
    // inputs at the next operation, preserving analysis for planning-only changes.
    private void InvalidatePreview(string reason = "settings or selections changed")
    {
        _previewCache.Invalidate(reason);
        foreach (var item in Items) item.ClearPlacementPreview();
        Changed(nameof(IsPreviewCurrent));
        Changed(nameof(PreviewStateText));
        if (_previewCache.Preview is null && _scan is not null)
            PreviewDetails = "Choose Generate Patch to calculate output files, variant counts, and leveled-list growth before confirming generation.";
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

    private async Task Busy(Func<Task> operation, ProgressKind kind = ProgressKind.Preview)
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
        }
        catch (Exception ex)
        {
            SessionRuntime.Log(ex.ToString());
            Status = "Failed: " + ex.Message;
            WpfMessageBox.Show(ex.ToString(), Title, MessageBoxButton.OK, MessageBoxImage.Error);
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
