using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using System.Security.Cryptography;
using System.Text;

namespace XEnchantmentPatcher;

public static partial class PatcherEngine
{
    private static string RuntimeHolderEvidenceDomain(string evidence) => evidence.Contains("lvli-carrier-v1", StringComparison.Ordinal)
        ? "runtime-holder-capability-closure-v2" : "effect-free-holder-view-v1";
    private sealed record RuntimeQuestHolder(FormKey Owner, string Identity, string Property, short Alias, int ArrayIndex);
    private sealed record RuntimeHolderCheck(string OwnerIdentity, FormKey Target, string? Problem);
    // Cache representation only: every caller still executes its live holder
    // proof. A cheap fingerprint locates candidates; exact ordinal set equality
    // is always required, so collisions change lookup cost, never rendered text.
    private sealed class HolderEvidenceText
    {
        // Ordinal equality with the string's own ordinal hash, computed once per
        // instance: certificate strings are long and the same instances recur.
        private sealed class CachedOrdinalComparer : IEqualityComparer<string>
        {
            public static readonly CachedOrdinalComparer Instance = new();
            private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<string, System.Runtime.CompilerServices.StrongBox<int>> Hashes = new();
            public bool Equals(string? x, string? y) => string.Equals(x, y, StringComparison.Ordinal);
            public int GetHashCode(string value) => value.Length < 64 ? value.GetHashCode()
                : Hashes.GetValue(value, static v => new(v.GetHashCode())).Value;
        }
        private sealed class Entry(HashSet<string> certificates, string text)
        {
            public HashSet<string> Certificates { get; } = certificates;
            public string Text { get; } = text;
            private byte[]? utf8;
            public void AppendTo(IncrementalHash signature) => signature.AppendData(utf8 ??= Encoding.UTF8.GetBytes(Text));
        }
        private const int MaximumEntries = 32, MaximumCharacters = 16 * 1024 * 1024, MaximumAtoms = 131072;
        private readonly Dictionary<int, List<Entry>> entries = [];
        private int count, characters, atoms;

        public void AppendUtf8(IncrementalHash signature, string text)
        {
            // Reuse only bytes owned by a currently retained canonical entry.
            // Oversized, unretained and evicted text does not grow another cache.
            foreach (var bucket in entries.Values)
                foreach (var entry in bucket)
                    if (ReferenceEquals(entry.Text, text)) { entry.AppendTo(signature); return; }
            signature.AppendData(Encoding.UTF8.GetBytes(text));
        }

        public string Render(IEnumerable<string> certificates)
        {
            var unique = certificates.ToHashSet(CachedOrdinalComparer.Instance);
            if (unique.Count == 0) return "";
            var hash = 0; long certificateCharacters = 0;
            foreach (var certificate in unique)
            {
                // Do not hash every character a second time just to choose a
                // bucket. The set already owns full ordinal hashes/equality.
                var length = certificate?.Length ?? 0;
                var fingerprint = length == 0 ? 0 : HashCode.Combine(length, certificate![0], certificate[length / 2], certificate[^1]);
                hash = unchecked(hash + fingerprint);
                certificateCharacters += certificate?.Length ?? 0;
            }
            hash = HashCode.Combine(hash, unique.Count);
            if (entries.TryGetValue(hash, out var matches))
                foreach (var entry in matches)
                    if (entry.Certificates.SetEquals(unique)) return entry.Text;

            using var timing = PhaseTiming.Measure("availability.holder-frame.evidence-format");
            var text = string.Join('|', unique.Order(StringComparer.Ordinal).ToArray());
            var retainedCharacters = certificateCharacters + text.Length;
            if (retainedCharacters > MaximumCharacters || unique.Count > MaximumAtoms) return text;
            if (count >= MaximumEntries || characters + retainedCharacters > MaximumCharacters || atoms + unique.Count > MaximumAtoms)
            { entries.Clear(); count = characters = atoms = 0; }
            if (!entries.TryGetValue(hash, out matches)) entries[hash] = matches = [];
            matches.Add(new(unique, text)); count++; characters += (int)retainedCharacters; atoms += unique.Count;
            return text;
        }
    }
    private sealed partial class Catalog
    {
        // A planning pass starts with its own empty representation cache.
        public HolderEvidenceText HolderEvidence { get; } = new();
    }
    // Attached non-Quest VMAD holders cannot be treated as absent just because
    // the supported binding frame only loads Quest/ReferenceAlias instances.
    // This closes an observed plugin-metadata gap, not arbitrary native/global
    // code, effective asset, event or saved/runtime attachment completeness.
    private static string? UnclosedRuntimeHolderProblem(ScriptInstance[] frame, Catalog catalog, FormKey? listRoot = null)
        => RuntimeHolderFrameProblem(frame, catalog, listRoot, out _);

    // With a slice scope (ADR 0008/0009) the walk follows only records the source
    // mod defines or wins, proves non-Quest holders by the slice rules (E1), and
    // treats in-mod FormList carrier edges as holders (E3). Other mods' scripts are
    // outside the proof.
    private static string? RuntimeHolderFrameProblem(ScriptInstance[] frame, Catalog catalog, FormKey? listRoot, out string holderEvidence,
        RuntimeSliceScope? slice = null)
    {
        using var timing = PhaseTiming.Count("availability.holder-frame");
        // Review prefetch workers prove list roots one at a time: script analysis state
        // is touched only inside proofs, so one lock serialises all of it. Other proofs
        // leave the item to the ordered review (see PrefetchSpecializedAssessments).
        if (ReviewPrefetch.Active && !ReviewPrefetch.Proving)
        {
            if (frame.Length > 0 || slice is not null || listRoot is null) throw new ReviewPrefetchDeferred();
            lock (catalog.RuntimeListRootHolderVerdicts)
            {
                ReviewPrefetch.Proving = true;
                try { return RuntimeHolderFrameProblem(frame, catalog, listRoot, out holderEvidence, slice); }
                finally { ReviewPrefetch.Proving = false; }
            }
        }
        // Only the empty-frame, whole-load-order list-root verdict is reused.
        // Property frames and slice proofs always run their own proof.
        if (frame.Length > 0 || slice is not null || listRoot is not { } root)
            return RuntimeHolderFrameProof(frame, catalog, listRoot, out holderEvidence, slice);
        var inputs = RuntimeListRootHolderInputs.Of(catalog);
        if (catalog.RuntimeListRootHolderVerdicts.TryGetValue(root, out var known) && known.Inputs.Matches(inputs))
        {
            holderEvidence = known.Evidence;
            return known.Problem;
        }
        var problem = RuntimeHolderFrameProof(frame, catalog, listRoot, out holderEvidence, slice);
        // A proof that itself changed an input (a newly loaded instance) is not retained.
        if (RuntimeListRootHolderInputs.Of(catalog).Matches(inputs))
            catalog.RuntimeListRootHolderVerdicts[root] = new(problem, holderEvidence, inputs);
        return problem;
    }

    // The empty-frame, non-slice proof reads three inputs that change while a
    // catalog is built or later: possible carrier exports (set once, during
    // availability construction, and shared by passes), the binding resolver, and
    // the add-only loaded script instances. A pass's resolver is identified by the
    // initializer family that bound it, so passes of one analysis share verdicts. Every other input is fixed by the
    // catalog's load order or is already cached once per key (holder indices,
    // holder views, resolved bindings), so a recomputation would repeat it.
    // AvailabilityInitialized marks the start of construction, not completion,
    // so the inputs themselves, rather than that flag, decide reuse.
    private readonly record struct RuntimeListRootHolderInputs(object? PossibleCarriers, object? BindingResolver, int ScriptInstances)
    {
        public static RuntimeListRootHolderInputs Of(Catalog catalog)
            => new(catalog.RuntimePossibleListCarriers, catalog.RuntimeBindingResolverOrigin is { } origin
                && ReferenceEquals(origin.Resolver, catalog.RuntimeBindingResolver) ? origin.Family : catalog.RuntimeBindingResolver,
                catalog.RuntimeScriptInstances.Count);
        public bool Matches(RuntimeListRootHolderInputs other) => ReferenceEquals(PossibleCarriers, other.PossibleCarriers)
            && ReferenceEquals(BindingResolver, other.BindingResolver) && ScriptInstances == other.ScriptInstances;
    }
    private sealed record RuntimeListRootHolderVerdict(string? Problem, string Evidence, RuntimeListRootHolderInputs Inputs);

    private static string? RuntimeHolderFrameProof(ScriptInstance[] frame, Catalog catalog, FormKey? listRoot, out string holderEvidence,
        RuntimeSliceScope? slice)
    {
        using var timing = PhaseTiming.Measure("availability.holder-frame.proof");
        bool Followed(FormKey record) => slice is null || InSliceScope(record, slice.Source, catalog);
        holderEvidence = "";
        var certificates = new List<string>();
        var winners = RuntimeRecordWinners(catalog);
        if (catalog.RuntimeUnclosedHolderIndex is null)
        {
            var handles = new Dictionary<FormKey, List<string>>();
            var typedHandles = new Dictionary<FormKey, List<RuntimePublicHolder>>();
            var questHandles = new Dictionary<FormKey, List<RuntimeQuestHolder>>();
            var parents = new Dictionary<FormKey, HashSet<FormKey>>();
            var listParents = new Dictionary<FormKey, List<RuntimeListCarrier>>();
            foreach (var (record, plugin) in winners.Values)
            {
                if (record.IsDeleted) continue;
                if (record is ILeveledItemGetter leveled)
                    foreach (var (entry, slot) in (leveled.Entries ?? []).Select((e, i) => (e, i)))
                    {
                        if (entry.Data is not { } data || !winners.TryGetValue(data.Reference.FormKey, out var child)
                            || child.Record.IsDeleted || child.Record is not ILeveledItemGetter) continue;
                        if (!listParents.TryGetValue(data.Reference.FormKey, out var rows)) listParents[data.Reference.FormKey] = rows = [];
                        rows.Add(new(record.FormKey, $"winning-lvli-carrier-v1 {record.FormKey}/{plugin}/entry {slot}/{data.Reference.FormKey}"));
                    }
                if (record is IFormListGetter list)
                    foreach (var member in list.Items)
                    {
                        if (!parents.TryGetValue(member.FormKey, out var rows)) parents[member.FormKey] = rows = [];
                        rows.Add(list.FormKey);
                    }
                if (record is not IHaveVirtualMachineAdapterGetter vmad || vmad.VirtualMachineAdapter is not { } adapter) continue;
                var entries = adapter.Scripts.Select((s, i) => (Script: s, Slot: i, AliasSlot: -1, Attached: (IScriptObjectPropertyGetter?)null));
                if (adapter is IQuestAdapterGetter questAdapter)
                    entries = entries.Concat(questAdapter.Aliases.SelectMany((a, ai) => a.Scripts.Select((s, si) => (s, si, ai, (IScriptObjectPropertyGetter?)a.Property))));
                foreach (var (script, slot, aliasSlot, attached) in entries)
                    foreach (var (property, propertyIndex) in script.Properties.Select((p, i) => (p, i)))
                    {
                        if (property is IScriptObjectPropertyGetter scalar) Add(scalar, -1);
                        else if (property is IScriptObjectListPropertyGetter array)
                            foreach (var (value, index) in array.Objects.Select((v, i) => (v, i))) Add(value, index);
                        void Add(IScriptObjectPropertyGetter handle, int index)
                        {
                            if (handle.Object.FormKey.IsNull) return;
                            if (record is IQuestGetter)
                            {
                                if (!questHandles.TryGetValue(handle.Object.FormKey, out var questRows)) questHandles[handle.Object.FormKey] = questRows = [];
                                (FormKey Quest, short Alias)? aliasIdentity = attached is null ? null : (attached.Object.FormKey, attached.Alias);
                                questRows.Add(new(record.FormKey, $"{record.FormKey}/{aliasSlot}/{slot}/{aliasIdentity}/{script.Name}", property.Name, handle.Alias, index));
                                return;
                            }
                            if (!handles.TryGetValue(handle.Object.FormKey, out var rows)) handles[handle.Object.FormKey] = rows = [];
                            rows.Add($"{record.FormKey} (winning {plugin}), script {slot}/{script.Name}, property {propertyIndex}/{property.Name}, alias {handle.Alias}, array index {index}");
                            if (!typedHandles.TryGetValue(handle.Object.FormKey, out var typedRows)) typedHandles[handle.Object.FormKey] = typedRows = [];
                            typedRows.Add(new(record.FormKey, slot, propertyIndex, property.Name, handle.Alias, index));
                        }
                    }
            }
            catalog.RuntimeUnclosedHolderIndex = handles.ToDictionary(p => p.Key, p => p.Value.Order(StringComparer.Ordinal).ToArray());
            catalog.RuntimeNonQuestHolderIndex = typedHandles.ToDictionary(p => p.Key, p => p.Value.ToArray());
            catalog.RuntimeFormListHolderParents = parents.ToDictionary(p => p.Key, p => p.Value.Order().ToArray());
            catalog.RuntimeAuthoredListCarriers = listParents.ToDictionary(p => p.Key, p => p.Value.OrderBy(e => e.Evidence, StringComparer.Ordinal).ToArray());
            catalog.RuntimeQuestHolderIndex = questHandles.ToDictionary(p => p.Key, p => p.Value.OrderBy(h => h.Identity, StringComparer.Ordinal).ToArray());
        }
        var relevant = frame.Select(i => i.Quest.FormKey).ToHashSet();
        if (listRoot is { } seedList) relevant.Add(seedList);
        foreach (var script in frame)
            foreach (var handle in script.VmadHandles)
                if (catalog.Lists.ContainsKey(handle.Record) || winners.TryGetValue(handle.Record, out var winner) && !winner.Record.IsDeleted && winner.Record is IFormListGetter)
                    relevant.Add(handle.Record);
        // A collection exposing a list is another public handle to it, even
        // when no loaded Quest script mentions that collection.
        var pending = new Queue<(FormKey Key, int Depth)>(relevant.Order().Select(k => (k, 0)));
        var seen = new HashSet<FormKey>();
        var covered = new HashSet<string>(StringComparer.Ordinal);
        while (pending.TryDequeue(out var current))
        {
            if (seen.Contains(current.Key)) continue;
            if (current.Depth > 16 || seen.Count >= 8192) return "unclosed winning VMAD holder capability scan exceeds 16 edges or 8192 states";
            seen.Add(current.Key);
            foreach (var edge in catalog.RuntimeAuthoredListCarriers!.GetValueOrDefault(current.Key, [])
                .Concat(catalog.RuntimePossibleListCarriers?.GetValueOrDefault(current.Key, []) ?? []))
            {
                if (edge.Parent is not { } parent)
                {
                    if (slice is not null && edge.Instance is { } exporter && catalog.RuntimeScriptInstances.TryGetValue(exporter, out var other)
                        && !InSliceScope(other.Quest.FormKey, slice.Source, catalog)) continue;
                    return "unclosed possible leveled-list carrier export of " + current.Key + ": " + edge.Evidence;
                }
                certificates.Add(edge.Evidence);
                pending.Enqueue((parent, current.Depth + 1));
            }
            if (slice is not null)
                foreach (var carrier in SliceFormListCarriers(slice.Source, catalog).GetValueOrDefault(current.Key, [])
                    .Concat(EnsureSliceFlow(slice.Source, catalog).CarriersOf(current.Key)).Distinct().Where(Followed))
                {
                    if (SliceCarrierProblem(carrier, slice.Source, catalog) is { } carrierProblem) return $"unproved FormList carrier of {current.Key}: {carrierProblem}";
                    certificates.Add($"slice-formlist-carrier-v1 {current.Key}/{carrier}");
                    pending.Enqueue((carrier, current.Depth + 1));
                }
            if (slice is not null && catalog.RuntimeUnclosedHolderIndex.ContainsKey(current.Key))
            {
                var sliceView = RuntimeSliceHolderView(current.Key, catalog, slice);
                if (sliceView.Problem is not null) return $"unclosed winning non-Quest VMAD holder of {current.Key}: {sliceView.FailureIdentity}; {sliceView.Problem}";
                certificates.Add(sliceView.Evidence);
            }
            else if (catalog.RuntimeUnclosedHolderIndex.TryGetValue(current.Key, out var holders))
            {
                if (!catalog.RuntimeHolderViewResults.TryGetValue(current.Key, out var result))
                {
                    result = RuntimeEffectFreeHolderView(current.Key, catalog);
                    catalog.RuntimeHolderViewResults[current.Key] = result;
                    catalog.RuntimeHolderViewChecks.Add(result);
                }
                if (result.Problem is not null) return $"unclosed winning non-Quest VMAD holder of {current.Key}: {holders[0]}; {result.Problem}";
                certificates.Add(result.Evidence);
            }
            foreach (var parent in catalog.RuntimeFormListHolderParents!.GetValueOrDefault(current.Key, []).Where(Followed)) pending.Enqueue((parent, current.Depth + 1));
            foreach (var holder in catalog.RuntimeQuestHolderIndex!.GetValueOrDefault(current.Key, []))
            {
                // Another mod's holders are proved in their own analysis (combined evidence).
                if (!Followed(holder.Owner)) continue;
                // Contract bound: a quest's holders count only when their text uses a slice value.
                if (slice is not null && winners.GetValueOrDefault(current.Key).Record is IQuestGetter && catalog.RuntimeSliceCallers is { } callersOf
                    && !callersOf(current.Key, slice.Source).Any(i => i.Identity == holder.Identity)) continue;
                if (listRoot is not null)
                {
                    if (winners.TryGetValue(current.Key, out var currentWinner) && currentWinner.Record is IQuestGetter)
                    {
                        if (!covered.Contains(holder.Identity)) return $"unclosed winning Quest public holder {holder.Identity}/{holder.Property} of {current.Key}";
                    }
                    else if (slice is not null && winners.TryGetValue(current.Key, out var carrierWinner) && carrierWinner.Record is IFormListGetter)
                    {
                        // E3: a carrier's holders use it only as proved by SliceCarrierProblem.
                        if (SliceCarrierProblem(current.Key, slice.Source, catalog) is { } carrierProblem)
                            return $"unclosed winning Quest holder {holder.Identity}/{holder.Property} of carrier {current.Key}: {carrierProblem}";
                        covered.Add(holder.Identity);
                    }
                    else
                    {
                        var resolver = slice is null ? catalog.RuntimeBindingResolver : catalog.RuntimeSliceBindingResolver;
                        if (holder.Alias != -1 || holder.ArrayIndex != -1 || !catalog.RuntimeScriptInstances.TryGetValue(holder.Identity, out var instance)
                            || resolver is null) return $"unclosed winning Quest collection/alias holder {holder.Identity}/{holder.Property} of {current.Key}";
                        var binding = resolver(instance, holder.Property, true);
                        if (binding.Problem is not null) return $"unclosed winning Quest holder {holder.Identity}/{holder.Property} of {current.Key}: {binding.Problem}";
                        slice?.Warnings.AddRange(binding.Warnings);
                        covered.UnionWith(binding.CoveredInstanceIdentities);
                    }
                }
                pending.Enqueue((holder.Owner, current.Depth + 1));
            }
        }
        foreach (var quest in frame.Select(i => i.Quest).Concat(seen.Select(k => winners.GetValueOrDefault(k).Record).OfType<IQuestGetter>()).DistinctBy(q => q.FormKey).Where(q => Followed(q.FormKey)).OrderBy(q => q.FormKey))
        {
            var adapter = quest.VirtualMachineAdapter!;
            var attached = adapter.Scripts.Select(s => s.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var root in adapter.Fragments.Select(f => f.ScriptName).Prepend(adapter.FileName).Where(n => !string.IsNullOrWhiteSpace(n)).Distinct(StringComparer.OrdinalIgnoreCase))
                if (!attached.Contains(root)) return $"unbound Quest fragment root {root} on {quest.FormKey} has no closed attached script frame";
        }
        // Render only after the live proof succeeds; cached text grants nothing.
        using var evidenceTiming = PhaseTiming.Measure("availability.holder-frame.evidence");
        holderEvidence = catalog.HolderEvidence.Render(certificates);
        return null;
    }
}
