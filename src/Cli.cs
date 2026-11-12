using System.Globalization;
using System.Text.Json;
using System.IO;

namespace XEnchantmentPatcher;

public static class Cli
{
    public sealed record CliRunOptions(bool NoSave, string? LogPath);

    // Read before the main parse so logging covers everything the run prints.
    public static CliRunOptions ReadRunOptions(string[] args)
    {
        var i = Array.IndexOf(args, "--log");
        string? log = null;
        if (i >= 0) log = i + 1 < args.Length ? Path.GetFullPath(args[i + 1]) : throw new ArgumentException("Missing value for --log.");
        return new CliRunOptions(args.Contains("--no-save"), log);
    }

    // The app is a WinExe, so a launch through MO2 has no console: --log sends all output to a file ending in "EXIT <code>".
    public static int Run(string[] args)
    {
        CliRunOptions options;
        try { options = ReadRunOptions(args); }
        catch (Exception ex) { Console.Error.WriteLine(ex.Message); return 1; }
        if (options.LogPath is null) return RunCore(args, options);
        var (oldOut, oldError) = (Console.Out, Console.Error);
        Directory.CreateDirectory(Path.GetDirectoryName(options.LogPath)!);
        using var log = TextWriter.Synchronized(new StreamWriter(options.LogPath, false) { AutoFlush = true });
        Console.SetOut(log); Console.SetError(log);
        try
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            Console.WriteLine($"SESSION id={SessionRuntime.Id}; pid={Environment.ProcessId}; settings={SettingsStore.PathName}; build={typeof(Cli).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false).OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion}");
            Console.WriteLine($"RUNTIME framework={System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}; server_gc={System.Runtime.GCSettings.IsServerGC}; processors={Environment.ProcessorCount}");
            var code = RunCore(args, options);
            clock.Stop();
            using var process = System.Diagnostics.Process.GetCurrentProcess();
            Console.WriteLine(FormattableString.Invariant($"GC_METRICS elapsed_ms={clock.Elapsed.TotalMilliseconds:F3}; allocated_bytes={GC.GetTotalAllocatedBytes()}; pause_ms={GC.GetTotalPauseDuration().TotalMilliseconds:F3}; gen0={GC.CollectionCount(0)}; gen1={GC.CollectionCount(1)}; gen2={GC.CollectionCount(2)}; peak_working_set_bytes={process.PeakWorkingSet64}"));
            Console.WriteLine($"EXIT {code}");
            return code;
        }
        finally { Console.SetOut(oldOut); Console.SetError(oldError); }
    }

    // Run choices read from the command line. ApplyArguments has already applied
    // the folder and profile-setting options to the settings.
    internal sealed class RunArguments
    {
        public bool ScanOnly, PreviewOnly, ListProviders, Help;
        public string? ScanJson, CdfInputData;
        public readonly List<string> ProviderOverrides = [];
        public readonly Dictionary<string, PatchPolicy> Selections = new(StringComparer.OrdinalIgnoreCase);
        public readonly Dictionary<string, bool> ItemSelections = new(StringComparer.OrdinalIgnoreCase);
    }

    // --profile selects a saved profile before any profile setting applies, wherever
    // it appears. Stops at --help without reading the options after it.
    internal static RunArguments ApplyArguments(string[] args, AppSettings settings)
    {
        var run = new RunArguments();
        string? profileName = null;
        var profileChanges = new List<Action<ProfileSettings>>();
        // The value is read and checked now; the change waits for the selected profile.
        void Profile<T>(T value, Action<ProfileSettings, T> set) => profileChanges.Add(profile => set(profile, value));
        for (var i = 0; i < args.Length; i++)
        {
            string Value() => ++i < args.Length ? args[i] : throw new ArgumentException($"Missing value for {args[i - 1]}.");
            switch (args[i])
            {
                case "--data": settings.DataFolder = Path.GetFullPath(Value()); break;
                case "--cdf-input-data": run.CdfInputData = Path.GetFullPath(Value()); break;
                case "--output-folder": settings.OutputFolder = Path.GetFullPath(Value()); break;
                case "--provider": run.ProviderOverrides.Add(Value()); break;
                case "--providers": run.ListProviders = true; break;
                case "--profile": profileName = profileName is null ? Value() : throw new ArgumentException("--profile may be given only once."); break;
                case "--warning-list-size": Profile(WarningListSize(Value()), (p, size) => p.WarningListEntries = size); break;
                case "--scan": run.ScanOnly = true; break;
                case "--preview": run.PreviewOnly = true; break;
                case "--no-save": break;
                case "--log": _ = Value(); break;
                case "--specialized-enchanted":
                    Profile(Mutagen.Bethesda.Plugins.FormKey.Factory(Value()).ToString(),
                        (p, key) => p.SpecializedLootOverrides[key] = SpecializedLootMode.SpecializedEnchantedLists);
                    break;
                case "--material-expansion": Profile(bool.Parse(Value()), (p, on) => p.AllowOtherMaterialRoutes = on); break;
                case "--broad-unmatched": Profile(bool.Parse(Value()), (p, on) => p.BroadUnmatchedDistribution = on); break;
                case "--resolve-leveled-list-conflicts": Profile(bool.Parse(Value()), (p, on) => p.ResolveLeveledListConflicts = on); break;
                case "--keep-undistributed": Profile(bool.Parse(Value()), (p, on) => p.KeepUndistributedVariants = on); break;
                case "--full-output-plugin": Profile(bool.Parse(Value()), (p, on) => p.FullOutputPlugin = on); break;
                case "--scan-json": run.ScanJson = Path.GetFullPath(Value()); break;
                case "--existing": run.Selections[Value()] = PatchPolicy.ExistingDistribution; break;
                case "--exclude": run.Selections[Value()] = PatchPolicy.Exclude; break;
                case "--include-item": run.ItemSelections[Value()] = true; break;
                case "--exclude-item": run.ItemSelections[Value()] = false; break;
                case "--help": run.Help = true; return run;
                default: throw new ArgumentException($"Unknown option {args[i]}.");
            }
        }
        if (profileName is not null)
        {
            // Never create a profile here: a mistyped name would be saved as the new active profile.
            if (!settings.Profiles.ContainsKey(profileName.Trim()))
                throw new ArgumentException($"Unknown profile \"{profileName}\". Saved profiles: "
                    + string.Join(", ", settings.Profiles.Keys.Order(StringComparer.OrdinalIgnoreCase)) + ". Create profiles in the graphical interface.");
            SettingsStore.SelectExistingProfile(settings, profileName);
        }
        foreach (var change in profileChanges) change(settings.CurrentProfile);
        return run;
    }

    private static int WarningListSize(string text) => int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var size)
        ? Math.Clamp(size, 1, 255)
        : throw new ArgumentException($"--warning-list-size needs a whole number (values outside 1-255 are clamped), not \"{text}\".");

    private static int RunCore(string[] args, CliRunOptions options)
    {
        try
        {
            var settings = SettingsStore.Load();
            var run = ApplyArguments(args, settings);
            if (run.Help) { PrintHelp(); return 0; }
            var cdfInputData = run.CdfInputData;

            if (cdfInputData is not null)
            {
                var sessionRoot = SessionRuntime.DirectoryPath is { } directory ? Path.GetFullPath(directory) + Path.DirectorySeparatorChar : null;
                if (!options.NoSave || (!run.ScanOnly && !run.PreviewOnly) || sessionRoot is null ||
                    !Path.GetFullPath(SettingsStore.PathName).StartsWith(sessionRoot, StringComparison.OrdinalIgnoreCase) ||
                    settings.OutputFolder is null || !Path.GetFullPath(settings.OutputFolder).StartsWith(sessionRoot, StringComparison.OrdinalIgnoreCase))
                    throw new ArgumentException("--cdf-input-data requires an isolated session with private settings/output, --no-save and --scan or --preview.");
                if (!Directory.Exists(cdfInputData)) throw new DirectoryNotFoundException("CDF fixture Data directory does not exist: " + cdfInputData);
                Console.WriteLine("CDF input Data override (private validation): " + cdfInputData);
            }

            if (run.ProviderOverrides.Count > 0)
            {
                settings.CurrentProfile.UseLoadOrderProviderDefaults = false;
                settings.CurrentProfile.EnchantmentProviderPlugins = run.ProviderOverrides.Where(x => !string.IsNullOrWhiteSpace(x))
                    .Select(x => x.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                settings.CurrentProfile.EnchantmentProviderOrder = settings.CurrentProfile.EnchantmentProviderPlugins.ToList();
            }
            if (run.ListProviders)
            {
                var providers = PatcherEngine.DiscoverProviders(settings);
                if (providers.Count == 0) Console.WriteLine("No usable enchantment origins are available in the active load order.");
                foreach (var provider in providers)
                    Console.WriteLine($"{provider.Plugin}\tenchantments={provider.EnchantmentCount}\ttemplates={provider.TemplateCount}");
                return 0;
            }
            foreach (var choice in run.Selections) settings.CurrentProfile.Policies[choice.Key] = choice.Value;
            foreach (var choice in run.ItemSelections) settings.CurrentProfile.ItemOverrides[choice.Key] = choice.Value;
            SessionRuntime.Settings(SettingsStore.PathName, settings.OutputFolder);
            SessionRuntime.EffectiveSettings(settings);
            // Preview and Generate reuse this Scan's loaded plugins and analysis.
            var scan = PatcherEngine.ScanWithCdfInputs(settings, out var snapshotHandle, cdfInputData);
            using var snapshot = snapshotHandle;
            if (cdfInputData is not null)
                File.WriteAllText(Path.Combine(SessionRuntime.DirectoryPath!, "cdf-inputs.json"), JsonSerializer.Serialize(scan.Cdf));
            foreach (var warning in scan.Warnings) Console.WriteLine("WARNING: " + warning);
            Console.WriteLine($"Enchantment origins: {string.Join(", ", settings.CurrentProfile.EnchantmentProviderPlugins)}; active plugins: {scan.ActivePluginCount}; equipment plugins: {scan.Plugins.Count}; eligible items: {scan.Items.Count(x => x.Eligible)}; selected items: {scan.Items.Count(x => x.Selected)}; templates: {scan.TemplateCount}");
            foreach (var p in scan.Plugins) Console.WriteLine($"{p.Plugin}\tdecision={p.Policy}\teligible={p.EligibleItems}\tdistributed={p.DistributedItems}\texcluded={p.ExcludedItems}");
            if (run.ScanJson is not null) File.WriteAllText(run.ScanJson, JsonSerializer.Serialize(scan, JsonContext.Default.ScanResult));
            if (!options.NoSave) SettingsStore.Save(settings);
            if (run.ScanOnly) return 0;

            var policies = scan.Plugins.ToDictionary(x => x.Plugin, x => x.Policy, StringComparer.OrdinalIgnoreCase);
            var preview = PatcherEngine.Preview(settings, policies, snapshot);
            PrintPreview(preview);
            if (run.PreviewOnly) return 0;
            if (policies.Any(x => x.Value == PatchPolicy.Review))
                throw new InvalidOperationException("Every equipment plugin needs a saved decision before generation. Use --existing or --exclude.");
            // Same settings and policies as the Preview above, so its plans are current.
            var report = PatcherEngine.Generate(settings, policies, snapshot, reusePreview: true, Console.WriteLine);
            if (!options.NoSave) SettingsStore.Save(settings);
            Console.WriteLine($"Written: {string.Join(", ", report.Outputs)}; variants={report.Variants}; sourceItems={report.SourceItems}; lists={report.LeveledListsOverridden}");
            if (report.BackupFolder is not null) Console.WriteLine($"Previous output backup: {report.BackupFolder}");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex is DataFolderMismatchException or OutputInstallException or UnreadableRecordException ? ex.Message : ex.ToString()); return 1; }
    }

    private static void PrintPreview(GenerationPreview preview)
    {
        Console.WriteLine($"Preview: origins={string.Join(",", preview.EnchantmentProviderPlugins)}; sourceItems={preview.SelectedSourceItems}; variants={preview.Variants}; outputs={preview.OutputFiles}; lists={preview.TargetLists}; identityChanged={preview.IdentityChanged}");
        Console.WriteLine($"Origin and leveled-list priority: {string.Join(" -> ", preview.EnchantmentProviderPlugins)}");
        Console.WriteLine($"Output names: {string.Join(", ", preview.OutputNames)}");
        Console.WriteLine($"Generated sublists: {preview.GeneratedSublists} ({preview.FallbackSublists} source-item pools)");
        Console.WriteLine($"Reused variants: {preview.ReusedVariants}; undistributed combinations: {preview.UndistributedVariants}");
        Console.WriteLine($"LVLI conflict resolution: {(preview.ResolveLeveledListConflicts ? "enabled" : "disabled")}; lists={preview.ConflictResolvedLists}; restoredEntries={preview.ConflictRestoredEntries}");
        Console.WriteLine($"Keep undistributed variants: {(preview.KeepUndistributedVariants ? "enabled" : "disabled")}");
        Console.WriteLine($"Full (non-light) output plugin: {(preview.FullOutputPlugin ? "enabled" : "disabled")}");
        foreach (var connection in preview.FallbackConnections) Console.WriteLine("FALLBACK: " + connection);
        foreach (var item in preview.Placements)
            Console.WriteLine($"ITEM: {item.Name} [{item.Source}] family={item.Family}; archetype={item.Archetype}; variants={item.Variants}; destinations={string.Join("; ", item.Destinations)}");
        foreach (var warning in preview.Warnings) Console.WriteLine("WARNING: " + warning);
        foreach (var list in preview.Lists.OrderByDescending(x => x.After))
            Console.WriteLine($"LIST: {list.FormKey}\t{list.EditorId}\t{list.Before}+{list.Added}={list.After}{(list.OverWarningLimit ? "\tWARNING" : "")}");
    }

    private static void PrintHelp() => Console.WriteLine("""
        XEnchantmentPatcher
          Launch with no arguments for the graphical interface.
          Run through MO2 so Mutagen sees the virtual Data folder.

          --gui                      Open the graphical interface with session arguments
          --settings-file PATH       Read/save a separate settings file (stale writes rejected)
          --settings-snapshot PATH   Copy settings into a new isolated session; separate output
          --session-dir PATH         New session folder for settings, log and run.json identity
          --run-id ID                Optional identity for --session-dir

          --scan                     Scan only
          --preview                  Calculate exact output without writing plugins
          --material-expansion BOOL  Override specialized material expansion for this run
          --specialized-enchanted KEY Select specialized enchanted routing for one item
          --scan-json PATH           Write detailed scan JSON
          --cdf-input-data PATH      Private scan/preview fixture Data root for CDF inputs only;
                                     requires --no-save and isolated session settings/output
          --no-save                  Leave saved settings untouched (options apply to this run only)
          --log PATH                 Write all output to PATH instead of the console
          --profile NAME             Select a saved settings profile; give once, applies before other options
          --providers                List plugins that originate usable enchantments
          --provider PLUGIN          Enable an origin; repeat in highest-to-lowest priority order
          --output-folder PATH       Output folder; plugin name is fixed
          --existing PLUGIN          Generate only for source items already in an LVLI
          --exclude PLUGIN           Explicitly exclude plugin
          --include-item FORMKEY     Override one item on (for example 012345:Mod.esp)
          --exclude-item FORMKEY     Override one item off
          --warning-list-size 1-255  Warn when an output LVLI reaches this size
          --broad-unmatched BOOL     Enable source-gated broader matching for otherwise unmatched equipment
          --resolve-leveled-list-conflicts BOOL
                                     Merge unaware LVLI overwrites before analysis and output
          --keep-undistributed BOOL Keep generated variants that lack a safe LVLI destination
          --full-output-plugin BOOL  Write one regular (non-light) output plugin instead of the light split
          --data PATH                Skyrim Special Edition Data folder
        """);
}
