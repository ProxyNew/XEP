using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;

namespace XEnchantmentPatcher;

// Slice value flow (ADR 0009), one analysis per mod (ADR 0008). Within the mod's own
// scripts it follows every value that can reach a block's lists, carriers, the mod's
// quest handles or `self` through properties, script variables, locals, parameters,
// returns, array elements, casts, member reads, inherited bodies and calls into the
// mod's own scripts. Calls into another mod's scripts count as proved without looking.
// Each use is checked against the contract's admitted operations. An unproved use
// becomes an operation that taints only the lists its value can reach; list changes
// through other handles become effects that admission treats like the source's own
// resets and insertions.
public static partial class PatcherEngine
{
    // Kind: list, carrier, quest or alias; unknown is a handle of unknown origin.
    private sealed record SliceValue(string Kind, string KeyText, string View = "", bool Array = false)
    {
        private sealed record ParsedKeys(ImmutableArray<FormKey> Values);
        private static readonly ConditionalWeakTable<string, ParsedKeys> parsed = new();
        // Keep memo state out of value equality and with-copy state. The text is
        // immutable, and consumers cannot modify the parsed keys shared by copies.
        public ImmutableArray<FormKey> Keys => KeyText.Length == 0 ? []
            : parsed.GetValue(KeyText, static text => new(text.Split('|').Select(k => FormKey.Factory(k)).ToImmutableArray())).Values;
        public static SliceValue Of(string kind, IEnumerable<FormKey> keys, string view = "", bool array = false) =>
            new(kind, string.Join('|', keys.Distinct().Select(k => k.ToString()).Order(StringComparer.Ordinal)), view, array);
    }
    // Mutation marks a change to a list's entries or rules through a handle the analysis
    // cannot resolve, the only kind of unproved operation combined from another mod.
    private sealed record SliceOp(string Body, string Label, string Function, string Line, string Problem, FormKey[]? Reach, bool Mutation = false);
    private sealed record SliceEffect(string Body, string Function, string Method, FormKey[] Lists, int Position, string Line);
    private readonly record struct SliceToken(string Value, int Index);
    private sealed record SliceLine(string Raw, string Masked, int Position, SliceToken[] Tokens);

    private static SliceLine[] SliceLines(ScriptFunction function)
    {
        using var timing = PhaseTiming.Measure("slice-flow.body.line-syntax");
        var lines = new List<SliceLine>(); var offset = 0;
        foreach (var raw in function.Body.Split('\n'))
        {
            var position = function.BodyStart + offset; offset += raw.Length + 1;
            var masked = MaskScriptStrings(raw);
            if (masked.Trim().Length == 0) continue;
            var tokens = Regex.Matches(masked, @"(?<![\w.])(?<!\.\s+)[A-Za-z_]\w*\b")
                .Select(token => new SliceToken(token.Value, token.Index)).ToArray();
            lines.Add(new(raw, masked, position, tokens));
        }
        return lines.ToArray();
    }
    private sealed class SliceBody
    {
        public required string Identity { get; init; }
        public required string Label { get; init; }
        // The mod whose analysis this body belongs to.
        public required ModKey Mod { get; init; }
        public required string Text { get; init; }
        public required List<ScriptFunction> Functions { get; init; }
        public Dictionary<string, FormKey> Objects { get; init; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, FormKey[]> Arrays { get; init; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, (FormKey Quest, short Alias)> AliasBindings { get; init; } = new(StringComparer.OrdinalIgnoreCase);
        public FormKey? SelfQuest { get; init; }
        public FormKey? OwningQuest { get; init; }
        public string[] Chain { get; init; } = [];
        // Records whose source scope decides refusal or warning (ADR 0008).
        public FormKey[] Owners { get; init; } = [];
        public ScriptInstance? Instance { get; init; }
        public string? Problem { get; init; }
        private SliceBodySyntax? syntax;
        public SliceBodySyntax Syntax => syntax ??= new(this);

        public void ReuseSyntax(SliceBody owner) => syntax = owner.Syntax;
    }
    private sealed class SliceBodySyntax(SliceBody body)
    {
        public string Masked => TextSyntax(body.Text).Masked;
        // Syntax belongs to this complete body/chain, never to a name shared by
        // another source. Pass copies share the body; fresh analyses build new ones.
        private readonly Dictionary<ScriptFunction, SliceLine[]> lines = new(ScriptFunctionKeys);
        private readonly Dictionary<(ScriptFunction, string), string?> declared = new(ScriptFunctionNameKeys);
        private readonly Dictionary<(ScriptFunction, string), bool> local = new(ScriptFunctionNameKeys);

        public SliceLine[] Lines(ScriptFunction function)
        {
            if (!lines.TryGetValue(function, out var known)) lines[function] = known = SliceLines(function);
            return known;
        }
        public string? Declared(ScriptFunction function, string name)
        {
            var key = (function, name.ToLowerInvariant());
            if (!declared.TryGetValue(key, out var known))
            {
                using var timing = PhaseTiming.Measure("slice-flow.body.declaration-syntax");
                declared[key] = known =
                    Regex.Match(function.Body, @"(?im)^[ \t]*(?!(?:return|if|elseif|while)\b)(\w+)(\[\])?[ \t]+" + Regex.Escape(name) + @"[ \t]*(?:=|\r?$)") is { Success: true } variable ? variable.Groups[1].Value + variable.Groups[2].Value
                    : Regex.Match(function.Parameters, @"(?i)(?:^|,)\s*(\w+)(\[\])?\s+" + Regex.Escape(name) + @"\b") is { Success: true } parameter ? parameter.Groups[1].Value + parameter.Groups[2].Value
                    : Regex.Match(Masked, @"(?im)^[ \t]*(\w+)(\[\])?[ \t]+(?:property[ \t]+)?" + Regex.Escape(name) + @"\b") is { Success: true } member ? member.Groups[1].Value + member.Groups[2].Value : null;
            }
            return known;
        }
        public bool Local(ScriptFunction function, string name)
        {
            var key = (function, name.ToLowerInvariant());
            if (!local.TryGetValue(key, out var known))
            {
                using var timing = PhaseTiming.Measure("slice-flow.body.shadow-syntax");
                local[key] = known = HasLocalShadow(function, name);
            }
            return known;
        }
    }

    private static SliceBody ReuseSliceBodySyntax(SliceBody body, Catalog catalog)
    {
        lock (catalog.SliceLock)
        {
            var key = (body.Mod, body.Identity);
            if (catalog.SliceSyntaxBodies.TryGetValue(key, out var owner)
                && owner.Text == body.Text && owner.Functions.SequenceEqual(body.Functions)
                && owner.Chain.SequenceEqual(body.Chain) && owner.Owners.SequenceEqual(body.Owners))
                body.ReuseSyntax(owner);
            else catalog.SliceSyntaxBodies[key] = body;
            return body;
        }
    }
    private sealed class SliceFlowState(ModKey mod)
    {
        public ModKey Mod { get; } = mod;
        public Dictionary<string, SliceBody> Bodies { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, HashSet<(string Function, int Parameter, SliceValue Value)>> Seeds { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, (List<SliceOp> Ops, List<SliceEffect> Effects, Dictionary<string, SliceValue> Returns)> Results { get; } = new(StringComparer.Ordinal);
        private readonly Dictionary<FormKey, HashSet<FormKey>> carriedLists = [];
        private Dictionary<FormKey, FormKey[]>? carriersByList;
        public IEnumerable<FormKey> CarriedBy(FormKey carrier) => carriedLists.GetValueOrDefault(carrier, []);
        public IEnumerable<FormKey> CarriersOf(FormKey list)
        {
            // Preserve the forward dictionary's carrier order, including when
            // an earlier carrier acquires another list later in the fixed point.
            carriersByList ??= carriedLists.SelectMany(p => p.Value.Select(k => (List: k, Carrier: p.Key)))
                .GroupBy(p => p.List).ToDictionary(g => g.Key, g => g.Select(p => p.Carrier).ToArray());
            return carriersByList.GetValueOrDefault(list, []);
        }
        public void AddCarriedLists(FormKey carrier, IEnumerable<FormKey> lists)
        {
            // An empty value still establishes the carrier's traversal position.
            if (!carriedLists.TryGetValue(carrier, out var rows)) carriedLists[carrier] = rows = [];
            foreach (var list in lists)
            {
                if (!rows.Add(list)) continue;
                carriersByList = null;
                CarriersChanged = true;
            }
        }
        // Operations by reachable list; the null key holds operations that reach any list.
        public Dictionary<FormKey, List<SliceOp>>? Index { get; set; }
        public Dictionary<SliceValue, FormKey[]?> Reach { get; } = [];
        // Carried lists grew: cached reaches and recorded operations are stale.
        public bool CarriersChanged { get; set; }
        // Bodies that received a new parameter seed since they were last analysed.
        public HashSet<string> Dirty { get; } = new(StringComparer.Ordinal);
        // The fixpoint exceeded its bound: no verdict may rely on it.
        public bool Incomplete { get; set; }

        public SliceFlowState CopyForPass()
        {
            var copy = new SliceFlowState(Mod) { CarriersChanged = CarriersChanged, Incomplete = Incomplete,
                Index = Index?.ToDictionary(x => x.Key, x => x.Value.ToList()) };
            foreach (var (key, value) in Bodies) copy.Bodies[key] = value;
            foreach (var (key, value) in Seeds) copy.Seeds[key] = new(value);
            foreach (var (key, value) in Results) copy.Results[key] = (value.Ops.ToList(), value.Effects.ToList(), new(value.Returns));
            foreach (var (key, value) in carriedLists) copy.carriedLists[key] = new(value);
            foreach (var (key, value) in Reach) copy.Reach[key] = value;
            copy.Dirty.UnionWith(Dirty);
            return copy;
        }
    }

    private static readonly HashSet<string> SliceReadMethods = new(StringComparer.OrdinalIgnoreCase) { "GetChanceNone", "GetNumForms", "GetNthLevel", "GetNthCount" };
    private static readonly HashSet<string> SliceMutationMethods = new(StringComparer.OrdinalIgnoreCase)
        { "AddForm", "Revert", "RemoveAddedForm", "SetChanceNone", "SetChanceGlobal", "SetNthLevel", "SetNthCount" };
    // Quest natives admitted on a slice quest handle besides the pure queries: stage,
    // lifecycle and objective changes only run the quest's own analysed scripts or
    // change journal state; none reads or rebinds a bound property.
    private static readonly HashSet<string> SliceQuestStateMethods = new(StringComparer.OrdinalIgnoreCase)
    {
        "SetStage", "SetCurrentStageID", "Start", "Stop", "Reset", "SetActive", "SetObjectiveDisplayed", "SetObjectiveCompleted",
        "SetObjectiveFailed", "IsObjectiveDisplayed", "IsObjectiveCompleted", "IsObjectiveFailed", "CompleteAllObjectives", "FailAllObjectives"
    };

    private static bool SliceQuestNative(string method, int arguments, Catalog catalog) =>
        HolderQueryMethods.Contains(method) ? QuestNativeContract(catalog, method, out _, out var exact) && arguments == exact
        : SliceQuestStateMethods.Contains(method) && QuestNativeContract(catalog, method, out _, out var count) && arguments <= count;

    private static SliceValue? SliceRecordValue(FormKey key, ModKey mod, Catalog catalog, bool array = false)
    {
        if (!SliceRecord(key, mod, catalog)) return null;
        if (catalog.Lists.ContainsKey(key)) return SliceValue.Of("list", [key], "", array);
        return RuntimeRecordWinners(catalog).GetValueOrDefault(key).Record is IQuestGetter
            ? SliceValue.Of("quest", [key], "Quest", array) : SliceValue.Of("carrier", [key], "", array);
    }

    // The lists an unproved use of this value can reach; null means any list.
    private static FormKey[]? SliceReach(SliceValue value, SliceFlowState state, Catalog catalog)
    {
        if (state.Reach.TryGetValue(value, out var known)) return known;
        return state.Reach[value] = SliceReachCore(value, state, catalog);
    }

    private static FormKey[]? SliceReachCore(SliceValue value, SliceFlowState state, Catalog catalog)
    {
        var lists = new HashSet<FormKey>(); var seen = new HashSet<FormKey>();
        void Carrier(FormKey carrier)
        {
            lock (catalog.SliceLock) catalog.RuntimeFormLists ??= catalog.Mods.SelectMany(m => m.FormLists).GroupBy(l => l.FormKey).ToDictionary(g => g.Key, g => g.Last());
            var pending = new Queue<(FormKey Key, int Depth)>(); pending.Enqueue((carrier, 0));
            while (pending.TryDequeue(out var current))
            {
                if (!seen.Add(current.Key) || current.Depth > 16) continue;
                if (catalog.Lists.ContainsKey(current.Key)) { lists.Add(current.Key); continue; }
                foreach (var carried in SliceFormListCarriers(state.Mod, catalog).Where(p => p.Value.Contains(current.Key)).Select(p => p.Key)) lists.Add(carried);
                foreach (var carried in state.CarriedBy(current.Key)) lists.Add(carried);
                if (catalog.RuntimeFormLists.TryGetValue(current.Key, out var collection))
                    foreach (var item in collection.Items) pending.Enqueue((item.FormKey, current.Depth + 1));
            }
        }
        void Quest(FormKey quest, int depth)
        {
            if (!seen.Add(quest) || depth > 16) return;
            foreach (var instance in SliceScopeInstances(state.Mod, catalog).Where(i => i.Quest.FormKey == quest))
                foreach (var key in instance.Objects.Values.Concat(instance.Arrays.Values.SelectMany(k => k)))
                    if (catalog.Lists.ContainsKey(key)) lists.Add(key);
                    else if (SliceQuests(state.Mod, catalog).Contains(key)) Quest(key, depth + 1);
                    else if (SliceRecord(key, state.Mod, catalog)) Carrier(key);
        }
        switch (value.Kind)
        {
            case "unknown": return null;
            case "list": return value.Keys.ToArray();
            case "carrier": foreach (var key in value.Keys) Carrier(key); break;
            default: foreach (var key in value.Keys) Quest(key, 0); break;
        }
        return lists.ToArray();
    }

    private static SliceBody SliceInstanceBody(ScriptInstance instance, ModKey mod, Catalog catalog)
    {
        var (view, problem) = SliceChainView(instance, catalog);
        var source = view ?? instance;
        return ReuseSliceBodySyntax(new SliceBody
        {
            Identity = "q:" + instance.Identity, Label = instance.Label, Mod = mod, Text = source.Text ?? "", Functions = source.Functions.ToList(),
            Objects = new(instance.Objects, StringComparer.OrdinalIgnoreCase), Arrays = new(instance.Arrays, StringComparer.OrdinalIgnoreCase),
            AliasBindings = new(instance.AliasBindings, StringComparer.OrdinalIgnoreCase),
            SelfQuest = instance.Alias ? null : instance.Quest.FormKey, OwningQuest = instance.Quest.FormKey, Chain = source.ChainNames,
            Owners = [instance.Quest.FormKey], Instance = instance,
            Problem = instance.Text is null ? $"script text is unavailable: {instance.Evidence}" : problem
        }, catalog);
    }

    // E2 for a non-Quest holder: custom parents run inside the holder's own object and
    // are analysed with it. The chain ends at the first game script, one that declares
    // native functions, whose code never sees the holder's own properties.
    private static (string? Text, string[] Names, string? Problem) SliceHolderChain(string name, Catalog catalog)
    {
        var asset = catalog.RuntimeHolderAssetResolver?.Invoke(name);
        if (asset?.Text is not { } text) return (null, [name], $"script text is unavailable: {asset?.Evidence}");
        var names = new List<string> { name }; var combined = text;
        for (var parent = ScriptParent(text); parent.Length > 0;)
        {
            if (names.Contains(parent, StringComparer.OrdinalIgnoreCase)) return (null, names.ToArray(), "custom inheritance is cyclic at " + parent);
            if (names.Count > 16) return (null, names.ToArray(), "custom inheritance exceeds 16 scripts");
            var parentText = catalog.RuntimeHolderAssetResolver?.Invoke(parent).Text;
            if (parentText is null) return (null, names.ToArray(), $"custom parent {parent} has no winning script asset");
            if (Regex.IsMatch(MaskScriptStrings(parentText), @"(?im)^[ \t]*(?:\w+(?:\[\])?[ \t]+)?function[ \t]+\w+[ \t]*\([^)\n]*\)[^\n;]*\bnative\b")) break;
            names.Add(parent);
            combined += "\n" + Regex.Replace(parentText, @"(?im)^[^\S\n]*scriptname\b[^\n]*", m => new string(' ', m.Length));
            if (combined.Length > 1_000_000) return (null, names.ToArray(), "custom inheritance chain exceeds the text bound");
            parent = ScriptParent(parentText);
        }
        return (combined, names.ToArray(), null);
    }

    private static SliceBody SliceHolderBody(FormKey record, int slot, IScriptEntryGetter entry, ModKey mod, Catalog catalog)
    {
        var objects = new Dictionary<string, FormKey>(StringComparer.OrdinalIgnoreCase);
        var arrays = new Dictionary<string, FormKey[]>(StringComparer.OrdinalIgnoreCase);
        var aliases = new Dictionary<string, (FormKey Quest, short Alias)>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in entry.Properties)
            if (property is IScriptObjectPropertyGetter handle)
            { if (handle.Alias == -1) objects[property.Name] = handle.Object.FormKey; else aliases[property.Name] = (handle.Object.FormKey, handle.Alias); }
            else if (property is IScriptObjectListPropertyGetter list) arrays[property.Name] = list.Objects.Select(o => o.Object.FormKey).ToArray();
        var (text, names, problem) = SliceHolderChain(entry.Name, catalog);
        // Alias array elements are not followed: holding a slice quest's alias makes every
        // use of that quest's values unproved (the array carries the quest itself).
        if (problem is null && entry.Properties.OfType<IScriptObjectListPropertyGetter>().FirstOrDefault(l => l.Objects.Any(o => o.Alias != -1
            && SliceRecord(o.Object.FormKey, mod, catalog))) is { } aliasArray) problem = $"alias array {aliasArray.Name} is outside the slice flow";
        return ReuseSliceBodySyntax(new SliceBody
        {
            Identity = $"h:{record}/{slot}/{entry.Name}", Label = $"{record}/{entry.Name}", Mod = mod, Text = text ?? "", Functions = text is null ? [] : ParseFunctions(text),
            Objects = objects, Arrays = arrays, AliasBindings = aliases, Owners = [record], Chain = names, Problem = problem
        }, catalog);
    }

    // One of the mod's own scripts reached only through a call on a typed handle.
    private static SliceBody SliceLibraryBody(string script, ModKey mod, Catalog catalog)
    {
        var (text, names, problem) = SliceHolderChain(script, catalog);
        return ReuseSliceBodySyntax(new SliceBody
        {
            Identity = "l:" + script, Label = script, Mod = mod, Text = text ?? "", Functions = text is null ? [] : ParseFunctions(text),
            Owners = SliceScriptAttachments(catalog).GetValueOrDefault(script, []).Where(r => InSliceScope(r, mod, catalog)).ToArray(), Chain = names, Problem = problem
        }, catalog);
    }

    // A script attached to a record of this mod; calls into any other script are another
    // mod's code, which the mod's analysis counts as proved without looking (ADR 0008).
    private static bool SliceOwnScript(string script, ModKey mod, Catalog catalog) =>
        SliceScriptAttachments(catalog).GetValueOrDefault(script, []).Any(r => InSliceScope(r, mod, catalog));

    // The mod's own analysis: its quest scripts with their chains and its non-Quest
    // holders of slice values. No other mod's script enters it.
    private static SliceFlowState EnsureSliceFlow(ModKey mod, Catalog catalog)
    {
        if (catalog.SliceFlows.TryGetValue(mod, out var state)) return state;
        state = catalog.SliceFlows[mod] = new SliceFlowState(mod);
        AnalyseSliceFlow(state, catalog);
        return state;
    }

    // Placed actors, for AddItem receivers. One index, built on first use.
    private static Dictionary<FormKey, bool> SliceActorReferences(Catalog catalog)
    {
        lock (catalog.SliceLock)
            return catalog.RuntimeActorReferences ??= catalog.Mods
                .SelectMany(m => m.EnumerateMajorRecords<IPlacedNpcGetter>()).GroupBy(r => r.FormKey).Where(g => !g.Last().IsDeleted && !g.Last().Base.IsNull)
                .ToDictionary(g => g.Key, g => true);
    }

    // Every listed mod's flow, as EnsureSliceFlow computes it. A mod's analysis never
    // reads another mod's flow, so missing flows run in parallel; their shared inputs
    // are loaded first in this order, memo tables publish under SliceLock, and states
    // are published in this order.
    private static void EnsureSliceFlows(IReadOnlyList<ModKey> mods, Catalog catalog)
    {
        var missing = mods.Distinct().Where(m => !catalog.SliceFlows.ContainsKey(m)).ToArray();
        if (missing.Length < 2) { foreach (var mod in missing) EnsureSliceFlow(mod, catalog); return; }
        using var timing = PhaseTiming.Measure("slice-flow.parallel");
        RuntimeRecordWinners(catalog);
        SliceScriptAttachments(catalog);
        foreach (var mod in missing) SliceScopeInstances(mod, catalog);
        var states = missing.Select(mod => new SliceFlowState(mod)).ToArray();
        using (PhaseTiming.Detach())
            Parallel.For(0, states.Length, new ParallelOptions { MaxDegreeOfParallelism = Math.Min(4, Environment.ProcessorCount) },
                i => AnalyseSliceFlow(states[i], catalog));
        foreach (var state in states) catalog.SliceFlows.Add(state.Mod, state);
    }

    private static void AnalyseSliceFlow(SliceFlowState state, Catalog catalog)
    {
        var mod = state.Mod;
        using var timing = PhaseTiming.Measure("slice-flow");
        var bodies = SliceScopeInstances(mod, catalog).OrderBy(i => i.Identity, StringComparer.Ordinal).Select(i => SliceInstanceBody(i, mod, catalog)).ToList();
        var winners = RuntimeRecordWinners(catalog);
        foreach (var record in SliceScriptAttachments(catalog).Values.SelectMany(r => r).Distinct().Where(r => InSliceScope(r, mod, catalog)).Order())
        {
            if (!winners.TryGetValue(record, out var winner) || winner.Record.IsDeleted || winner.Record is IQuestGetter
                || winner.Record is not IHaveVirtualMachineAdapterGetter { VirtualMachineAdapter: { } adapter }) continue;
            foreach (var (entry, slot) in adapter.Scripts.Select((s, i) => (s, i)))
                if (entry.Properties.Any(p => p is IScriptObjectPropertyGetter h && SliceRecord(h.Object.FormKey, mod, catalog)
                    || p is IScriptObjectListPropertyGetter l && l.Objects.Any(o => SliceRecord(o.Object.FormKey, mod, catalog))))
                    bodies.Add(SliceHolderBody(record, slot, entry, mod, catalog));
        }
        SliceFlowAnalyse(state, bodies, catalog);
    }

    // Operations of a mod's analysis by reachable list; the null key holds operations that reach any list.
    private static Dictionary<FormKey, List<SliceOp>> SliceOpIndex(SliceFlowState state)
    {
        if (state.Index is { } built) return built;
        var index = new Dictionary<FormKey, List<SliceOp>>();
        foreach (var op in state.Results.Values.SelectMany(r => r.Ops))
            foreach (var list in op.Reach ?? [default])
            {
                if (!index.TryGetValue(list, out var rows)) index[list] = rows = [];
                rows.Add(op);
            }
        return state.Index = index;
    }

    // The first operation the source mod's own analysis leaves unproved on these lists.
    // Every other patched mod is analysed on its own; only its changes to the lists
    // through handles it cannot resolve are combined, as warnings naming the script and line.
    private static string? SliceFlowProblem(IEnumerable<FormKey> lists, ModKey source, Catalog catalog, List<string> warnings)
    {
        var targets = lists.Distinct().ToArray();
        IEnumerable<SliceOp> Reaching(SliceFlowState state) =>
            targets.SelectMany(l => SliceOpIndex(state).GetValueOrDefault(l, [])).Concat(SliceOpIndex(state).GetValueOrDefault(default, [])).Distinct();
        string Where(SliceOp op) => $"{op.Label}{(op.Function.Length == 0 ? "" : "." + op.Function)}";
        var own = EnsureSliceFlow(source, catalog);
        if (own.Incomplete) return "slice flow analysis exceeds its bound";
        if (Reaching(own).FirstOrDefault() is { } first) return $"{Where(first)}: {first.Problem}{(first.Line.Length == 0 ? "" : $" at `{first.Line}`")}";
        EnsureSliceFlows(SliceCombinedMods(source, catalog).Where(m => m != source).ToArray(), catalog);
        foreach (var mod in SliceCombinedMods(source, catalog).Where(m => m != source))
        {
            var other = EnsureSliceFlow(mod, catalog);
            // The source's own scope was proved above; a record both mods own is its code.
            foreach (var op in Reaching(other).Where(o => o.Mutation && !(other.Bodies.GetValueOrDefault(o.Body)?.Owners ?? []).Any(r => InSliceScope(r, source, catalog))))
                warnings.Add($"{Where(op)} in patched mod {mod.FileName} changes a shared list through a handle its own analysis cannot resolve at `{op.Line}`; affected gates resync at load and initialization after it runs.");
            if (other.Incomplete)
                warnings.Add($"The slice analysis of patched mod {mod.FileName} exceeds its bound; affected gates resync at load and initialization after its scripts run.");
        }
        return null;
    }

    // Analyses bodies until no new values, parameters or returns appear.
    private static void SliceFlowAnalyse(SliceFlowState state, IEnumerable<SliceBody> bodies, Catalog catalog)
    {
        using var timing = PhaseTiming.Measure("slice-flow.fixed-point");
        var queue = new Queue<string>(); var queued = new HashSet<string>(StringComparer.Ordinal);
        void Enqueue(string identity) { if (queued.Add(identity)) queue.Enqueue(identity); }
        foreach (var body in bodies)
            if (state.Bodies.TryAdd(body.Identity, body) || !state.Results.ContainsKey(body.Identity)) Enqueue(body.Identity);
        var analyses = 0;
        while (queue.TryDequeue(out var identity))
        {
            queued.Remove(identity);
            if (++analyses > 200_000) { state.Incomplete = true; break; }
            var oldReturns = state.Results.GetValueOrDefault(identity).Returns;
            state.Results[identity] = SliceFlowBody(state.Bodies[identity], state, catalog);
            // Only bodies that call a function whose slice return changed can use its new result.
            var changedReturns = state.Results[identity].Returns.Where(p => oldReturns?.GetValueOrDefault(p.Key) != p.Value).Select(p => p.Key).ToArray();
            if (changedReturns.Length > 0)
            {
                var calls = new Regex(@"(?i)\b(?:" + string.Join('|', changedReturns.Select(Regex.Escape)) + @")\s*\(");
                foreach (var (other, candidate) in state.Bodies)
                    if (other != identity && calls.IsMatch(candidate.Syntax.Masked)) Enqueue(other);
            }
            foreach (var target in state.Dirty) Enqueue(target);
            state.Dirty.Clear();
            // A newly carried list widens carrier reaches: recompute them in every body.
            if (state.CarriersChanged)
            {
                state.CarriersChanged = false; state.Reach.Clear();
                foreach (var other in state.Bodies.Keys) Enqueue(other);
            }
        }
        state.Index = null;
    }

    private static (List<SliceOp> Ops, List<SliceEffect> Effects, Dictionary<string, SliceValue> Returns) SliceFlowBody(SliceBody body, SliceFlowState state, Catalog catalog)
    {
        using var timing = PhaseTiming.Measure("slice-flow.body");
        var ops = new List<SliceOp>(); var effects = new List<SliceEffect>();
        var returns = new Dictionary<string, SliceValue>(StringComparer.OrdinalIgnoreCase);
        var script = new Dictionary<string, SliceValue>(StringComparer.OrdinalIgnoreCase);
        // A quest handle's view is its declared type: Quest, or a script attached to it.
        string? PropertyType(string name) => Regex.Match(body.Syntax.Masked, @"(?im)^[ \t]*(\w+)(?:\[\])?[ \t]+property[ \t]+" + Regex.Escape(name) + @"\b") is { Success: true } m ? m.Groups[1].Value : null;
        foreach (var (name, key) in body.Objects)
            if (SliceRecordValue(key, body.Mod, catalog) is { } value) script[name] = value.Kind == "quest" ? value with { View = PropertyType(name) ?? "Quest" } : value;
        foreach (var (name, keys) in body.Arrays)
        {
            var values = keys.Select(k => SliceRecordValue(k, body.Mod, catalog)).OfType<SliceValue>().ToArray();
            if (values.Length > 0) script[name] = SliceValue.Of(values.Select(v => v.Kind).Distinct().Count() == 1 ? values[0].Kind : "unknown", values.SelectMany(v => v.Keys), "", true);
        }
        foreach (var (name, alias) in body.AliasBindings) if (SliceRecord(alias.Quest, body.Mod, catalog)) script[name] = SliceValue.Of("alias", [alias.Quest]);
        var self = body.SelfQuest is { } quest && SliceRecord(quest, body.Mod, catalog) ? SliceValue.Of("quest", [quest], body.Chain.FirstOrDefault() ?? "") : null;
        var properties = new HashSet<string>(Regex.Matches(body.Syntax.Masked, @"(?im)^[ \t]*\w+(?:\[\])?[ \t]+property[ \t]+(\w+)").Select(m => m.Groups[1].Value), StringComparer.OrdinalIgnoreCase);
        void Op(ScriptFunction? function, string line, string problem, SliceValue? value, bool mutation = false)
            => ops.Add(new(body.Identity, body.Label, function?.Name ?? "", line.Trim(), problem, value is null ? null : SliceReach(value, state, catalog), mutation));
        if (body.Problem is not null)
        {
            // Unreadable code is an unproved operation on every slice value it holds.
            foreach (var value in script.Values.Append(self).OfType<SliceValue>()) Op(null, "", body.Problem, value);
            return (ops, effects, returns);
        }
        if (body.Text.Length > 1_000_000 || body.Functions.Count > 512)
        {
            foreach (var value in script.Values.Append(self).OfType<SliceValue>()) Op(null, "", "script exceeds the analysis bound", value);
            return (ops, effects, returns);
        }
        var locals = body.Functions.ToDictionary(f => f, _ => new Dictionary<string, SliceValue>(StringComparer.OrdinalIgnoreCase), ScriptFunctionKeys);
        SliceValue? BoundValue(ScriptFunction function, string name) => locals[function].GetValueOrDefault(name)
            ?? (script.TryGetValue(name, out var property) && !body.Syntax.Local(function, name) ? property : null);
        foreach (var (function, parameter, value) in state.Seeds.GetValueOrDefault(body.Identity, []))
            foreach (var target in body.Functions.Where(f => f.Name.Equals(function, StringComparison.OrdinalIgnoreCase)))
                if (SplitArguments(target.Parameters) is var parameters && parameter < parameters.Length
                    && Regex.Match(parameters[parameter], @"^\s*\w+(?:\[\])?\s+(\w+)") is { Success: true } name)
                    locals[target][name.Groups[1].Value] = value;
        SliceValue Merge(SliceValue? old, SliceValue value) => old is null ? value
            : SliceValue.Of(old.Kind == value.Kind ? old.Kind : "unknown", old.Keys.Concat(value.Keys), old.View == value.View ? old.View : "", old.Array || value.Array);
        var changed = true;
        bool Track(Dictionary<string, SliceValue> scope, string name, SliceValue value)
        {
            var merged = Merge(scope.GetValueOrDefault(name), value);
            if (scope.GetValueOrDefault(name) == merged) return false;
            scope[name] = merged; changed = true; return true;
        }
        void SeedCall(SliceBody target, string function, int parameter, SliceValue value)
        {
            state.Bodies.TryAdd(target.Identity, target);
            if (!state.Seeds.TryGetValue(target.Identity, out var seeds)) state.Seeds[target.Identity] = seeds = [];
            if (seeds.Add((function, parameter, value))) state.Dirty.Add(target.Identity);
        }
        // The body that a quest value's view dispatches to: self's own chain, or the
        // script attached to that quest whose chain declares the view.
        var targets = new Dictionary<SliceValue, SliceBody?>();
        SliceBody? Target(SliceValue quest)
        {
            if (!targets.TryGetValue(quest, out var known)) targets[quest] = known = TargetCore(quest);
            return known;
        }
        SliceBody? TargetCore(SliceValue quest)
        {
            if (self is not null && quest.Keys.SequenceEqual(self.Keys) && (quest.View.Length == 0 || body.Chain.Contains(quest.View, StringComparer.OrdinalIgnoreCase))) return body;
            if (quest.Keys.Length != 1 || quest.View.Equals("Quest", StringComparison.OrdinalIgnoreCase)) return null;
            var instance = SliceScopeInstances(body.Mod, catalog).FirstOrDefault(i => !i.Alias && i.Quest.FormKey == quest.Keys[0]
                && SliceChainView(i, catalog).View?.ChainNames.Contains(quest.View, StringComparer.OrdinalIgnoreCase) == true);
            if (instance is null) return null;
            return state.Bodies.GetValueOrDefault("q:" + instance.Identity) ?? SliceInstanceBody(instance, body.Mod, catalog);
        }
        SliceValue? Returned(SliceBody target, string function) =>
            state.Results.GetValueOrDefault(target.Identity).Returns?.GetValueOrDefault(function) ?? (target == body ? returns.GetValueOrDefault(function) : null);
        bool Defines(SliceBody target, string function) => target.Functions.Any(f => f.Name.Equals(function, StringComparison.OrdinalIgnoreCase));
        bool Primitive(SliceBody target, string function) => target.Functions.Where(f => f.Name.Equals(function, StringComparison.OrdinalIgnoreCase))
            .All(f => f.ReturnType.Length == 0 || SlicePrimitive(f.ReturnType));
        bool NativeSelf(string method, string[] args, ScriptFunction f)
        {
            var name = method.ToLowerInvariant();
            if (name is "getstate" or "gotostate") return !Defines(body, method);
            if (name == "getformid") return NativeFrameContract(catalog, "Form", "GetFormID", "Int", [], false);
            if (name == "registerformodevent") return NativeFrameContract(catalog, "Form", "RegisterForModEvent", "", ["String", "String"], false);
            if (name == "unregisterformodevent") return NativeFrameContract(catalog, "Form", "UnregisterForModEvent", "", ["String"], false);
            if (name == "unregisterforallmodevents") return NativeFrameContract(catalog, "Form", "UnregisterForAllModEvents", "", [], false);
            if (name is "registerforsingleupdate" or "registerforsingleupdategametime" or "unregisterforupdate" or "unregisterforupdategametime" or "registerforupdate" or "registerforupdategametime")
                return !Defines(body, method) && NativeFrameContract(catalog, "Form", method, "", name.StartsWith("unregister") ? [] : ["Float"], false);
            if (name == "sendmodevent") return !Defines(body, method) && NativeSendModEventContract(catalog, body.Instance?.Alias == true);
            return SliceQuestNative(method, args.Length, catalog);
        }
        // Classifies the value expression starting at [start, end) in a masked line.
        void Use(ScriptFunction f, string line, string raw, int position, int start, int end, SliceValue value, bool direct, string? baseName)
        {
            var pos = end;
            if (value.Array)
            {
                var index = Regex.Match(line[pos..], @"^\s*\[");
                if (index.Success)
                {
                    var depth = 0; var i = pos + index.Length - 1;
                    for (; i < line.Length; i++) { if (line[i] == '[') depth++; else if (line[i] == ']' && --depth == 0) break; }
                    if (i >= line.Length) { Op(f, raw, "unparsed array element", value); return; }
                    pos = i + 1; value = value with { Array = false }; direct = false;
                }
                else if (Regex.IsMatch(line[pos..], @"(?i)^\s*\.\s*length\b")) return;
            }
            // A grouping parenthesis, not a call's argument list.
            var castOpen = Regex.Match(line[..start], @"(?i)(?:(?<![\w\])]\s*)|(?<=(?:^|[^\w.])(?:if|elseif|while|return)\s*))\(\s*$");
            var castTail = Regex.Match(line[pos..], @"(?i)^\s+as\s+(\w+)\s*\)");
            if (castOpen.Success && castTail.Success)
            {
                var type = castTail.Groups[1].Value;
                if (Regex.IsMatch(type, "(?i)^(?:bool|string)$")) return;
                if (value.Kind == "quest" && !type.Equals("Form", StringComparison.OrdinalIgnoreCase)) value = value with { View = type };
                start = castOpen.Index; pos += castTail.Length;
            }
            for (var guard = 0; guard < 64; guard++)
            {
                var tail = line[pos..];
                if (BalancedMemberCall(tail) is { } call)
                {
                    var args = SplitArguments(call.Args);
                    var statement = Regex.IsMatch(line[..start], @"^\s*$") && Regex.IsMatch(tail[call.Length..], @"^\s*$");
                    var result = Method(f, raw, position, value, direct, call.Method, args, statement, line, pos + call.Length);
                    if (result is null) return;
                    value = result; direct = false; pos += call.Length; continue;
                }
                var member = Regex.Match(tail, @"^\s*\.\s*(\w+)\b(?!\s*\()");
                if (member.Success)
                {
                    var result = Member(f, raw, value, member.Groups[1].Value, tail[member.Length..]);
                    if (result is null) return;
                    value = result; direct = false; pos += member.Length; continue;
                }
                var cast = Regex.Match(tail, @"(?i)^\s+as\s+(\w+)\b");
                if (cast.Success)
                {
                    if (Regex.IsMatch(cast.Groups[1].Value, "(?i)^(?:bool|string)$")) return;
                    if (value.Kind == "quest" && !cast.Groups[1].Value.Equals("Form", StringComparison.OrdinalIgnoreCase)) value = value with { View = cast.Groups[1].Value };
                    pos += cast.Length; continue;
                }
                break;
            }
            Context(f, line, raw, start, pos, value, direct, baseName);
        }
        SliceValue? Method(ScriptFunction f, string raw, int position, SliceValue value, bool direct, string method, string[] args, bool statement, string line, int after)
        {
            var sliceArgs = args.Select((a, i) => (Index: i, Value: ArgumentValue(f, a))).Where(a => a.Value is not null).ToArray();
            switch (value.Kind)
            {
                case "quest":
                {
                    var target = Target(value);
                    if (target is not null && Defines(target, method))
                    {
                        foreach (var (index, argument) in sliceArgs) SeedCall(target, method, index, argument!);
                        if (Returned(target, method) is { } returned) return returned;
                        return statement || Primitive(target, method) ? null : Unproved($"call {method} returns an unanalysed handle");
                    }
                    if (target == body || target is null && self is not null && value.Keys.SequenceEqual(self.Keys))
                    {
                        if (NativeSelf(method, args, f) && sliceArgs.Length == 0) return null;
                    }
                    else if (SliceQuestNative(method, args.Length, catalog) && sliceArgs.Length == 0) return null;
                    return Unproved($"unverified quest dispatch {method}");
                }
                case "alias":
                    if (method.Equals("GetOwningQuest", StringComparison.OrdinalIgnoreCase) && args.Length == 0) return SliceValue.Of("quest", value.Keys, "Quest");
                    return sliceArgs.Length == 0 ? null : Unproved($"slice value passed to alias {method}");
                case "list":
                    if (SliceReadMethods.Contains(method) && NativeListFrameContract(catalog, method)) return null;
                    if (method.Equals("GetNthForm", StringComparison.OrdinalIgnoreCase))
                    {
                        // An entry of the list: any descendant list it can be.
                        var descendants = new HashSet<FormKey>(); var pending = new Queue<(FormKey Key, int Depth)>(value.Keys.Select(k => (k, 0)));
                        while (pending.TryDequeue(out var current))
                            if (current.Depth <= 16 && descendants.Count < 8192 && catalog.Lists.TryGetValue(current.Key, out var list))
                                foreach (var entry in list.Entries ?? [])
                                    if (entry.Data is { } data && catalog.Lists.ContainsKey(data.Reference.FormKey) && descendants.Add(data.Reference.FormKey))
                                        pending.Enqueue((data.Reference.FormKey, current.Depth + 1));
                        return SliceValue.Of("list", descendants);
                    }
                    if (SliceMutationMethods.Contains(method) && NativeListFrameContract(catalog, method))
                    {
                        // Direct property calls are judged by the site, reset and metadata rules.
                        if (direct) return null;
                        effects.Add(new(body.Identity, f.Name, method, value.Keys.ToArray(), position, raw.Trim()));
                        return null;
                    }
                    return Unproved($"unproved list call {method}");
                case "carrier":
                    if (Regex.IsMatch(method, "(?i)^(?:HasForm|GetSize)$")) return null;
                    if (method.Equals("AddForm", StringComparison.OrdinalIgnoreCase) && args.Length == 1)
                    {
                        if (ArgumentValue(f, args[0]) is { Kind: "list" } carried)
                            foreach (var carrier in value.Keys)
                                state.AddCarriedLists(carrier, carried.Keys);
                        return null;
                    }
                    if (method.Equals("GetAt", StringComparison.OrdinalIgnoreCase)
                        && Regex.IsMatch(line[..(after - 1)], @"(?i)^\s*\w+\s*\.\s*AddForm\s*\(\s*\w+\s*\.\s*GetAt\s*\([^()]*$")
                        && Regex.IsMatch(line[after..], @"(?i)^(?:\s+as\s+form)?\s*,\s*[^,()]+,\s*[^,()]+\)\s*$")) return null;
                    return Unproved($"unproved FormList carrier call {method}");
                default:
                    return SliceMutationMethods.Contains(method)
                        ? Unproved($"list call {method} on a handle of unknown origin", !method.Equals("AddForm", StringComparison.OrdinalIgnoreCase)) : null;
            }
            SliceValue? Unproved(string problem, bool mutation = false) { Op(f, raw, problem, value, mutation); return null; }
        }
        SliceValue? Member(ScriptFunction f, string raw, SliceValue value, string name, string tail)
        {
            if (value.Kind is not "quest") { if (value.Kind != "alias") Op(f, raw, $"member {name} of a {value.Kind} handle", value); return null; }
            var target = Target(value);
            if (target is null) { Op(f, raw, $"member {name} of an unanalysed quest view", value); return null; }
            var bound = target.Objects.TryGetValue(name, out var key) ? SliceRecordValue(key, body.Mod, catalog)
                : target.Arrays.TryGetValue(name, out var keys) ? keys.Select(k => SliceRecordValue(k, body.Mod, catalog)).OfType<SliceValue>().FirstOrDefault() is { } first ? first with { Array = true } : null : null;
            var assignment = Regex.Match(tail, @"^\s*(?:\[[^\]]*\])?\s*[+\-*/]?=(?!=)\s*(.*)$");
            if (assignment.Success)
            {
                if (bound is not null) Op(f, raw, $"rebinding of member {name}", bound);
                else if (ArgumentValue(f, assignment.Groups[1].Value) is { } stored) Op(f, raw, $"member {name} stores a slice value", stored);
                return null;
            }
            var declared = Regex.Match(target.Syntax.Masked, @"(?im)^[ \t]*(\w+)(?:\[\])?[ \t]+property[ \t]+" + Regex.Escape(name) + @"\b");
            if (!declared.Success) { Op(f, raw, $"member {name} is not a declared property", value); return null; }
            return bound is { Kind: "quest" } ? bound with { View = declared.Groups[1].Value } : bound;
        }
        // A receiver expression: a value followed through member reads of quest views.
        SliceValue? ReceiverValue(ScriptFunction f, string text)
        {
            if (Regex.Match(text.Trim(), @"(?is)^\(\s*(?<inner>.+?)\s+as\s+(?<type>\w+)\s*\)$") is { Success: true } cast)
            {
                var inner = OwningQuestCall(cast.Groups["inner"].Value) ?? ReceiverValue(f, cast.Groups["inner"].Value);
                return inner is { Kind: "quest" } && !cast.Groups["type"].Value.Equals("Form", StringComparison.OrdinalIgnoreCase)
                    ? inner with { View = cast.Groups["type"].Value } : inner;
            }
            if (OwningQuestCall(text) is { } owner) return owner;
            var segments = text.Split('.').Select(t => t.Trim()).ToArray();
            var value = ArgumentValue(f, segments[0]);
            foreach (var segment in segments.Skip(1))
            {
                if (value is not { Kind: "quest" } || Target(value) is not { } target) return null;
                var name = Regex.Match(segment, @"^\w+").Value;
                value = target.Objects.TryGetValue(name, out var key) ? SliceRecordValue(key, body.Mod, catalog)
                    : target.Arrays.TryGetValue(name, out var keys) && segment.Contains('[')
                        ? keys.Select(k => SliceRecordValue(k, body.Mod, catalog)).OfType<SliceValue>().FirstOrDefault() : null;
                if (value is { Kind: "quest" } && Regex.Match(target.Syntax.Masked, @"(?im)^[ \t]*(\w+)(?:\[\])?[ \t]+property[ \t]+" + Regex.Escape(name) + @"\b") is { Success: true } declared)
                    value = value with { View = declared.Groups[1].Value };
            }
            return value;
        }
        // `GetOwningQuest()` or `self.GetOwningQuest()` of a body that knows its quest.
        SliceValue? OwningQuestCall(string text) =>
            body.OwningQuest is { } owning && SliceRecord(owning, body.Mod, catalog) && Regex.IsMatch(text.Trim(), @"(?i)^(?:self\s*\.\s*)?GetOwningQuest\s*\(\s*\)$")
                ? SliceValue.Of("quest", [owning], "Quest") : null;
        // The receiver expression before `.method` at the end of the text, or null for a plain call.
        string? ReceiverText(string prefix)
        {
            var dot = Regex.Match(prefix, @"\.\s*$");
            if (!dot.Success) return null;
            var text = prefix[..dot.Index].TrimEnd();
            var start = text.Length;
            if (text.EndsWith(')'))
            {
                var depth = 0;
                for (start = text.Length - 1; start >= 0; start--)
                    if (text[start] == ')') depth++;
                    else if (text[start] == '(' && --depth == 0) break;
                if (start < 0) return null;
            }
            // An identifier chain before it: the whole receiver, or a call's name directly
            // before its parentheses. A keyword before a grouping parenthesis is not one.
            var chain = Regex.Match(text[..start], @"(?:\w+(?:\s*\[[^\]]*\])?\s*\.\s*)*\w+(?:\s*\[[^\]]*\])?$");
            if (chain.Success && !Regex.IsMatch(chain.Value, @"(?i)^(?:if|elseif|while|return|new)$")) start = chain.Index;
            var receiver = text[start..].Trim();
            return receiver.Length == 0 ? null : receiver;
        }
        // The value of a whole argument or right-hand expression, when it is one.
        SliceValue? ArgumentValue(ScriptFunction f, string expression)
        {
            var text = expression.Trim();
            text = Regex.Replace(text, @"(?i)\s+as\s+(?:form|leveleditem|formlist|quest)\s*$", "");
            var token = Regex.Match(text, @"^(?:\(\s*)?(\w+)(?:\s*\[[^\]]*\])?(?:\s+as\s+\w+\s*\))?$");
            if (!token.Success) return null;
            var name = token.Groups[1].Value;
            if (name.Equals("self", StringComparison.OrdinalIgnoreCase)) return self;
            if (name.Equals("none", StringComparison.OrdinalIgnoreCase)) return null;
            var value = BoundValue(f, name);
            return value is { Array: true } && text.Contains('[') ? value with { Array = false } : value;
        }
        void Context(ScriptFunction f, string line, string raw, int start, int pos, SliceValue value, bool direct, string? baseName)
        {
            var before = line[..start]; var after = line[pos..];
            if (Regex.IsMatch(after, @"(?i)^\s*(?:==|!=)\s*none\b") || Regex.IsMatch(before, @"(?i)\bnone\s*(?:==|!=)\s*$")) return;
            // Bool conversion: `if x`, `!x`, `x && ...` read only whether the handle is None.
            if (Regex.IsMatch(before, @"(?i)(?:^\s*(?:if|elseif|while)|&&|\|\||(?<![\w\])]\s*)\(|!)\s*!?\s*$") && Regex.IsMatch(after, @"^\s*(?:&&|\|\||\)|$)")) return;
            var assignTarget = Regex.Match(after, @"^\s*(?<op>[+\-*/]?=)(?!=)\s*(?<rhs>.*)$");
            if (assignTarget.Success && Regex.IsMatch(before, @"^\s*(?:\w+(?:\[\])?\s+)?$"))
            {
                if (baseName is not null && properties.Contains(baseName) && direct && !locals[f].ContainsKey(baseName))
                {
                    Op(f, raw, $"rebinding of property {baseName}", value);
                    if (ArgumentValue(f, assignTarget.Groups["rhs"].Value) is { } rhs) Op(f, raw, $"rebinding of property {baseName}", rhs);
                }
                return; // The right-hand side is classified as its own occurrence.
            }
            var assignment = Regex.Match(before, @"^\s*(?:(?<type>\w+)(?<array>\[\])?\s+)?(?<name>\w+)\s*(?<index>\[[^\]]*\])?\s*=\s*$");
            if (assignment.Success && Regex.IsMatch(after, @"^\s*$"))
            {
                var name = assignment.Groups["name"].Value;
                var stored = assignment.Groups["index"].Success ? value with { Array = true } : value;
                if (assignment.Groups["type"].Success || body.Syntax.Local(f, name)) { Track(locals[f], name, stored); return; }
                if (properties.Contains(name)) { Op(f, raw, $"slice value stored in property {name}", value); return; }
                Track(script, name, stored); return;
            }
            if (Regex.IsMatch(before, @"(?i)^\s*return\s+$") && Regex.IsMatch(after, @"^\s*$"))
            {
                if (!returns.TryGetValue(f.Name, out var old) || Merge(old, value) != old) { returns[f.Name] = Merge(old, value); changed = true; }
                return;
            }
            if (Argument(f, line, raw, start, pos, value)) return;
            Op(f, raw, "slice value use is unproved", value);
        }
        // An argument of an enclosing call: followed into its parameter, or one of
        // the contract's admitted native or carrier uses.
        bool Argument(ScriptFunction f, string line, string raw, int start, int pos, SliceValue value)
        {
            var depth = 0; var open = -1;
            for (var i = start - 1; i >= 0 && open < 0; i--)
                if (line[i] == ')') depth++;
                else if (line[i] == '(') { if (depth == 0) open = i; else depth--; }
            if (open < 0) return false;
            var head = Regex.Match(line[..open], @"(?<method>\w+)\s*$");
            if (!head.Success) return false;
            if (BalancedMemberCall("." + line[head.Groups["method"].Index..]) is not { } call) return false;
            var args = SplitArguments(call.Args);
            var index = SplitArguments(line[(open + 1)..start] + "x").Length - 1;
            // The value must be the whole argument (a trailing record-class cast aside).
            string Normal(string text) => Regex.Replace(Regex.Replace(text, @"(?i)\s+as\s+(?:form|leveleditem|formlist|quest|\w+)\s*$", ""), @"\s+", "");
            if (index >= args.Length || Normal(args[index]) != Normal(line[start..pos])) return false;
            var method = head.Groups["method"].Value; var recv = ReceiverText(line[..head.Index]);
            if (recv is null || recv.Equals("self", StringComparison.OrdinalIgnoreCase))
            {
                if (Defines(body, method)) { SeedCall(body, method, index, value); return true; }
                return false;
            }
            var receiver = ReceiverValue(f, recv);
            // A list as another AddForm's payload is a list-carrier edge, whatever the receiver;
            // the receiver's own change is judged separately.
            if (value.Kind == "list" && method.Equals("AddForm", StringComparison.OrdinalIgnoreCase) && index == 0 && args.Length == 3) return true;
            if (receiver is not null)
            {
                if (receiver.Kind == "carrier" && Regex.IsMatch(method, "(?i)^(?:AddForm|HasForm)$") && index == 0) return true;
                if (receiver.Kind != "quest" || Target(receiver) is not { } target || !Defines(target, method)) return false;
                SeedCall(target, method, index, value);
                return true;
            }
            if (value.Kind == "list" && method.Equals("AddItem", StringComparison.OrdinalIgnoreCase) && index == 0 && args.Length == 3
                && Regex.IsMatch(args[1].Trim(), "^[1-9][0-9]*$") && Regex.IsMatch(args[2].Trim(), "(?i)^(?:true|false)$")
                && NativeFrameContract(catalog, "ObjectReference", "AddItem", "", ["Form", "Int", "Bool"], false)
                && (Regex.IsMatch(recv, @"(?i)^game\s*\.\s*GetPlayer\s*\(\s*\)$") ? NativeFrameContract(catalog, "Game", "GetPlayer", "Actor", [], true)
                    : body.Objects.TryGetValue(recv, out var actor) && (actor == PlayerReference || SliceActorReferences(catalog).ContainsKey(actor)))) return true;
            // A global function, or a typed receiver. Another mod's script is outside this
            // mod's proof and counts as proved (ADR 0008); the mod's own script is followed.
            var type = Regex.IsMatch(recv, @"^\w+$") ? DeclaredScriptType(f, recv)
                : Regex.Match(recv, @"(?is)^\(.+\s+as\s+(\w+)\s*\)$") is { Success: true } cast ? cast.Groups[1].Value : null;
            if (type is null) return false;
            if (!SliceOwnScript(type, body.Mod, catalog)) return true;
            var library = state.Bodies.GetValueOrDefault("l:" + type) ?? SliceLibraryBody(type, body.Mod, catalog);
            if (!Defines(library, method)) return false;
            SeedCall(library, method, index, value);
            return true;
        }
        string? DeclaredScriptType(ScriptFunction f, string name)
        {
            if (locals[f].ContainsKey(name) || script.ContainsKey(name)) return null;
            var declared = body.Syntax.Declared(f, name);
            if (declared is not null) return Regex.IsMatch(declared, @"(?i)^(?:int|float|bool|string)$") ? null : declared.TrimEnd('[', ']');
            return catalog.RuntimeHolderAssetResolver?.Invoke(name).Text is not null ? name : null; // global call on a script name
        }
        for (var round = 0; changed && round < 32; round++)
        {
            changed = false; ops.Clear(); effects.Clear();
            foreach (var f in body.Functions)
            {
                foreach (var syntax in body.Syntax.Lines(f))
                {
                    var raw = syntax.Raw; var line = syntax.Masked; var position = syntax.Position;
                    foreach (var token in syntax.Tokens)
                    {
                        var name = token.Value;
                        var value = name.Equals("self", StringComparison.OrdinalIgnoreCase) ? self
                            : BoundValue(f, name);
                        var lookup = name.Equals("Game", StringComparison.OrdinalIgnoreCase)
                            ? Regex.Match(line[token.Index..], @"(?i)^Game\s*\.\s*(?<m>GetFormFromFile|GetFormEx|GetForm)\s*\(") : Match.Empty;
                        if (lookup.Success)
                        {
                            var literal = Regex.Match(raw[token.Index..], "(?i)^Game\\s*\\.\\s*GetFormFromFile\\s*\\(\\s*(?<id>0x[0-9a-f]+|\\d+)\\s*,\\s*\"(?<plugin>[^\"\\\\]+)\"\\s*\\)");
                            var form = Regex.Match(raw[token.Index..], @"(?i)^Game\s*\.\s*GetForm\s*\(\s*(?<id>0x[0-9a-f]+|\d+)\s*\)");
                            FormKey? found = null; var dynamic = !literal.Success && !form.Success;
                            if (literal.Success) found = SliceLiteralLookup(literal.Groups["id"].Value, literal.Groups["plugin"].Value, catalog);
                            else if (form.Success) found = SliceLiteralForm(form.Groups["id"].Value, catalog, out dynamic);
                            value = dynamic ? SliceValue.Of("unknown", []) : found is { } record ? SliceRecordValue(record, body.Mod, catalog) : null;
                            if (value is not null && BalancedMemberCall(line[(token.Index + "Game".Length)..]) is { } call)
                                Use(f, line, raw, position, token.Index, token.Index + "Game".Length + call.Length, value, false, null);
                            continue;
                        }
                        if (value is null && name.Equals("self", StringComparison.OrdinalIgnoreCase)
                            && Regex.Match(line[token.Index..], @"(?i)^self\s*\.\s*GetOwningQuest\s*\(\s*\)") is { Success: true } selfOwner
                            && OwningQuestCall(selfOwner.Value) is { } selfQuest)
                        {
                            Use(f, line, raw, position, token.Index, token.Index + selfOwner.Length, selfQuest, false, null);
                            continue;
                        }
                        if (value is null && body.OwningQuest is { } owning && name.Equals("GetOwningQuest", StringComparison.OrdinalIgnoreCase)
                            && Regex.IsMatch(line[(token.Index + name.Length)..], @"^\s*\(\s*\)") && SliceRecord(owning, body.Mod, catalog)
                            && (token.Index == 0 || !line[..token.Index].TrimEnd().EndsWith('.')))
                        {
                            var close = line.IndexOf(')', token.Index);
                            Use(f, line, raw, position, token.Index, close + 1, SliceValue.Of("quest", [owning], "Quest"), false, null);
                            continue;
                        }
                        if (value is null && (Returned(body, name) is { } returned) && Regex.IsMatch(line[(token.Index + name.Length)..], @"^\s*\(")
                            && BalancedMemberCall("." + line[token.Index..]) is { } own)
                        {
                            Use(f, line, raw, position, token.Index, token.Index + own.Length - 1, returned, false, null);
                            continue;
                        }
                        if (value is null)
                        {
                            // A list call on a receiver that holds no tracked value: unknown origin.
                            var mutation = Regex.Match(line[(token.Index + name.Length)..], @"(?i)^\s*\.\s*(?<m>AddForm|Revert|RemoveAddedForm|SetChanceNone|SetChanceGlobal|SetNthLevel|SetNthCount)\s*\(");
                            if (mutation.Success && !body.Objects.ContainsKey(name) && !body.Arrays.ContainsKey(name) && !properties.Contains(name)
                                && body.Syntax.Declared(f, name) is var type && (type is null || Regex.IsMatch(type, "(?i)^(?:leveleditem|form)$")))
                                Op(f, raw, $"{mutation.Groups["m"].Value} on a handle of unknown origin", SliceValue.Of("unknown", []),
                                    !mutation.Groups["m"].Value.Equals("AddForm", StringComparison.OrdinalIgnoreCase));
                            continue;
                        }
                        if (IsPropertyDeclaration(line, name) || Regex.IsMatch(line, @"(?i)^\s*(?:\w+(?:\[\])?\s+)?(?:function|event)\s")) continue;
                        if (Regex.IsMatch(line[..token.Index], @"(?i)^\s*\w+(?:\[\])?\s+$") && Regex.IsMatch(line[(token.Index + name.Length)..], @"^\s*$")) continue; // declaration without value
                        var isProperty = !locals[f].ContainsKey(name) && (body.Objects.ContainsKey(name) || body.Arrays.ContainsKey(name));
                        Use(f, line, raw, position, token.Index, token.Index + name.Length, value, isProperty && !value.Array, name);
                    }
                }
            }
        }
        return (ops.Distinct().ToList(), effects.Distinct().ToList(), returns);
    }
}
