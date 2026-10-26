using Mutagen.Bethesda.Plugins;

namespace XEnchantmentPatcher;

public static partial class PatcherEngine
{
    private static IEnumerable<FormKey> RuntimeQuestHandles(ScriptInstance instance) =>
        instance.VmadHandles.Select(p => p.Record)
            .Concat(instance.AliasIdentity is { } alias ? new[] { alias.Quest } : []);

    // A public Auto interface remains an obligation even if the holder never
    // reads it. Native use closes an expression, not that public interface.
    // This conservative finite slice deliberately does not invent a negative
    // cut from an unused token, readonly qualifier or an in-progress cycle.
    private static IReadOnlyList<ScriptInstance> CloseRuntimeIncomingFrame(FormKey owner,
        IReadOnlyList<ScriptInstance> initial, Dictionary<FormKey, ScriptInstance[]> holders,
        Dictionary<FormKey, ScriptInstance[]> incoming, Action<ScriptInstance> load, Catalog catalog)
    {
        var rows = new HashSet<ScriptInstance>(); var visited = new HashSet<FormKey>();
        var pending = new Queue<(FormKey Key, int Depth)>(); pending.Enqueue((owner, 0));
        foreach (var holder in initial.Select(i => i.Quest.FormKey).Distinct()) pending.Enqueue((holder, 0));
        string? problem = null;
        while (pending.TryDequeue(out var current))
        {
            if (visited.Contains(current.Key)) continue;
            if (current.Depth > 16 || visited.Count >= 8192)
            { problem = $"incoming public-interface closure at {current.Key} exceeds 16 edges or 8192 states"; break; }
            if (holders.TryGetValue(current.Key, out var scripts))
            {
                if (rows.Count + scripts.Count(i => !rows.Contains(i)) > 512)
                { problem = $"incoming public-interface closure at {current.Key} exceeds 512 script instances"; break; }
                foreach (var script in scripts) rows.Add(script);
            }
            visited.Add(current.Key);
            foreach (var caller in incoming.GetValueOrDefault(current.Key, []))
                pending.Enqueue((caller.Quest.FormKey, current.Depth + 1));
        }
        if (problem is not null)
        {
            catalog.RuntimeFrameClosureProblems[owner] = problem;
            // No partial acceptance or expensive decompilation of a failed
            // component. Original bound holders remain tainted by the caller.
            return initial;
        }
        var result = rows.OrderBy(i => i.Identity, StringComparer.Ordinal).ToArray();
        foreach (var instance in result) load(instance);
        return result;
    }
}
