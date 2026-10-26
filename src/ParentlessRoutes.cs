using Mutagen.Bethesda.Plugins;

namespace XEnchantmentPatcher;

public static partial class PatcherEngine
{
    private static TypedLootUse[] ParentlessSourceUses(FormKey source, Catalog catalog)
        => catalog.DirectParents.GetValueOrDefault(source, [])
            .SelectMany(p => SpecializedGraph(catalog).Endpoints(p.List))
            .Concat(catalog.ScriptUnlocks.GetValueOrDefault(source, [])
                .SelectMany(u => SpecializedGraph(catalog).Endpoints(u.OriginalTarget)))
            .Where(u => u.Known).Distinct().ToArray();

    // A root cannot be isolated through an incoming LVLI. Use the existing semantic
    // context rules, not exact actor identity: higher-tier actors may carry only
    // enchanted equipment. Scope-only passengers cannot authorize a route alone.
    private static string? ParentlessConsumerProblem(FormKey source, FormKey target, Catalog catalog,
        TypedLootUse[]? sourceUses = null, bool controlled = false, Func<TypedLootUse, bool>? observedMatch = null)
    {
        var destinations = SpecializedGraph(catalog).Endpoints(target);
        if (destinations.Length == 0) return "No recognized live consumer for this parentless list";
        sourceUses ??= ParentlessSourceUses(source, catalog);
        bool Matches(TypedLootUse d) => observedMatch?.Invoke(d) ?? MatchesSpecializedUse(d, sourceUses, controlled);
        var conflict = destinations.FirstOrDefault(d => !d.Known || (!d.ScopeOnly && !Matches(d)));
        if (conflict is not null)
            return $"Consumer {conflict.Consumer} ({conflict.Role}, {conflict.Context}) "
                + (!conflict.Known ? "has unresolved consumer evidence" : "has no compatible source context or NPC template family")
                + "; parentless lists cannot isolate this consumer through an LVLI ancestor";
        return destinations.Any(Matches) ? null
            : "No positively matched source context for this parentless list; actor-specific passengers alone do not authorize distribution";
    }

    // A parentless direct entry's level is split in two. The destination/proof half: the
    // highest of every destination's lowest matching proof level, or the first problem. It
    // does not read the target or the tier level, so callers memoise it more coarsely.
    private static (short? Floor, string? Problem) ParentlessDirectFloor(IReadOnlyList<MaterialSourceProof> proofs,
        IEnumerable<TypedLootUse> destinations, TypedLootUse[] uses, MaterialSourceMatcher matcher)
    {
        short? floor = null;
        foreach (var destination in destinations)
        {
            if (destination.ScopeOnly && !MatchesSpecializedUse(destination, uses, false)) continue;
            // Every proof is still tested (the matcher memoises each verdict); only the
            // lowest matching level is kept, without an array per destination.
            short? lowest = null;
            foreach (var proof in proofs)
            {
                if (proof.Availability.Controls.Length != 0 || proof.Unlock is not null || !matcher.Matches(destination, proof)) continue;
                if (lowest is null || proof.Level < lowest.Value) lowest = proof.Level;
            }
            if (lowest is null) return (null, "Source availability needs a controlled family or an unsupported source structure; a flat direct entry cannot preserve it");
            floor = floor is null ? lowest : Math.Max(floor.Value, lowest.Value);
        }
        return floor is null ? (null, "No supported source availability context for the parentless destination") : (floor, null);
    }

    // The target/tier half: the template level raised to the floor, refused where a
    // highest-eligible target would change selection.
    private static (short? Level, string? Problem) ParentlessDirectLevelAt((short? Floor, string? Problem) floor, FormKey target,
        short templateLevel, Catalog catalog)
    {
        if (floor.Floor is not { } minimum) return (null, floor.Problem);
        var level = Math.Max(templateLevel, minimum);
        if (((int)catalog.Lists[target].Flags & 1) == 0 && level > templateLevel)
            return (null, $"Source minimum {level} exceeds mirrored tier {templateLevel}; raising a direct entry would change highest-eligible selection");
        return (level, null);
    }
}
