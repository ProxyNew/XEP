using Mutagen.Bethesda;
using Mutagen.Bethesda.Environments;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Order;
using Mutagen.Bethesda.Skyrim;
using System.IO;

namespace XEnchantmentPatcher;

public sealed class LoadedGame : IDisposable
{
    private readonly IGameEnvironment<ISkyrimMod, ISkyrimModGetter> _environment;
    public IReadOnlyList<ISkyrimModGetter> Mods { get; }
    public LoadedGame(IGameEnvironment<ISkyrimMod, ISkyrimModGetter> environment)
    {
        _environment = environment;
        var listed = environment.LoadOrder.ListedOrder.Where(x => x.Enabled).ToArray();
        var missing = listed.Where(x => x.Mod is null).Select(x => x.ModKey).ToArray();
        if (missing.Length > 0)
            throw new DataFolderMismatchException(
                $"XEnchantmentPatcher could not find {missing.Length} of the {listed.Length} plugins enabled in your load order.\n"
                + $"Missing: {string.Join(", ", missing.Take(3))}{(missing.Length > 3 ? $" and {missing.Length - 3} more" : "")}\n\n"
                + $"It looked in this Data folder:\n{environment.DataFolderPath}\n\n"
                + "Usually this is the wrong Data folder for your load order.\n\n"
                + "To fix it, close XEnchantmentPatcher and start it again from Mod Organizer 2 (MO2). "
                + "In the first window, choose the Data folder inside the game folder MO2 uses. "
                + "To find that folder, open MO2's folder menu and choose \"Open Game folder\". "
                + "For a Wabbajack list, it is usually the \"Stock Game\" folder.\n\n"
                + "If only one or two plugins are missing, enable or reinstall the mods that provide them, or disable those plugins.");
        Mods = listed.Select(x => x.Mod!).ToArray();
    }
    public void Dispose() => _environment.Dispose();
}

// The message tells the player the fix, so it is shown without a stack trace.
public sealed class DataFolderMismatchException(string message) : IOException(message);
public sealed class OutputInstallException(string message) : IOException(message);
public sealed class UnreadableRecordException(string message, Exception inner) : Exception(message, inner);

public static class LoadOrderProvider
{
    internal static readonly Dictionary<string, ModKey[]> sessionOrders = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object orderGate = new();

    public static LoadedGame Load(AppSettings settings, string? outputName = null)
    {
        using var timing = PhaseTiming.Measure("load-order");
        ScanProgress.Enter(ScanStage.LoadOrder);
        if (string.IsNullOrWhiteSpace(settings.DataFolder))
            throw new ArgumentException("Choose the Skyrim Special Edition Data folder.");
        var dataFolder = Path.GetFullPath(settings.DataFolder);
        if (!Directory.Exists(dataFolder))
            throw new DirectoryNotFoundException($"The Data folder was not found: {dataFolder}");
        var builder = GameEnvironment.Typical.Builder<ISkyrimMod, ISkyrimModGetter>(GameRelease.SkyrimSE);
        builder = builder.WithTargetDataFolder(dataFolder);
        builder = builder.WithFileSystem(new DiscoveryFileSystem(dataFolder));
        // A later VFS discovery can expose a different projected plugins.txt.
        // Explicit sessions retain the order observed at startup;
        // plugin files themselves remain shared VFS inputs, not private copies.
        if (SessionRuntime.DirectoryPath is not null)
            lock (orderGate)
                if (sessionOrders.TryGetValue(dataFolder, out var order)) builder = builder.WithLoadOrder(order);
        builder = builder.TransformLoadOrderListings(x => x.Where(y => y.Enabled && !IsOutputPlugin(y.ModKey, outputName)));
        var game = new LoadedGame(builder.Build());
        try
        {
            if (SessionRuntime.DirectoryPath is not null)
                lock (orderGate)
                {
                    var current = game.Mods.Select(m => m.ModKey).ToArray();
                    if (sessionOrders.TryGetValue(dataFolder, out var expected) && !expected.SequenceEqual(current))
                        throw new IOException("This session's observed load order changed. Restart through MO2 with the intended unchanged profile. No partial load order is accepted.");
                    sessionOrders[dataFolder] = current;
                }
            SessionRuntime.LoadOrder(dataFolder, game.Mods.Select(m => m.ModKey.FileName.String)); return game;
        }
        catch { game.Dispose(); throw; }
    }

    private static bool IsOutputPlugin(ModKey key, string? outputName)
    {
        if (string.IsNullOrWhiteSpace(outputName)) return false;
        var name = key.FileName.String;
        if (name.Equals(outputName, StringComparison.OrdinalIgnoreCase)) return true;
        var stem = Path.GetFileNameWithoutExtension(outputName);
        if (!name.StartsWith(stem + "_", StringComparison.OrdinalIgnoreCase) ||
            !name.EndsWith(".esp", StringComparison.OrdinalIgnoreCase)) return false;
        var suffix = name[(stem.Length + 1)..^4];
        return int.TryParse(suffix, out _);
    }

}
