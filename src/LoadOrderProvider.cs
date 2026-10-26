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
        Mods = environment.LoadOrder.ListedOrder.Where(x => x.Enabled)
            .Select(x => x.Mod ?? throw new IOException($"Could not load {x.ModKey}.")).ToArray();
    }
    public void Dispose() => _environment.Dispose();
}

public static class LoadOrderProvider
{
    private static readonly Dictionary<string, ModKey[]> sessionOrders = new(StringComparer.OrdinalIgnoreCase);
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
