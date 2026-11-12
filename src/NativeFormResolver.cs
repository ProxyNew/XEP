using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Aspects;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;

namespace XEnchantmentPatcher;

// Three-state outcome of one identifier. Unverified means the native result depends on state XEP
// cannot read (ambiguous or unavailable EditorID lookups, MergeMapper, bare FormIDs): it is neither
// a resolution nor a definite failure.
internal enum ReferenceOutcome { Resolved, Failed, Unverified }

internal static class ReferenceStatus
{
    public static ReferenceOutcome Classify(string status) => status switch
    {
        "resolved" or "resolved-unmatchable" or "plugin-filter" or "created-keyword" => ReferenceOutcome.Resolved,
        "ambiguous-editorid" or "editorid-cache-unavailable" or "merge-mapping-unavailable" or "merge-mapping-unverified"
            or "bare-formid-unverified" or "created-keyword-order-unverified"
            => ReferenceOutcome.Unverified,
        _ => ReferenceOutcome.Failed,
    };

    // Native GetBoundObject / As<TESBoundObject> accept any TESBoundObject. Reasoned list of the Skyrim
    // bound types; equipment relevance is decided by consumers, not by this check.
    public static bool IsBoundObject(IMajorRecordGetter record)
        => record is IWeaponGetter or IArmorGetter or IAmmunitionGetter or IBookGetter or IIngestibleGetter or IIngredientGetter
            or IKeyGetter or ILightGetter or IMiscItemGetter or IScrollGetter or ISoulGemGetter or IAlchemicalApparatusGetter
            or ILeveledItemGetter or IContainerGetter or IStaticGetter or IMoveableStaticGetter or IFurnitureGetter or IDoorGetter
            or IActivatorGetter or IFloraGetter or ITreeGetter or IProjectileGetter or INpcGetter or ILeveledNpcGetter
            or ILeveledSpellGetter or ITextureSetGetter or IHazardGetter or IExplosionGetter or IAddonNodeGetter or IGrassGetter
            or IArtObjectGetter or IAcousticSpaceGetter or ISoundMarkerGetter or IIdleMarkerGetter;
}

// Loaded-record lookups shared by the framework input captures (SkyPatcher, LLOS, KID, SPID, CID, CDF).
// Each capture keeps its own identifier syntax; this owns plugin, winner and native EditorID cache facts
// so they are interpreted once. The winners are the scan's load-order winners; the EditorID index is an
// input structure built here, once per scan, when the scan has a configuration that can name a record.
internal sealed class NativeFormResolver
{
    internal readonly Dictionary<string, ISkyrimModGetter> plugins;
    private readonly IReadOnlyDictionary<FormKey, (ISkyrimMajorRecordGetter Record, ModKey Plugin)> winners;
    private readonly Dictionary<string, IMajorRecordGetter[]>? editorIds;

    // names: the EditorIDs LoadOrderWinners collected (load order, then record order); null builds no index.
    public NativeFormResolver(IReadOnlyList<ISkyrimModGetter> mods,
        IReadOnlyDictionary<FormKey, (ISkyrimMajorRecordGetter Record, ModKey Plugin)> winners, IEnumerable<(string EditorId, FormKey Key)>? names)
    {
        Mods = mods;
        plugins = mods.ToDictionary(m => m.ModKey.FileName.String, StringComparer.OrdinalIgnoreCase);
        this.winners = winners;
        if (names is null) return;
        using var timing = PhaseTiming.Measure("native.editor-ids");
        editorIds = names.GroupBy(n => n.EditorId, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key,
            g => g.Select(n => n.Key).Distinct().Select(key => (IMajorRecordGetter)winners[key].Record).ToArray(), StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyList<ISkyrimModGetter> Mods { get; }

    public bool HasPlugin(string name) => plugins.ContainsKey(name);
    public bool TryGetPlugin(string name, out ISkyrimModGetter plugin) => plugins.TryGetValue(name, out plugin!);

    public ISkyrimMajorRecordGetter? Winner(FormKey key) => winners.TryGetValue(key, out var winner) ? winner.Record : null;

    // The native name map retains aliases introduced by overrides. Resolve each
    // alias to the winning form, deduplicating its history.
    // An optional filter keeps only the candidates of one record type before ambiguity is judged
    // (native keyword lookups search the keyword array alone). CDF matches a winning record's own
    // EditorID with case (caseSensitive); the other tools use the native case-insensitive map.
    // Without an index (the scan has no configuration) nothing is named.
    public (string Status, IMajorRecordGetter? Record) EditorId(string identifier, Func<IMajorRecordGetter, bool>? filter = null, bool caseSensitive = false)
    {
        if (editorIds is null || !editorIds.TryGetValue(identifier, out var records)) return ("missing-record", null);
        if (caseSensitive) records = records.Where(r => string.Equals(r.EditorID, identifier, StringComparison.Ordinal)).ToArray();
        if (filter is not null) records = records.Where(filter).ToArray();
        if (records.Length == 0) return ("missing-record", null);
        return records.Length != 1 ? ("ambiguous-editorid", null) : ("resolved", records[0]);
    }

    // Every record an EditorID names, for a reference that is ambiguous.
    public IReadOnlyList<IMajorRecordGetter> EditorIdMatches(string identifier)
        => editorIds is not null && editorIds.TryGetValue(identifier, out var records) ? records : [];

    public IReadOnlyDictionary<FormKey, IReadOnlySet<FormKey>> KeywordMembers(HashSet<FormKey> requested)
    {
        var members = requested.ToDictionary(k => k, _ => new HashSet<FormKey>());
        if (requested.Count > 0)
            foreach (var record in winners.Values.Select(w => w.Record).Where(r => !r.IsDeleted))
            if (record is IKeywordedGetter<IKeywordGetter> keyworded)
            foreach (var keyword in keyworded.Keywords ?? [])
                if (members.TryGetValue(keyword.FormKey, out var items)) items.Add(record.FormKey);
        return members.ToDictionary(p => p.Key, p => (IReadOnlySet<FormKey>)p.Value).AsReadOnly();
    }

    // Each winning record's keywords in record order, restricted to the requested
    // keywords; records holding none of them are omitted.
    public IReadOnlyDictionary<FormKey, FormKey[]> OrderedKeywords(IReadOnlySet<FormKey> requested)
    {
        var result = new Dictionary<FormKey, FormKey[]>();
        if (requested.Count == 0) return result;
        foreach (var record in winners.Values.Select(w => w.Record).Where(r => !r.IsDeleted))
            if (record is IKeywordedGetter<IKeywordGetter> keyworded
                && (keyworded.Keywords ?? []).Select(k => k.FormKey).Where(requested.Contains).ToArray() is { Length: > 0 } keys)
                result[record.FormKey] = keys;
        return result;
    }
}
