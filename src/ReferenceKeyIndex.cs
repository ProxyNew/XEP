using System.Linq.Expressions;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;

namespace XEnchantmentPatcher;

internal static class ReferenceKeyIndex
{
    // Discover the schema's top-level groups once, not a hand-maintained list of
    // equipment types. Overlay FormKeys read the group index, not record bodies.
    private static readonly Func<ISkyrimModGetter, IGroupGetter>[] Groups = typeof(ISkyrimModGetter)
        .GetProperties().Where(p => typeof(IGroupGetter).IsAssignableFrom(p.PropertyType))
        .Select(p =>
        {
            var mod = Expression.Parameter(typeof(ISkyrimModGetter));
            return Expression.Lambda<Func<ISkyrimModGetter, IGroupGetter>>(
                Expression.Convert(Expression.Property(mod, p), typeof(IGroupGetter)), mod).Compile();
        }).ToArray();

    public static HashSet<FormKey> FindMissing(IEnumerable<FormKey> references,
        IReadOnlyList<ISkyrimModGetter> sources, IReadOnlyList<ISkyrimModGetter> outputs)
    {
        using var timing = PhaseTiming.Measure("validation.reference-index");
        var missing = references.Where(k => !k.IsNull).ToHashSet();
        // Outputs are small and already read for validation; include every owned
        // and overridden key, including records in another output shard.
        foreach (var output in outputs)
            foreach (var record in output.EnumerateMajorRecords()) missing.Remove(record.FormKey);
        if (missing.Count == 0) return missing;

        // Preserve the old union-of-all-records semantics, including losing and
        // deleted overrides and overrides whose owning plugin is absent.
        // Sources are read in parallel (concurrent reads of the missing set are safe);
        // each group records the missing keys it holds, or its failure, and a source
        // stops at its first failure. Replaying in source/group order then removes keys,
        // returns and throws exactly where the sequential scan did.
        var scans = new (FormKey[]? Found, Exception? Failure)[sources.Count][];
        Parallel.For(0, sources.Count, s =>
        {
            var results = new (FormKey[]? Found, Exception? Failure)[Groups.Length];
            for (var g = 0; g < Groups.Length; g++)
            {
                try { results[g] = (Groups[g](sources[s]).FormKeys.Where(missing.Contains).ToArray(), null); }
                catch (Exception ex) { results[g] = (null, ex); break; }
            }
            scans[s] = results;
        });
        for (var s = 0; s < sources.Count; s++)
        for (var g = 0; g < Groups.Length; g++)
        {
            var (keys, failure) = scans[s][g];
            if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
            foreach (var key in keys!) missing.Remove(key);
            if (missing.Count == 0) return missing;
        }

        // CELL/WRLD children, dialogue responses and other nested records need
        // traversal. Missing keys also take this path before any error is issued.
        using var nestedTiming = PhaseTiming.Measure("validation.reference-nested");
        foreach (var source in sources)
        foreach (var record in source.EnumerateMajorRecords())
        {
            missing.Remove(record.FormKey);
            if (missing.Count == 0) return missing;
        }
        return missing;
    }
}
