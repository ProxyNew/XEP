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

    private static int RunCore(string[] args, CliRunOptions options)
    {
        try
        {
            var settings = SettingsStore.Load();
            var scanOnly = false;
            var previewOnly = false;
            var listProviders = false;
            var providerOverrides = new List<string>();
            var distributionProviderOverrides = new List<string>();
            var selections = new Dictionary<string, PatchPolicy>(StringComparer.OrdinalIgnoreCase);
            var itemSelections = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            string? scanJson = null;
            string? merchantPreflight = null;
            string? cdfInputData = null;
            for (var i = 0; i < args.Length; i++)
            {
                string Value() => ++i < args.Length ? args[i] : throw new ArgumentException($"Missing value for {args[i - 1]}.");
                switch (args[i])
                {
                    case "--data": settings.DataFolder = Path.GetFullPath(Value()); break;
                    case "--cdf-input-data": cdfInputData = Path.GetFullPath(Value()); break;
                    case "--output-folder": settings.OutputFolder = Path.GetFullPath(Value()); break;
                    case "--provider": providerOverrides.Add(Value()); break;
                    case "--distribution-provider": distributionProviderOverrides.Add(Value()); break;
                    case "--clear-distribution-providers": Console.WriteLine("Legacy --clear-distribution-providers ignored: ordered --provider arguments now set priority."); break;
                    case "--thaumaturgy": providerOverrides.Add(Value()); break;
                    case "--providers": listProviders = true; break;
                    case "--profile": SettingsStore.SelectProfile(settings, Value()); break;
                    case "--distribution":
                        _ = Value();
                        Console.WriteLine("Legacy --distribution ignored: distribution now follows source equipment.");
                        break;
                    case "--general-root": _ = Value(); Console.WriteLine("Legacy --general-root ignored: enchanted routes are discovered automatically."); break;
                    case "--clear-general-roots": Console.WriteLine("Legacy --clear-general-roots ignored: manual roots have been retired."); break;
                    case "--warning-list-size": settings.CurrentProfile.WarningListEntries = Math.Clamp(int.Parse(Value()), 1, 255); break;
                    case "--scan": scanOnly = true; break;
                    case "--preview": previewOnly = true; break;
                    case "--no-save": break;
                    case "--compare-authorisation": PatcherEngine.EnableAuthorisationComparison(); break;
                    case "--log": _ = Value(); break;
                    case "--specialized-enchanted":
                        settings.CurrentProfile.SpecializedLootOverrides[Mutagen.Bethesda.Plugins.FormKey.Factory(Value()).ToString()] = SpecializedLootMode.SpecializedEnchantedLists;
                        break;
                    case "--material-expansion": settings.CurrentProfile.AllowOtherMaterialRoutes = bool.Parse(Value()); break;
                    case "--broad-unmatched": settings.CurrentProfile.BroadUnmatchedDistribution = bool.Parse(Value()); break;
                    case "--resolve-leveled-list-conflicts": settings.CurrentProfile.ResolveLeveledListConflicts = bool.Parse(Value()); break;
                    case "--keep-undistributed": settings.CurrentProfile.KeepUndistributedVariants = bool.Parse(Value()); break;
                    case "--full-output-plugin": settings.CurrentProfile.FullOutputPlugin = bool.Parse(Value()); break;
                    case "--scan-json": scanJson = Path.GetFullPath(Value()); break;
                    case "--merchant-source-preflight": merchantPreflight = Path.GetFullPath(Value()); break;
                    case "--existing": selections[Value()] = PatchPolicy.ExistingDistribution; break;
                    case "--create-distribution": throw new ArgumentException("Create Distribution has been retired. Use --existing for equipment with an existing source route.");
                    case "--all": selections[Value()] = PatchPolicy.ExistingDistribution; Console.WriteLine("Legacy --all now uses ExistingDistribution; unrestricted plugin selection has been retired."); break;
                    case "--general": selections[Value()] = PatchPolicy.ExistingDistribution; Console.WriteLine("Legacy --general now uses ExistingDistribution; unrestricted plugin selection has been retired."); break;
                    case "--exclude": selections[Value()] = PatchPolicy.Exclude; break;
                    case "--include-item": itemSelections[Value()] = true; break;
                    case "--exclude-item": itemSelections[Value()] = false; break;
                    case "--help": PrintHelp(); return 0;
                    default: throw new ArgumentException($"Unknown option {args[i]}.");
                }
            }

            if (cdfInputData is not null)
            {
                var sessionRoot = SessionRuntime.DirectoryPath is { } directory ? Path.GetFullPath(directory) + Path.DirectorySeparatorChar : null;
                if (!options.NoSave || (!scanOnly && !previewOnly) || sessionRoot is null ||
                    !Path.GetFullPath(SettingsStore.PathName).StartsWith(sessionRoot, StringComparison.OrdinalIgnoreCase) ||
                    settings.OutputFolder is null || !Path.GetFullPath(settings.OutputFolder).StartsWith(sessionRoot, StringComparison.OrdinalIgnoreCase))
                    throw new ArgumentException("--cdf-input-data requires an isolated session with private settings/output, --no-save and --scan or --preview.");
                if (!Directory.Exists(cdfInputData)) throw new DirectoryNotFoundException("CDF fixture Data directory does not exist: " + cdfInputData);
                Console.WriteLine("CDF input Data override (private validation): " + cdfInputData);
            }

            if (providerOverrides.Count > 0)
            {
                settings.CurrentProfile.UseLoadOrderProviderDefaults = false;
                settings.CurrentProfile.EnchantmentProviderPlugins = providerOverrides.Where(x => !string.IsNullOrWhiteSpace(x))
                    .Select(x => x.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                settings.CurrentProfile.EnchantmentProviderOrder = settings.CurrentProfile.EnchantmentProviderPlugins.ToList();
            }
            if (distributionProviderOverrides.Count > 0)
            {
                if (settings.CurrentProfile.UseLoadOrderProviderDefaults is not false) _ = PatcherEngine.DiscoverProviders(settings);
                settings.CurrentProfile.UseLoadOrderProviderDefaults = false;
                var legacyPriority = distributionProviderOverrides.Where(x => !string.IsNullOrWhiteSpace(x))
                    .Select(x => x.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                settings.CurrentProfile.EnchantmentProviderPlugins = legacyPriority
                    .Where(x => settings.CurrentProfile.EnchantmentProviderPlugins.Contains(x, StringComparer.OrdinalIgnoreCase))
                    .Concat(settings.CurrentProfile.EnchantmentProviderPlugins.Where(x => !legacyPriority.Contains(x, StringComparer.OrdinalIgnoreCase)))
                    .ToList();
                settings.CurrentProfile.EnchantmentProviderOrder = settings.CurrentProfile.EnchantmentProviderPlugins.ToList();
                Console.WriteLine("Legacy --distribution-provider migrated into the ordered origin list. Pass --provider arguments in highest-to-lowest order instead.");
            }
            if (listProviders)
            {
                var providers = PatcherEngine.DiscoverProviders(settings);
                if (providers.Count == 0) Console.WriteLine("No usable enchantment origins are available in the active load order.");
                foreach (var provider in providers)
                    Console.WriteLine($"{provider.Plugin}\tenchantments={provider.EnchantmentCount}\ttemplates={provider.TemplateCount}");
                return 0;
            }
            foreach (var choice in selections) settings.CurrentProfile.Policies[choice.Key] = choice.Value;
            foreach (var choice in itemSelections) settings.CurrentProfile.ItemOverrides[choice.Key] = choice.Value;
            SessionRuntime.Settings(SettingsStore.PathName, settings.OutputFolder);
            SessionRuntime.EffectiveSettings(settings);
            if (merchantPreflight is not null)
            {
                PatcherEngine.WriteMerchantSourcePreflight(settings, merchantPreflight);
                Console.WriteLine($"Read-only merchant source preflight: {merchantPreflight}");
                return 0;
            }
            // Preview and Generate reuse this Scan's loaded plugins and analysis.
            var scan = PatcherEngine.ScanWithCdfInputs(settings, out var snapshotHandle, cdfInputData);
            using var snapshot = snapshotHandle;
            if (cdfInputData is not null)
                File.WriteAllText(Path.Combine(SessionRuntime.DirectoryPath!, "cdf-inputs.json"), JsonSerializer.Serialize(scan.Cdf));
            foreach (var warning in scan.Warnings) Console.WriteLine("WARNING: " + warning);
            Console.WriteLine($"Enchantment origins: {string.Join(", ", settings.CurrentProfile.EnchantmentProviderPlugins)}; active plugins: {scan.ActivePluginCount}; equipment plugins: {scan.Plugins.Count}; eligible items: {scan.Items.Count(x => x.Eligible)}; selected items: {scan.Items.Count(x => x.Selected)}; templates: {scan.TemplateCount}");
            foreach (var p in scan.Plugins) Console.WriteLine($"{p.Plugin}\tdecision={p.Policy}\teligible={p.EligibleItems}\tdistributed={p.DistributedItems}\texcluded={p.ExcludedItems}");
            if (scanJson is not null) File.WriteAllText(scanJson, JsonSerializer.Serialize(scan, JsonContext.Default.ScanResult));
            if (!options.NoSave) SettingsStore.Save(settings);
            if (scanOnly) return 0;

            var policies = scan.Plugins.ToDictionary(x => x.Plugin, x => x.Policy, StringComparer.OrdinalIgnoreCase);
            var preview = PatcherEngine.Preview(settings, policies, snapshot);
            PrintPreview(preview);
            if (previewOnly) return 0;
            if (policies.Any(x => x.Value == PatchPolicy.Review))
                throw new InvalidOperationException("Every equipment plugin needs a saved decision before generation. Use --existing or --exclude.");
            // Same settings and policies as the Preview above, so its plans are current.
            var report = PatcherEngine.Generate(settings, policies, snapshot, reusePreview: true, Console.WriteLine);
            if (!options.NoSave) SettingsStore.Save(settings);
            Console.WriteLine($"Written: {string.Join(", ", report.Outputs)}; variants={report.Variants}; sourceItems={report.SourceItems}; lists={report.LeveledListsOverridden}");
            if (report.BackupFolder is not null) Console.WriteLine($"Previous output backup: {report.BackupFolder}");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
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
          --merchant-source-preflight PATH
                                     Write bounded source/runtime dependency diagnostics only
          --cdf-input-data PATH      Private scan/preview fixture Data root for CDF inputs only;
                                     requires --no-save and isolated session settings/output
          --no-save                  Leave saved settings untouched (options apply to this run only)
          --log PATH                 Write all output to PATH instead of the console
          --profile NAME             Select or create a saved settings profile
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
          --data PATH                Skyrim Special Edition Data folder
          --thaumaturgy PLUGIN       Legacy alias for --provider
        """);
}
