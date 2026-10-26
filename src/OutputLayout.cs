using Mutagen.Bethesda.Skyrim;

namespace XEnchantmentPatcher;

/// <summary>
/// The one source of truth for output-plugin capacity and local FormID range.
/// Light layout: ESL-flagged plugins of at most 4096 records (IDs 0x000-0xFFF), split across files.
/// Full layout: a single regular plugin, no Small flag, new IDs from 0x800 to 0xFFFFFF.
/// Writers derive the layout from the plugin they hold, so allocation and validation cannot disagree.
/// </summary>
public sealed class OutputLayout
{
    public static readonly OutputLayout Light = new(true, 0x000, 0x1000);
    public static readonly OutputLayout Full = new(false, 0x800, 0x1000000 - 0x800);

    private OutputLayout(bool isLight, uint firstId, uint idCount)
    {
        IsLight = isLight; FirstId = firstId; IdCount = idCount;
    }

    public bool IsLight { get; }
    public uint FirstId { get; }
    /// <summary>Number of distinct local IDs, which is also the new-record capacity of one plugin.</summary>
    public uint IdCount { get; }
    public uint LastId => FirstId + IdCount - 1;
    public string Kind => IsLight ? "ESPFE" : "full plugin";

    public static OutputLayout For(ProfileSettings profile) => profile.FullOutputPlugin ? Full : Light;
    public static OutputLayout For(ISkyrimModGetter mod) => mod.ModHeader.Flags.HasFlag(SkyrimModHeader.HeaderFlag.Small) ? Light : Full;

    public void Apply(SkyrimMod mod)
    {
        if (IsLight) mod.ModHeader.Flags |= SkyrimModHeader.HeaderFlag.Small;
    }

    /// <summary>Plugin that receives the record with this zero-based allocation ordinal.</summary>
    public int PatchIndex(int ordinal) => IsLight ? ordinal / (int)IdCount : 0;

    public int PatchCount(int records)
    {
        if (!IsLight)
        {
            if (records > IdCount) throw new InvalidOperationException($"The output needs {records:N0} new records, more than a full plugin's {IdCount:N0}-record capacity.");
            return 1;
        }
        return Math.Max(1, (records + (int)IdCount - 1) / (int)IdCount);
    }

    public bool Contains(uint id) => id >= FirstId && id <= LastId;
}
