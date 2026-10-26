using System.Threading;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;

namespace XEnchantmentPatcher;

public static partial class PatcherEngine
{
    // Memo tables that review workers may fill in parallel. Every value is a pure
    // function of the frozen analysis inputs: it is computed outside the lock and the
    // first published value wins, so all readers share one complete instance.
    private static TValue SharedMemo<TKey, TValue>(Dictionary<TKey, TValue> table, TKey key, Func<TValue> compute) where TKey : notnull
    {
        lock (table) if (table.TryGetValue(key, out var known)) return known;
        var value = compute();
        lock (table)
        {
            if (table.TryGetValue(key, out var known)) return known;
            table[key] = value;
            return value;
        }
    }

    // A lazily built, set-once value with the same publication rule.
    private static T SharedOnce<T>(object gate, Func<T?> read, Action<T> write, Func<T> compute) where T : class
    {
        if (read() is { } known) return known;
        var value = compute();
        lock (gate)
        {
            if (read() is { } published) return published;
            write(value);
            return value;
        }
    }

    private static HashSet<FormKey> StaticSourceItems(Catalog catalog)
        => SharedOnce(catalog, () => catalog.StaticSourceItems, value => catalog.StaticSourceItems = value,
            () => catalog.Mods.SelectMany(m => m.EnumerateMajorRecords<IItemGetter>())
                .GroupBy(i => i.FormKey).Where(g => !g.Last().IsDeleted).Select(g => g.Key).ToHashSet());

    // A worker thread's current review item. Its distribution notes are kept, in
    // order, and written when the ordered review reaches that item.
    private static class ReviewPrefetch
    {
        [ThreadStatic] public static List<string>? Notes;
        public static bool Active => Notes is not null;
        // Set while this worker holds the list-root proof lock.
        [ThreadStatic] public static bool Proving;
    }

    // The item needs a property-frame or slice proof, which only the ordered review
    // runs. The review assesses it itself, exactly as without a prefetch.
    private sealed class ReviewPrefetchDeferred() : Exception("Assessment needs an ordered script proof.");

    private static void ReviewNote(Catalog catalog, string note)
    {
        if (ReviewPrefetch.Notes is { } notes) notes.Add(note);
        else catalog.DistributionNotes.Add(note);
    }

    // A fresh review's route assessments are pure over its frozen graph and the
    // shared memo tables, so they are computed in parallel before the ordered review,
    // which then reads them. The prefetch assesses exactly the items the review will,
    // decided by the review's own gate without filling order-sensitive caches.
    // Exactness:
    // - Notes are buffered per item and written at that item's place in the review.
    // - A deferred item leaves only complete, pure memo entries; the note-bearing
    //   merchant route entries it added for its own source are removed, so the review
    //   recomputes them and writes their notes in order.
    // - List-root holder proofs run one at a time under one lock; script analysis
    //   state is written only inside proofs. Proof order is not observable: its caches
    //   are keyed, its diagnostic rows are written sorted, and a non-slice proof never
    //   loads script instances, so verdict inputs stay fixed for the whole review
    //   (checked when the review uses a result). Other proofs defer the item.
    private sealed record PrefetchedReview(Dictionary<FormKey, List<string>> Notes, RuntimeListRootHolderInputs Inputs);

    // A prefetched item's assessments, notes and merchant-route entries, reused by a later
    // review of the same load-order analysis and material/origin set when the item's
    // review inputs are unchanged. Those inputs (Fingerprint) are the enabled templates
    // of the item's shape that its author enchantment list allows, and its author
    // destination list; everything else the assessment reads is load-order or material
    // state shared by the whole cache, or script analysis carried by AdoptScriptAnalysis.
    private sealed class ReviewAssessmentCache
    {
        public sealed record Entry(string Fingerprint, SpecializedAssessment Base, SpecializedAssessment Expanded, List<string> Notes,
            List<MerchantSourceRoute>? Exact, (List<MerchantSourceRoute> Routes, string[] Evidence)? Structured);
        public Dictionary<FormKey, Entry> Entries { get; } = [];
        public int Reused { get; set; }
    }

    private static SpecializedAssessment CopyAssessment(SpecializedAssessment source)
        => new(source.Routes.ToList(), source.Evidence.ToList()) { Stage = source.Stage, Refusals = source.Refusals.ToList() };

    // Specialized routes search the item's whole specialized group (for example one-handed
    // swords, war axes and maces together, of any material when expanding), and counted
    // merchant routes a subset of it. Evidence distinguishes disabled templates from ones the
    // author list disallows, so the enabled set and the author lists are keyed separately.
    private static Func<FormKey, string> AssessmentFingerprints(Catalog catalog)
    {
        var byGroup = catalog.Templates.GroupBy(t => SpecializedGroup(t.Class)).ToDictionary(g => g.Key,
            g => string.Join("|", g.Select(t => t.Item.FormKey.ToString() + "/" + t.Enchantment).Order(StringComparer.Ordinal)));
        static string Keys(IEnumerable<FormKey> keys) => string.Join(",", keys.Select(k => k.ToString()).Order(StringComparer.Ordinal));
        // Source proofs read script receiver state: gate-free, or with the effects of gates
        // built for the patched-mod set (load order fixed per cache).
        var receivers = catalog.ScriptGates is not null && catalog.AdoptedGateReceivers is null && catalog.PatchedMods is { } patched
            ? string.Join(",", patched.Select(m => m.FileName.String).Order(StringComparer.OrdinalIgnoreCase)) : "-";
        return key =>
        {
            IMajorRecordGetter? record = catalog.Weapons.GetValueOrDefault(key) ?? (IMajorRecordGetter?)catalog.Armors.GetValueOrDefault(key);
            var cls = record is null ? null : CachedClass(record, catalog);
            var templates = cls is null ? "-" : byGroup.GetValueOrDefault(SpecializedGroup(cls), "");
            var enchantments = catalog.SourceEnchantmentAllow.TryGetValue(key, out var allowList) ? Keys(allowList) : "-";
            var destinations = catalog.SourceDestinationAllow.TryGetValue(key, out var places) ? Keys(places) : "-";
            return templates + "#" + enchantments + "#" + destinations + "#" + receivers;
        };
    }
    private static PrefetchedReview PrefetchSpecializedAssessments(List<CandidateItem> candidates, Catalog catalog,
        ProfileSettings profile, AccessIndex index, HashSet<FormKey> consumers)
    {
        using var timing = PhaseTiming.Measure("review.prefetch");
        var graph = SpecializedGraph(catalog);
        var keys = new List<FormKey>(); var seen = new HashSet<FormKey>();
        var general = new Dictionary<FormKey, bool>();
        bool IsGeneral(FormKey list)
            => catalog.GeneralLootCache.TryGetValue(list, out var cached) ? cached.General
                : general.TryGetValue(list, out var known) ? known : general[list] = UncachedGeneralLootEvidence(list, catalog).General;
        foreach (var item in candidates)
        {
            if (profile.SpecializedLootOverrides.GetValueOrDefault(item.FormKey) == SpecializedLootMode.ScriptDistribution) continue;
            var key = FormKey.Factory(item.FormKey);
            if (!seen.Add(key) || ReviewRecord(key, catalog) is null || !HasBaseLootRoute(key, catalog)
                || graph.Assessments.ContainsKey(key) || graph.ExpandedAssessments.ContainsKey(key)) continue;
            var verdict = catalog.AccessVerdicts[key] = EvaluateAccess(index, key, catalog);
            if (GeneralAccess(key, verdict.Outcome == AccessOutcome.Variants, LootAncestors(key, catalog), consumers, catalog, IsGeneral)
                || !CanAssessSpecialized(verdict, profile)) continue;
            keys.Add(key);
        }
        var notes = new Dictionary<FormKey, List<string>>();
        var workers = Math.Min(Math.Min(4, Math.Max(1, Environment.ProcessorCount)), keys.Count);
        if (workers == 0) return new(notes, RuntimeListRootHolderInputs.Of(catalog));
        var exactBefore = catalog.MerchantExactSourceRoutes.Keys.ToHashSet();
        var structuredBefore = catalog.MerchantStructuredSourceRoutes.Keys.ToHashSet();
        var results = new (SpecializedAssessment Base, SpecializedAssessment Expanded, List<string> Notes)?[keys.Count];
        // Items whose review inputs match a cached entry are not assessed again.
        var reuse = catalog.AssessmentReuse;
        var fingerprint = reuse is null ? null : AssessmentFingerprints(catalog);
        var prints = new string?[keys.Count];
        var compute = new List<int>();
        for (var i = 0; i < keys.Count; i++)
        {
            prints[i] = fingerprint?.Invoke(keys[i]);
            if (reuse is not null && reuse.Entries.TryGetValue(keys[i], out var entry) && entry.Fingerprint == prints[i])
            {
                results[i] = (CopyAssessment(entry.Base), CopyAssessment(entry.Expanded), entry.Notes.ToList());
                if (entry.Exact is { } exact) catalog.MerchantExactSourceRoutes[keys[i]] = exact.ToList();
                if (entry.Structured is { } structured) catalog.MerchantStructuredSourceRoutes[keys[i]] = (structured.Routes.ToList(), structured.Evidence);
                reuse.Reused++;
            }
            else compute.Add(i);
        }
        var next = -1; var deferred = 0;
        Exception? failure = null;
        using (PhaseTiming.Detach())
            Parallel.For(0, workers, new ParallelOptions { MaxDegreeOfParallelism = workers }, _ =>
            {
                for (var n = Interlocked.Increment(ref next); n < compute.Count && Volatile.Read(ref failure) is null; n = Interlocked.Increment(ref next))
                {
                    var i = compute[n];
                    var buffer = ReviewPrefetch.Notes = [];
                    try { results[i] = (AssessSpecializedRoutes(keys[i], catalog, false), AssessSpecializedRoutes(keys[i], catalog, true), buffer); }
                    catch (ReviewPrefetchDeferred) { Interlocked.Increment(ref deferred); }
                    catch (Exception ex) { Interlocked.CompareExchange(ref failure, ex, null); }
                    finally { ReviewPrefetch.Notes = null; }
                }
            });
        // An unexpected failure is the review's to raise, in order: discard everything
        // this prefetch published that a sequential review could observe differently.
        for (var i = 0; i < keys.Count; i++)
        {
            if (results[i] is { } result && failure is null)
            {
                graph.Assessments[keys[i]] = result.Base;
                graph.ExpandedAssessments[keys[i]] = result.Expanded;
                notes[keys[i]] = result.Notes;
                if (reuse is not null && prints[i] is { } print)
                    reuse.Entries[keys[i]] = new(print, CopyAssessment(result.Base), CopyAssessment(result.Expanded), result.Notes.ToList(),
                        catalog.MerchantExactSourceRoutes.TryGetValue(keys[i], out var exact) ? exact.ToList() : null,
                        catalog.MerchantStructuredSourceRoutes.TryGetValue(keys[i], out var structured) ? (structured.Routes.ToList(), structured.Evidence) : null);
                continue;
            }
            if (!exactBefore.Contains(keys[i])) catalog.MerchantExactSourceRoutes.Remove(keys[i]);
            if (!structuredBefore.Contains(keys[i])) catalog.MerchantStructuredSourceRoutes.Remove(keys[i]);
        }
        for (var i = 0; i < deferred; i++) PhaseTiming.Measure("review.prefetch.deferred")?.Dispose();
        if (failure is not null) { PhaseTiming.Measure("review.prefetch.failed")?.Dispose(); notes.Clear(); }
        return new(notes, RuntimeListRootHolderInputs.Of(catalog));
    }
}
