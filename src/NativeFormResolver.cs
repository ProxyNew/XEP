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

// Loaded-record lookups shared by native input captures (SkyPatcher, LLOS).
// Each capture keeps its own identifier syntax; this owns plugin, winner and
// native EditorID cache facts so they are interpreted once.
internal sealed class NativeFormResolver(IReadOnlyList<ISkyrimModGetter> mods,
    Func<IReadOnlyDictionary<FormKey, (ISkyrimMajorRecordGetter Record, ModKey Plugin)>> loadWinners)
{
    private readonly Dictionary<string, ISkyrimModGetter> plugins = mods.ToDictionary(m => m.ModKey.FileName.String, StringComparer.OrdinalIgnoreCase);
    private IReadOnlyDictionary<FormKey, (ISkyrimMajorRecordGetter Record, ModKey Plugin)>? winners;
    private Dictionary<string, IMajorRecordGetter[]>? editorIds;

    public bool HasPlugin(string name) => plugins.ContainsKey(name);
    public bool TryGetPlugin(string name, out ISkyrimModGetter plugin) => plugins.TryGetValue(name, out plugin!);

    public ISkyrimMajorRecordGetter? Winner(FormKey key)
    {
        EnsureWinners();
        return winners!.TryGetValue(key, out var winner) ? winner.Record : null;
    }

    // The native name map retains aliases introduced by overrides. Resolve each
    // alias to the winning form, deduplicating its history.
    // An optional filter keeps only the candidates of one record type before ambiguity is judged
    // (native keyword lookups search the keyword array alone).
    public (string Status, IMajorRecordGetter? Record) EditorId(string identifier, Func<IMajorRecordGetter, bool>? filter = null)
    {
        EnsureWinners();
        editorIds ??= EditorIds();
        if (!editorIds.TryGetValue(identifier, out var records)) return ("missing-record", null);
        if (filter is not null) records = records.Where(filter).ToArray();
        if (records.Length == 0) return ("missing-record", null);
        return records.Length != 1 ? ("ambiguous-editorid", null) : ("resolved", records[0]);
    }

    // Every record an EditorID names, for a reference that is ambiguous.
    public IReadOnlyList<IMajorRecordGetter> EditorIdMatches(string identifier)
    {
        EnsureWinners();
        editorIds ??= EditorIds();
        return editorIds.TryGetValue(identifier, out var records) ? records : [];
    }

    public IReadOnlyDictionary<FormKey, IReadOnlySet<FormKey>> KeywordMembers(HashSet<FormKey> requested)
    {
        var members = requested.ToDictionary(k => k, _ => new HashSet<FormKey>());
        if (requested.Count > 0)
        {
            EnsureWinners();
            foreach (var record in winners!.Values.Select(w => w.Record).Where(r => !r.IsDeleted))
            if (record is IKeywordedGetter<IKeywordGetter> keyworded)
            foreach (var keyword in keyworded.Keywords ?? [])
                if (members.TryGetValue(keyword.FormKey, out var items)) items.Add(record.FormKey);
        }
        return members.ToDictionary(p => p.Key, p => (IReadOnlySet<FormKey>)p.Value).AsReadOnly();
    }

    // Each winning record's keywords in record order, restricted to the requested
    // keywords; records holding none of them are omitted.
    public IReadOnlyDictionary<FormKey, FormKey[]> OrderedKeywords(IReadOnlySet<FormKey> requested)
    {
        var result = new Dictionary<FormKey, FormKey[]>();
        if (requested.Count == 0) return result;
        EnsureWinners();
        foreach (var record in winners!.Values.Select(w => w.Record).Where(r => !r.IsDeleted))
            if (record is IKeywordedGetter<IKeywordGetter> keyworded
                && (keyworded.Keywords ?? []).Select(k => k.FormKey).Where(requested.Contains).ToArray() is { Length: > 0 } keys)
                result[record.FormKey] = keys;
        return result;
    }

    private void EnsureWinners() => winners ??= loadWinners();

    private Dictionary<string, IMajorRecordGetter[]> EditorIds()
    {
        using var timing = PhaseTiming.Measure("native.editor-ids");
        // Read plugins with the same bounded parallelism as the winner index;
        // merge in load order so alias order and winners stay deterministic.
        var names = new (string EditorId, FormKey Key)[mods.Count][];
        void Read(int i) => names[i] = mods[i].EnumerateMajorRecords().Where(r => !string.IsNullOrEmpty(r.EditorID))
            .Select(r => (r.EditorID!, r.FormKey)).ToArray();
        if (mods.Distinct(ReferenceEqualityComparer.Instance).Count() != mods.Count) for (var i = 0; i < mods.Count; i++) Read(i);
        else Parallel.For(0, mods.Count, new ParallelOptions { MaxDegreeOfParallelism = Math.Min(4, Math.Max(1, Environment.ProcessorCount)) }, Read);
        return names.SelectMany(n => n).GroupBy(n => n.EditorId, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key,
            g => g.Select(n => n.Key).Distinct().Select(key => (IMajorRecordGetter)winners![key].Record).ToArray(), StringComparer.OrdinalIgnoreCase);
    }
}
