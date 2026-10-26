using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Archives;
using Mutagen.Bethesda.Archives.Exceptions;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using Noggog;

namespace XEnchantmentPatcher;

public static partial class PatcherEngine
{
    // Count is the entry count of the item inside the payload. AtStart marks an insertion made
    // by OnInit of a start-game-enabled quest; it has no source stage.
    private sealed record ScriptUnlock(FormKey Quest, ushort Stage, short Level, FormKey OriginalTarget, string Evidence, short Count = 1, bool AtStart = false)
    {
        // Level includes the payload minimum; the receiver cutoff is determined
        // by the original AddForm entry level before that payload is evaluated.
        public short? InsertionLevel { get; init; }
        public FormKey Payload { get; init; }
        // Conditional retry proofs may wait on a different quest. Reset
        // ownership belongs to the distributor, never to the guard quest.
        public FormKey OwnerQuest { get; init; }
    }
    private sealed record RuntimePlacement(FormKey Source, ParentEntry Target, FormKey Child, ScriptUnlock Unlock);

    // Test hook: supplies decompiled script text by script name instead of Champollion.
    private static Func<string, (string? Text, string Evidence)>? ScriptTextOverride = null;

    private static readonly Regex MutationCall = new(@"(?i)\.\s*(AddForm|Revert|RemoveAddedForm|SetChanceNone|SetChanceGlobal|SetNthLevel|SetNthCount)\s*\(", RegexOptions.Compiled);
    private static readonly Regex DirectMutation = new(@"(?i)^\s*(?:(?<recv>\w+)|\(\s*(?<recv>\w+)\s+as\s+(?<cast>\w+)\s*\))\s*\.\s*(?<m>AddForm|Revert|RemoveAddedForm|SetChanceNone|SetChanceGlobal|SetNthLevel|SetNthCount)\s*\((?<args>.*)\)\s*$", RegexOptions.Compiled);
    private static bool IsRuntimeListReset(string method) => method.Equals("Revert", StringComparison.OrdinalIgnoreCase)
        || method.Equals("RemoveAddedForm", StringComparison.OrdinalIgnoreCase);
    private static readonly FormKey PlayerReference = new(ModKey.FromFileName("Skyrim.esm"), 0x14);
    private static readonly Regex Branching =new(@"(?im)^\s*(if|elseif|else|while|return)\b", RegexOptions.Compiled);

    // One attached script: its winning quest, its property values on that quest and its decompiled text.
    private sealed class ScriptInstance
    {
        public required IQuestGetter Quest { get; init; }
        public required string Name { get; init; }
        public bool Alias { get; init; }
        public int ScriptSlot { get; init; }
        public int AliasSlot { get; init; } = -1;
        public (FormKey Quest, short Alias)? AliasIdentity { get; init; }
        public IScriptEntryGetter? BindingEntry { get; init; }
        public string WinningQuestPlugin { get; init; } = "";
        public string? BindingMetadataProblem { get; set; }
        public List<RuntimeObjectHandle> VmadHandles { get; } = [];
        public Dictionary<string, FormKey> Objects { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, FormKey[]> Arrays { get; } = new(StringComparer.OrdinalIgnoreCase);
        // Complete VMAD array identities; Arrays contains record handles only.
        public Dictionary<string, RuntimeObjectHandle[]> ArrayBindings { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, (FormKey Quest, short Alias)> AliasBindings { get; } = new(StringComparer.OrdinalIgnoreCase);
        public string? Text { get; set; }
        public string Evidence { get; set; } = "";
        public string DiagnosticAssetEvidence { get; set; } = "";
        public List<ScriptFunction> Functions { get; } = [];
        public Dictionary<ScriptFunction, ScriptFlowAnalysis.Result> Flow { get; } = new(ScriptFunctionKeys);
        public Dictionary<ScriptFunction, string[]> ConditionalCallers { get; } = new(ScriptFunctionKeys);
        public bool? HasOwnedReset { get; set; }
        // Attached script first, then its custom parents (slice proofs only).
        public string[] Chain { get; init; } = [];
        public string[] ChainNames => Chain.Length > 0 ? Chain : [Name];
        public string Label => $"{Quest.EditorID ?? Quest.FormKey.ToString()}/{Name}";
        private sealed record IdentityText(string NegativeSign, string Value);
        private IdentityText? identity;
        public string Identity
        {
            get
            {
                var sign = System.Globalization.CultureInfo.CurrentCulture.NumberFormat.NegativeSign;
                var cached = System.Threading.Volatile.Read(ref identity);
                if (cached is not null && cached.NegativeSign == sign) return cached.Value;
                var value = $"{Quest.FormKey}/{AliasSlot}/{ScriptSlot}/{AliasIdentity}/{Name}";
                System.Threading.Volatile.Write(ref identity, new(sign, value));
                return value;
            }
        }
    }
    private readonly record struct RuntimeObjectHandle(FormKey Record, short Alias, int Index = -1);
    private sealed record ScriptFunction(string Name, string Kind, string Parameters, string Body, int Start, int End)
    { public int BodyStart { get; init; } public string ReturnType { get; init; } = ""; }
    // Functions are immutable. Retain their value equality and original hash, but
    // avoid hashing the entire script body on every flow/syntax dictionary lookup.
    // Keep cached state outside the record so it cannot affect equality or with copies.
    private static readonly ConditionalWeakTable<ScriptFunction, StrongBox<int>> ScriptFunctionHashes = new();
    private static readonly IEqualityComparer<ScriptFunction> ScriptFunctionKeys = EqualityComparer<ScriptFunction>.Create(
        (left, right) => EqualityComparer<ScriptFunction>.Default.Equals(left, right),
        function => function is null ? 0 : ScriptFunctionHashes.GetValue(function, static f => new(f.GetHashCode())).Value);
    private static readonly IEqualityComparer<(ScriptFunction Function, string Name)> ScriptFunctionNameKeys =
        EqualityComparer<(ScriptFunction Function, string Name)>.Create((left, right) => left.Equals(right),
            key => HashCode.Combine(ScriptFunctionKeys.GetHashCode(key.Function), key.Name?.GetHashCode() ?? 0));
    private sealed record ScriptTrigger(FormKey Quest, ushort Stage, bool AtStart, string Evidence)
    { public FormKey OwnerQuest { get; init; } }

    // Analyze winning VMADs and winning PEX, never script names or mod-specific tables.
    // Every leveled-list call is judged on its own. Detection records every item a proven call adds,
    // whatever its record type; eligibility of those items is decided later by the candidate rules.
    private static void InitializeAvailability(AppSettings settings, Catalog catalog)
    {
        if (catalog.AvailabilityInitialized) return;
        using var timing = PhaseTiming.Measure("availability-scripts");
        ScanProgress.Enter(ScanStage.Availability);
        catalog.AvailabilityInitialized = true;
        var quests = catalog.Mods.SelectMany(m => m.Quests).GroupBy(q => q.FormKey).Select(g => g.Last())
            .Where(q => !q.IsDeleted && q.VirtualMachineAdapter is not null).ToArray();
        var questWinners = catalog.Mods.SelectMany(m => m.Quests.Select(q => (q.FormKey, m.ModKey)))
            .GroupBy(p => p.FormKey).ToDictionary(g => g.Key, g => g.Last().ModKey.FileName.String);
        var formLists = catalog.Mods.SelectMany(m => m.FormLists).GroupBy(f => f.FormKey).ToDictionary(g => g.Key, g => g.Last());

        // Bounded transitive FLST capabilities, including collection-only
        // scripts. An exceeded closure cannot establish a disjoint footprint.
        var winningRecords = RuntimeRecordWinners(catalog);
        // Progress shares follow the measured record-winner, script-asset and mutation phases.
        ScanProgress.Advance(18, 100);
        var reachCache = new Dictionary<FormKey, (FormKey[] Lists, string? Problem)>();
        (FormKey[] Lists, string? Problem) Reach(FormKey key)
        {
            if (reachCache.TryGetValue(key, out var old)) return old;
            var result = new HashSet<FormKey>(); var seen = new HashSet<FormKey>();
            var path = new HashSet<FormKey>(); var heights = new Dictionary<FormKey, int>(); string? problem = null;
            int Visit(FormKey current, int depth)
            {
                if (problem is not null || current.IsNull) return 0;
                // The engine defines the player reference; it is never a collection or list.
                if (current == PlayerReference) return 0;
                if (!winningRecords.TryGetValue(current, out var winner) || winner.Record.IsDeleted)
                { problem = $"VMAD collection closure at {key} has an unresolved or deleted handle {current}"; return 0; }
                var isList = catalog.Lists.ContainsKey(current);
                if (!isList && !formLists.ContainsKey(current)) return 0; // Plain record leaves are not capability states.
                if (path.Contains(current))
                { problem = $"VMAD collection closure at {key} is cyclic at {current}"; return 0; }
                if (depth > 16 || heights.TryGetValue(current, out var height) && depth + height > 16)
                { problem = $"VMAD collection closure at {key} exceeds 16 edges or 8192 collection/LVLI states"; return 0; }
                if (heights.TryGetValue(current, out height)) return height;
                if (seen.Count >= 8192)
                { problem = $"VMAD collection closure at {key} exceeds 16 edges or 8192 collection/LVLI states"; return 0; }
                seen.Add(current);
                if (isList) { result.Add(current); heights[current] = 0; return 0; }
                path.Add(current); var longest = 0;
                foreach (var item in formLists[current].Items)
                {
                    var child = item.FormKey;
                    var childHeight = Visit(child, depth + 1);
                    if (problem is not null) break;
                    if (catalog.Lists.ContainsKey(child) || formLists.ContainsKey(child)) longest = Math.Max(longest, childHeight + 1);
                }
                path.Remove(current);
                return heights[current] = longest;
            }
            Visit(key, 0);
            if (problem is not null)
            {
                // Finish only the static taint inventory, not the failed proof.
                // A list beyond the exhausted frontier must still be refused;
                // unrelated winning lists must not inherit this root's failure.
                var taintSeen = new HashSet<FormKey>(); var pending = new Queue<FormKey>(); pending.Enqueue(key);
                while (pending.TryDequeue(out var current))
                {
                    if (!taintSeen.Add(current)) continue;
                    if (catalog.Lists.ContainsKey(current)) result.Add(current);
                    else if (formLists.TryGetValue(current, out var collection))
                        foreach (var item in collection.Items)
                            if (catalog.Lists.ContainsKey(item.FormKey) || formLists.ContainsKey(item.FormKey)) pending.Enqueue(item.FormKey);
                }
            }
            return reachCache[key] = (result.ToArray(), problem);
        }
        ScriptInstance Instance(IQuestGetter quest, IScriptEntryGetter script, int scriptSlot, int aliasSlot = -1,
            (FormKey Quest, short Alias)? aliasIdentity = null)
        {
            var instance = new ScriptInstance { Quest = quest, Name = script.Name, Alias = aliasSlot >= 0,
                ScriptSlot = scriptSlot, AliasSlot = aliasSlot, AliasIdentity = aliasIdentity,
                BindingEntry = script, WinningQuestPlugin = questWinners[quest.FormKey] };
            if (script.Properties.GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase).Any(g => g.Count() != 1))
                instance.BindingMetadataProblem = "duplicate VMAD property bindings have unproved runtime dispatch";
            foreach (var p in script.Properties)
            {
                if (p is IScriptObjectPropertyGetter handle) instance.VmadHandles.Add(new(handle.Object.FormKey, handle.Alias));
                else if (p is IScriptObjectListPropertyGetter handles)
                    instance.VmadHandles.AddRange(handles.Objects.Select((x, index) => new RuntimeObjectHandle(x.Object.FormKey, x.Alias, index)));
                if (p is IScriptObjectPropertyGetter o && o.Alias == -1) instance.Objects[p.Name] = o.Object.FormKey;
                else if (p is IScriptObjectPropertyGetter aliasObject) instance.AliasBindings[p.Name] = (aliasObject.Object.FormKey, aliasObject.Alias);
                else if (p is IScriptObjectListPropertyGetter l)
                {
                    instance.ArrayBindings[p.Name] = l.Objects.Select((x, index) => new RuntimeObjectHandle(x.Object.FormKey, x.Alias, index)).ToArray();
                    instance.Arrays[p.Name] = l.Objects.Where(x => x.Alias == -1).Select(x => x.Object.FormKey).ToArray();
                }
            }
            return instance;
        }
        IEnumerable<ScriptInstance> Instances(IQuestGetter quest) =>
            quest.VirtualMachineAdapter!.Scripts.Select((s, index) => Instance(quest, s, index))
                .Concat(quest.VirtualMachineAdapter.Aliases.SelectMany((a, aliasIndex) => a.Scripts.Select((s, index) =>
                    Instance(quest, s, index, aliasIndex, (a.Property.Object.FormKey, a.Property.Alias)))));
        var rawByQuest = quests.ToDictionary(q => q.FormKey, q => Instances(q).ToArray());
        bool TouchesLists(ScriptInstance i) => i.VmadHandles.Where(h => h.Alias == -1).Any(h =>
        {
            var reach = Reach(h.Record);
            return reach.Lists.Length > 0 || reach.Problem is not null;
        });
        string? InstanceClosureProblem(ScriptInstance i) => i.VmadHandles.Where(h => h.Alias == -1)
            .Select(h => Reach(h.Record).Problem).FirstOrDefault(p => p is not null);

        // Quests whose scripts can reach a leveled list, plus quests that hold a property pointing at one
        // of them (possible callers of a seeding function on that quest).
        var analysed = new Dictionary<FormKey, List<ScriptInstance>>();
        foreach (var quest in quests)
        {
            OperationProgress.Detail($"Checking quest scripts: {quest.FormKey.ModKey.FileName}: {quest.EditorID ?? quest.FormKey.ToString()}");
            var instances = rawByQuest[quest.FormKey].ToList();
            if (instances.Any(TouchesLists)) analysed[quest.FormKey] = instances;
        }
        var listQuests = analysed.Keys.ToHashSet();
        foreach (var quest in quests.Where(q => !listQuests.Contains(q.FormKey)))
        {
            var instances = rawByQuest[quest.FormKey].ToList();
            if (instances.Any(i => RuntimeQuestHandles(i).Any(listQuests.Contains))) analysed[quest.FormKey] = instances;
        }
        var all = analysed.Values.SelectMany(x => x).ToList();
        // Resolve winning asset bytes once. Incoming frames decompile only the
        // instances in a completed bounded slice, never the entire raw closure.
        var wanted = AvailabilityScriptNames(catalog.Mods, RuntimeRecordWinners(catalog));
        ScriptAssets? resolver = null;
        if (ScriptTextOverride is null)
        {
            // A scan resolves the same assets in the background while it captures SkyPatcher
            // inputs and builds the catalog; use that result only for identical inputs.
            if (catalog.PrefetchedScriptAssets is { } pending && pending.Data == (settings.DataFolder ?? "")
                && pending.Order.SequenceEqual(catalog.Mods.Select(m => m.ModKey)))
            {
                (HashSet<string> Wanted, ScriptAssets Assets) done;
                using (PhaseTiming.Measure("availability.assets-wait")) done = pending.Work.GetAwaiter().GetResult();
                if (done.Wanted.SetEquals(wanted)) resolver = done.Assets;
            }
            resolver ??= new ScriptAssets(settings.DataFolder ?? "", catalog.Mods.Select(m => m.ModKey), wanted);
        }
        catalog.PrefetchedScriptAssets = null;
        catalog.ScriptAnalysisWarning = resolver?.AnalysisWarning;
        var holderTextOverride = ScriptTextOverride;
        var texts = new Dictionary<string, (string? Text, string Evidence)>(StringComparer.OrdinalIgnoreCase);
        // Separate assets: new holder parents must not change existing Quest
        // frame signatures merely by being loaded for supplementary analysis.
        RuntimeHolderAsset HolderAsset(Catalog target, string name)
        {
        lock (target.SliceLock)
        {
                if (target.RuntimeHolderAssets.TryGetValue(name, out var cached)) return cached;
                if (!texts.TryGetValue(name, out var value))
                {
                    if (holderTextOverride is not null) value = holderTextOverride(name);
                    else { var body = resolver!.Decompile(name, out var detail); value = (body, detail); }
                    value = PrepareRuntimeScript(value); texts[name] = value;
                }
                return target.RuntimeHolderAssets[name] = new(value.Text, value.Evidence,
                    resolver?.DiagnosticEvidence(name) ?? value.Evidence);
        }
    }
        catalog.RuntimeHolderAssetResolver = name => HolderAsset(catalog, name);
        void LoadInstance(ScriptInstance instance)
        {
            LoadText(instance);
            catalog.RuntimeScriptInstances[instance.Identity] = instance;
        }
        void LoadText(ScriptInstance instance)
        {
            using var textTiming = PhaseTiming.Measure("availability.load-text");
            if (!texts.TryGetValue(instance.Name, out var text))
            {
                if (ScriptTextOverride is not null) text = ScriptTextOverride(instance.Name);
                else { var t = resolver!.Decompile(instance.Name, out var evidence); text = (t, evidence); }
                text = PrepareRuntimeScript(text);
                texts[instance.Name] = text;
            }
            instance.Text = text.Text; instance.Evidence = text.Evidence;
            instance.DiagnosticAssetEvidence = resolver?.DiagnosticEvidence(instance.Name) ?? text.Evidence;
            if (text.Text is not null && instance.Functions.Count == 0) instance.Functions.AddRange(ParseFunctions(text.Text));
        }
        if (ScriptTextOverride is null)
                resolver?.Prefetch(all.Select(i => i.Name).Concat(["Quest", "Alias", "ReferenceAlias", "Actor", "ObjectReference", "Debug", "Game", "LeveledItem", "Form", "Keyword", "GlobalVariable"]));
        var scriptsLoaded = 0;
        foreach (var instance in all)
        {
            ScanProgress.Advance(18 * all.Count + 20 * scriptsLoaded++, 100 * all.Count);
            OperationProgress.Detail($"Reading quest script ({scriptsLoaded:N0}/{all.Count:N0}): {instance.Name}");
            LoadInstance(instance);
        }
        ScanProgress.Advance(38, 100);
        foreach (var name in new[] { "Quest", "Alias", "ReferenceAlias", "Actor", "ObjectReference", "Debug", "Game", "LeveledItem", "Form", "Keyword", "GlobalVariable" })
        {
            OperationProgress.Detail($"Reading core script: {name}");
            if (!texts.TryGetValue(name, out var text))
            {
                if (ScriptTextOverride is not null) text = ScriptTextOverride(name);
                else { var t = resolver!.Decompile(name, out var evidence); text = (t, evidence); }
                text = PrepareRuntimeScript(text);
            }
            catalog.RuntimeBaseScripts[name] = text;
            catalog.RuntimeBaseScriptDiagnosticEvidence[name] = resolver?.DiagnosticEvidence(name) ?? text.Evidence;
        }

        var diagnostics = catalog.ScriptCalls;
        var unlocks = new List<(FormKey Item, ScriptUnlock Unlock)>();
        var blocked = new Dictionary<FormKey, string>();
        var reverted = new Dictionary<FormKey, string>();
        // Which scripts reset each list, and which scripts add to each list (for conflict warnings).
        var resetters = new Dictionary<FormKey, HashSet<ScriptInstance>>();
        var inserters = new Dictionary<FormKey, HashSet<ScriptInstance>>();
        void Block(FormKey item, string reason) => blocked.TryAdd(item, reason);
        void Control(FormKey list, string reason) => catalog.RuntimeControlledLists.TryAdd(list, reason);
        // Records which script's unproved operation can reach a list. The shared
        // OtherRuntimeMutations taint stays frame-wide for the other routes.
        void Taint(IEnumerable<FormKey> lists, ScriptInstance instance, string reason, string? property = null, bool receiver = false, bool flow = false)
        {
            foreach (var list in lists)
            {
                if (!catalog.RuntimeListTaints.TryGetValue(list, out var rows)) catalog.RuntimeListTaints[list] = rows = [];
                rows.Add(new(instance.Identity, reason, property, receiver, flow));
            }
        }
        IEnumerable<FormKey> PropertyLists(ScriptInstance instance, string property) =>
            (instance.Objects.TryGetValue(property, out var key) ? [key] : instance.Arrays.GetValueOrDefault(property, []))
                .SelectMany(k => Reach(k).Lists).Distinct();
        void Diag(ScriptInstance i, string function, string line, string method, string target, string payload, string trigger, string verdict, string detail) =>
            diagnostics.Add(new ScriptCallDiagnostic(i.Quest.EditorID ?? i.Quest.FormKey.ToString(), i.Name, function, line.Trim(), method,
                target, payload, trigger, verdict, detail));
        string Name(FormKey key) => catalog.Lists.TryGetValue(key, out var l) ? $"{l.EditorID} [{key}]" : key.ToString();

        // Records a script could hand out: object properties that are not lists, plus leaves of lists
        // owned by the quest's own plugin. Used only when a call cannot be traced to its payload.
        IEnumerable<FormKey> ScriptItems(ScriptInstance i)
        {
            var seen = new HashSet<FormKey>();
            IEnumerable<FormKey> Leaves(FormKey key)
            {
                if (!seen.Add(key)) yield break;
                if (catalog.Lists.TryGetValue(key, out var list))
                { foreach (var e in list.Entries ?? []) if (e.Data is { } d) foreach (var x in Leaves(d.Reference.FormKey)) yield return x; }
                else yield return key;
            }
            foreach (var key in i.VmadHandles.Where(h => h.Alias == -1).Select(h => h.Record))
            {
                if (!catalog.Lists.ContainsKey(key)) { if (!formLists.ContainsKey(key)) yield return key; continue; }
                if (key.ModKey == i.Quest.FormKey.ModKey) foreach (var leaf in Leaves(key)) yield return leaf;
            }
        }
        IEnumerable<FormKey> ScriptLists(ScriptInstance i) =>
            i.VmadHandles.Where(h => h.Alias == -1).SelectMany(h => Reach(h.Record).Lists).Distinct();

        var boundPropertyCallers = all.SelectMany(i => RuntimeQuestHandles(i).Where(analysed.ContainsKey).Append(i.Quest.FormKey).Distinct()
            .Select(q => (Quest: q, Instance: i))).GroupBy(p => p.Quest)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<ScriptInstance>)g.Select(p => p.Instance).Distinct().ToArray());
        var incomingInterfaces = rawByQuest.Values.SelectMany(x => x).SelectMany(i => RuntimeQuestHandles(i).Distinct().Select(target => (target, i)))
            .GroupBy(p => p.target).ToDictionary(g => g.Key, g => g.Select(p => p.i).Distinct().ToArray());
        foreach (var ownerQuest in boundPropertyCallers.Keys.ToArray())
            boundPropertyCallers[ownerQuest] = CloseRuntimeIncomingFrame(ownerQuest, boundPropertyCallers[ownerQuest], rawByQuest, incomingInterfaces, LoadInstance, catalog);
        CollectRuntimeListCarriers(catalog);
        string? BindingClosureProblem(ScriptInstance instance) =>
            boundPropertyCallers.GetValueOrDefault(instance.Quest.FormKey, []).Append(instance)
                .Select(InstanceClosureProblem).FirstOrDefault(p => p is not null);
        IEnumerable<FormKey> BindingFrameLists(ScriptInstance instance) =>
            boundPropertyCallers.GetValueOrDefault(instance.Quest.FormKey, []).Append(instance).SelectMany(ScriptLists).Distinct();
        // The closures retain raw VMAD/asset evidence, but all selection-sensitive
        // proofs and diagnostics must use the catalog of the current pass.
        // Resolvers this initializer binds to the passes of one analysis prove the same
        // bindings; each pass starts from a copy of its source's resolved bindings.
        var resolverFamily = new object();
        catalog.InitializeResolvers = target =>
        {
            target.RuntimeHolderAssetResolver = name => HolderAsset(target, name);
            var runtimeBindings = target.RuntimeBindings;
            RuntimePropertyBinding Binding(ScriptInstance instance, string name, bool receiver)
            {
                var key = (instance, name.ToLowerInvariant(), receiver);
                if (runtimeBindings.TryGetValue(key, out var old)) return old;
                var closureProblem = BindingClosureProblem(instance);
                var result = runtimeBindings[key] = closureProblem is not null
                    ? new RuntimePropertyBinding("VMAD capability closure is unproved: " + closureProblem, true)
                    : RuntimePropertyFrame(instance, name, receiver, boundPropertyCallers.GetValueOrDefault(instance.Quest.FormKey, []), target);
                target.RuntimeBindingChecks.Add(new(instance.Identity, name, receiver, result));
                return result;
            }
            Func<ScriptInstance, string, bool, RuntimePropertyBinding> resolver = Binding;
            target.RuntimeBindingResolver = resolver;
            target.RuntimeBindingResolverOrigin = (resolver, resolverFamily);
            // Slice proofs (script distribution): only the value's own collection
            // closure, and a frame bounded to its source mod (ADR 0008/0009).
            var sliceBindings = target.RuntimeSliceBindings;
            var sliceCallers = target.RuntimeSliceCallerCache;
            // Contract bound: the incoming-interface closure counts only the mod's own
            // callers whose text mentions or obtains a slice value; others cannot touch
            // the slice, and other mods' scripts are outside the proof (ADR 0008).
            (IReadOnlyList<ScriptInstance> Callers, string? Problem) SliceCallers(FormKey owner, ModKey mod)
            {
                if (sliceCallers.TryGetValue((owner, mod), out var cached)) return cached;
                bool Mentions(ScriptInstance i)
                {
                    if (i.Text is null) return true;
                    var body = string.Join('\n', i.Functions.Select(f => MaskScriptStrings(f.Body)));
                    if (Regex.IsMatch(body, @"(?i)\b(?:GetFormFromFile|GetFormEx|GetForm)\s*\(")) return true;
                    return i.Objects.Where(p => SliceRecord(p.Value, mod, target)).Select(p => p.Key)
                        .Concat(i.Arrays.Where(p => p.Value.Any(k => SliceRecord(k, mod, target))).Select(p => p.Key))
                        .Any(name => Regex.IsMatch(body, @"(?i)\b" + Regex.Escape(name) + @"\b"));
                }
                var rows = new HashSet<ScriptInstance>(); var visited = new HashSet<FormKey>();
                var pending = new Queue<(FormKey Key, int Depth)>(); pending.Enqueue((owner, 0));
                string? problem = null;
                while (pending.TryDequeue(out var current) && problem is null)
                {
                    if (!visited.Add(current.Key)) continue;
                    if (current.Depth > 16 || visited.Count > 8192) { problem = $"slice incoming-interface closure at {current.Key} exceeds 16 edges or 8192 states"; break; }
                    var mentioning = new List<ScriptInstance>();
                    foreach (var script in rawByQuest.GetValueOrDefault(current.Key, []))
                    {
                        LoadText(script);
                        target.RuntimeScriptInstances[script.Identity] = script;
                        if (current.Key == owner || InSliceScope(script.Quest.FormKey, mod, target) && Mentions(script)) mentioning.Add(script);
                    }
                    rows.UnionWith(mentioning);
                    if (rows.Count > 512) { problem = $"slice incoming-interface closure at {current.Key} exceeds 512 script instances"; break; }
                    if (mentioning.Count == 0 && current.Key != owner) continue;
                    foreach (var caller in incomingInterfaces.GetValueOrDefault(current.Key, []).Where(c => InSliceScope(c.Quest.FormKey, mod, target)))
                        pending.Enqueue((caller.Quest.FormKey, current.Depth + 1));
                }
                return sliceCallers[(owner, mod)] = (rows.OrderBy(i => i.Identity, StringComparer.Ordinal).ToArray(), problem);
            }
            target.RuntimeSliceCallers = (owner, mod) => SliceCallers(owner, mod).Callers;
            var scopeInstances = target.RuntimeScopeInstanceCache;
            target.RuntimeScopeInstances = mod =>
            {
                lock (target.SliceLock)
                {
                if (scopeInstances.TryGetValue(mod, out var known)) return known;
                // Slice proofs only: other routes keep seeing the instances their own analysis loaded.
                var rows = rawByQuest.Where(p => InSliceScope(p.Key, mod, target)).OrderBy(p => p.Key).SelectMany(p => p.Value).ToArray();
                foreach (var instance in rows)
                {
                    if (instance.Text is null && instance.Evidence.Length == 0) LoadText(instance);
                    if (!target.RuntimeScriptInstances.ContainsKey(instance.Identity)) target.RuntimeSliceInstances[instance.Identity] = instance;
                }
                return scopeInstances[mod] = rows;
                }
            };
            RuntimePropertyBinding SliceBinding(ScriptInstance instance, string name, bool receiver)
            {
                var key = (instance, name.ToLowerInvariant(), receiver);
                if (sliceBindings.TryGetValue(key, out var old)) return old;
                var handles = instance.Objects.TryGetValue(name, out var handle) ? [handle] : instance.Arrays.GetValueOrDefault(name, []);
                var reach = handles.Select(h => Reach(h).Problem).FirstOrDefault(p => p is not null);
                var scope = new RuntimeSliceScope(SliceSourceMod(instance));
                var callers = SliceCallers(instance.Quest.FormKey, scope.Source);
                var result = reach is not null ? new RuntimePropertyBinding("VMAD capability closure is unproved: " + reach, true)
                    : callers.Problem is { } bound ? new RuntimePropertyBinding(bound, true)
                    : RuntimePropertyFrame(instance, name, receiver, callers.Callers, target, scope);
                // Every in-scope operation the slice flow leaves unproved on this value's lists.
                if (result.Problem is null && SliceFlowProblem(PropertyLists(instance, name), scope.Source, target, scope.Warnings) is { } flow)
                    result = new RuntimePropertyBinding(flow, true);
                return sliceBindings[key] = result with { Warnings = result.Warnings.Concat(scope.Warnings).Distinct().ToArray() };
            }
            target.RuntimeSliceBindingResolver = SliceBinding;
        };
        catalog.InitializeResolvers(catalog);
        RuntimePropertyBinding Binding(ScriptInstance instance, string name, bool receiver) =>
            catalog.RuntimeBindingResolver!(instance, name, receiver);
        foreach (var instance in all)
            foreach (var handle in instance.VmadHandles.Where(h => h.Alias == -1).Select(h => h.Record).Distinct())
                if (Reach(handle).Problem is { } handleProblem)
                    Taint(Reach(handle).Lists, instance, "VMAD capability closure is unproved: " + handleProblem);
        foreach (var instance in all)
            if (BindingClosureProblem(instance) is { } closureProblem)
            {
                var reason = "VMAD capability closure is unproved: " + closureProblem;
                foreach (var list in BindingFrameLists(instance))
                {
                    Control(list, reason);
                    catalog.RuntimeAddPayloadProblems.TryAdd(list, reason);
                    catalog.OtherRuntimeMutations.Add(list);
                }
                foreach (var item in ScriptItems(instance)) Block(item, reason);
                Diag(instance, "", "", "", "", "", "", "Unverifiable", reason);
            }
        foreach (var instance in all.Where(i => i.BindingMetadataProblem is not null))
        {
            foreach (var list in BindingFrameLists(instance))
            {
                Control(list, instance.BindingMetadataProblem!);
                catalog.RuntimeAddPayloadProblems.TryAdd(list, instance.BindingMetadataProblem!);
                catalog.OtherRuntimeMutations.Add(list);
            }
            foreach (var item in ScriptItems(instance)) Block(item, instance.BindingMetadataProblem!);
            Taint(ScriptLists(instance), instance, instance.BindingMetadataProblem!);
        }
        // A rebound/escaped receiver can mutate another bound list. Taint the
        // entire reachable frame before freezing any source evidence graph.
        foreach (var instance in all.Where(i => i.Text is not null && listQuests.Contains(i.Quest.FormKey)))
            foreach (var property in instance.Objects.Where(p => Reach(p.Value).Lists.Length > 0).Select(p => p.Key)
                .Concat(instance.Arrays.Where(p => p.Value.Any(k => Reach(k).Lists.Length > 0)).Select(p => p.Key)).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var binding = Binding(instance, property, true);
                if (!binding.MayRetarget) continue;
                // Script distribution re-proves this value in its slice and taints only its own lists.
                Taint(PropertyLists(instance, property), instance, binding.Problem!, property, true);
                foreach (var list in BindingFrameLists(instance))
                {
                    catalog.RuntimeAddPayloadProblems.TryAdd(list, $"{instance.Label}/{property}: {binding.Problem}");
                    Control(list, $"Runtime list binding/escape is unproved in {instance.Label}/{property}");
                    catalog.OtherRuntimeMutations.Add(list);
                }
            }

        // Unconditional entry points of a function: a straight-line stage fragment on an unconditioned
        // stage, or OnInit of a start-game-enabled quest script.
        List<ScriptTrigger> EntryTriggers(ScriptInstance i, ScriptFunction f, int? callOffset = null)
        {
            var result = new List<ScriptTrigger>();
            if (Regex.IsMatch(MaskScriptStrings(i.Text!), @"(?im)^\s*(?:auto\s+)?state\b")) return result;
            if ((callOffset is null ? Branching.IsMatch(f.Body) : !GuaranteedRuntimePrefix(i, f, callOffset.Value, catalog))
                || i.Functions.Count(x => x.Name.Equals(f.Name, StringComparison.OrdinalIgnoreCase)) != 1) return result;
            if (!i.Alias && f.Kind == "function")
                foreach (var fragment in i.Quest.VirtualMachineAdapter!.Fragments.Where(x =>
                    x.ScriptName.Equals(i.Name, StringComparison.OrdinalIgnoreCase) && x.FragmentName.Equals(f.Name, StringComparison.OrdinalIgnoreCase)))
                    if (!HasStageConditions(i.Quest, fragment.Stage))
                        result.Add(new(i.Quest.FormKey, fragment.Stage, false, $"{i.Name}.{f.Name}"));
            if (!i.Alias && f.Kind == "event" && f.Name.Equals("OnInit", StringComparison.OrdinalIgnoreCase)
                && i.Quest.Flags.HasFlag(Quest.Flag.StartGameEnabled))
                result.Add(new(i.Quest.FormKey, 0, true, $"{i.Name}.OnInit of start-game-enabled quest"));
            return result;
        }
        var callMatcher = new ScriptCallMatcher();
        var stageRetries = new Dictionary<ScriptInstance, StageRetryProof>();
        // One level of calls: a straight-line entry point that calls this function directly.
        List<ScriptTrigger> CallerTriggers(ScriptInstance callee, ScriptFunction f, int callOffset)
        {
            var result = new List<ScriptTrigger>();
            if (Regex.IsMatch(MaskScriptStrings(callee.Text!), @"(?im)^\s*(?:auto\s+)?state\b")) return result;
            if (!GuaranteedRuntimePrefix(callee, f, callOffset, catalog)
                || callee.Functions.Count(x => x.Name.Equals(f.Name, StringComparison.OrdinalIgnoreCase)) != 1) return result;
            foreach (var caller in all.Where(x => x.Text is not null))
            foreach (var g in caller.Functions)
            {
                if (ReferenceEquals(caller, callee) && g.Name.Equals(f.Name, StringComparison.OrdinalIgnoreCase)) continue;
                foreach (Match m in callMatcher.Matches(f.Name, g.Body))
                {
                    if (!CallTargets(caller, g, m, callee)) continue;
                    foreach (var t in EntryTriggers(caller, g, m.Index)) result.Add(t with { Evidence = $"{t.Evidence} → {callee.Name}.{f.Name}" });
                }
            }
            return result;
        }
        bool CallTargets(ScriptInstance caller, ScriptFunction g, Match m, ScriptInstance callee)
        {
            var recv = m.Groups["recv"].Success ? m.Groups["recv"].Value : null;
            if (recv is null || recv.Equals("self", StringComparison.OrdinalIgnoreCase)) return ReferenceEquals(caller, callee);
            var type = m.Groups["cast"].Success ? m.Groups["cast"].Value : DeclaredType(caller.Text!, g, recv);
            if (type is null || !type.Equals(callee.Name, StringComparison.OrdinalIgnoreCase)) return false;
            // kmyQuest-style local cast of the fragment's own quest.
            if (Regex.IsMatch(g.Body, @"(?im)^\s*\w+\s+" + Regex.Escape(recv) + @"\s*=\s*__temp\s+as\s+" + Regex.Escape(type) + @"\s*$")
                && Regex.IsMatch(g.Body, @"(?im)^\s*quest\s+__temp\s*=\s*self\s+as\s+quest\s*$"))
                return caller.Quest.FormKey == callee.Quest.FormKey && !callee.Alias;
            return caller.Objects.TryGetValue(recv, out var quest) && quest == callee.Quest.FormKey && !callee.Alias;
        }

        var mutationInstances = all.Count(i => listQuests.Contains(i.Quest.FormKey)); var mutationsDone = 0;
        foreach (var instance in all.Where(i => listQuests.Contains(i.Quest.FormKey)))
        {
            using var instanceTiming = PhaseTiming.Measure("availability.mutations");
            ScanProgress.Advance(38 * mutationInstances + 62 * mutationsDone++, 100 * mutationInstances);
            OperationProgress.Detail($"Checking script mutations ({mutationsDone:N0}/{mutationInstances:N0}): {instance.Name}");
            if (instance.Text is null)
            {
                // Unverifiable script: its lists may be mutated and anything it holds may be injected.
                if (!TouchesLists(instance)) continue;
                foreach (var list in ScriptLists(instance))
                {
                    Control(list, $"Cannot verify quest script {instance.Name}: {instance.Evidence}");
                    catalog.OtherRuntimeMutations.Add(list);
                }
                Taint(ScriptLists(instance), instance, $"Cannot verify quest script {instance.Name}: {instance.Evidence}");
                foreach (var item in ScriptItems(instance)) Block(item, $"Cannot verify script {instance.Name}: {instance.Evidence}");
                Diag(instance, "", "", "", "", "", "", "Unverifiable", instance.Evidence);
                continue;
            }
            var lines = instance.Text.Split('\n');
            var offset = 0;
            foreach (var raw in lines)
            {
                var position = offset; offset += raw.Length + 1;
                if (!MutationCall.IsMatch(raw)) continue;
                var function = instance.Functions.FirstOrDefault(f => position >= f.Start && position < f.End);
                var fname = function?.Name ?? "";
                var direct = DirectMutation.Match(raw);
                if (direct.Success && UnfilledRuntimeListReceiver(instance, direct.Groups["recv"].Value)) continue;
                var method = direct.Success ? direct.Groups["m"].Value : MutationCall.Match(raw).Groups[1].Value;
                FormKey? target = null; var receiverIsList = true;
                if (direct.Success && instance.Objects.TryGetValue(direct.Groups["recv"].Value, out var receiverKey))
                {
                    if (catalog.Lists.ContainsKey(receiverKey)) target = receiverKey;
                    else receiverIsList = false; // FormList, leveled actor or other record: not a leveled item list.
                }
                else if (direct.Success)
                {
                    var type = direct.Groups["cast"].Success ? direct.Groups["cast"].Value
                        : function is null ? null : DeclaredType(instance.Text, function, direct.Groups["recv"].Value);
                    if (type is not null && !type.Equals("leveleditem", StringComparison.OrdinalIgnoreCase) && !type.Equals("form", StringComparison.OrdinalIgnoreCase))
                        receiverIsList = false;
                }
                if (!receiverIsList) continue;
                var targetLabel = target is { } tk ? Name(tk) : direct.Success ? direct.Groups["recv"].Value + " (not a list property)" : "unparsed";

                if (!method.Equals("AddForm", StringComparison.OrdinalIgnoreCase))
                {
                    var lists = target is { } t2 ? [t2] : ScriptLists(instance).ToArray();
                    var reset = IsRuntimeListReset(method);
                    foreach (var list in lists)
                    {
                        var reason = $"Quest {instance.Quest.EditorID ?? instance.Quest.FormKey.ToString()} script {instance.Name} calls {method} on this list";
                        Control(list, reason);
                        // Retain the shared distinction: presence can witness resets,
                        // but cannot prove a list's current chance, levels or counts.
                        if (!reset) catalog.RuntimeMetadataMutations.Add(list);
                        if (target is not null && method.Equals("Revert", StringComparison.OrdinalIgnoreCase)
                            && Binding(instance, direct.Groups["recv"].Value, true).Problem is null
                            && SplitArguments(direct.Groups["args"].Value).Length == 0 && NativeListFrameContract(catalog, "Revert"))
                        {
                            if (!catalog.VerifiedRevertLists.TryGetValue(list, out var plugins))
                                catalog.VerifiedRevertLists[list] = plugins = [];
                            plugins.Add(instance.Quest.FormKey.ModKey);
                        }
                        else
                        {
                            catalog.OtherRuntimeMutations.Add(list);
                            Taint([list], instance, reason, target is null ? null : direct.Groups["recv"].Value, true, target is null);
                        }
                        if (reset)
                        {
                            reverted.TryAdd(list, reason);
                            if (!resetters.TryGetValue(list, out var who)) resetters[list] = who = [];
                            who.Add(instance);
                        }
                    }
                    Diag(instance, fname, raw, method, target is null ? $"{lists.Length} reachable list(s)" : targetLabel, "", "",
                        reset ? "Resets script additions" : "Changes list data", target is null ? "Receiver is not a list property; every list the script can reach is treated as affected." : "");
                    continue;
                }

                // AddForm: target list property, payload property, literal level and count.
                string Unresolved(string why, IEnumerable<FormKey> items, string payloadLabel)
                {
                    foreach (var item in items) Block(item, $"Unresolved runtime distribution in {instance.Name}: {why}");
                    Diag(instance, fname, raw, method, targetLabel, payloadLabel, "", "Unresolved", why);
                    return why;
                }
                if (target is { } insertedInto)
                {
                    catalog.ScriptAddTargets.Add(insertedInto);
                    if (!inserters.TryGetValue(insertedInto, out var who)) inserters[insertedInto] = who = [];
                    who.Add(instance);
                }
                if (!direct.Success || target is null)
                {
                    catalog.OtherRuntimeMutations.UnionWith(ScriptLists(instance));
                    Taint(ScriptLists(instance), instance, "AddForm target is not a leveled-list property: " + raw.Trim(), flow: true);
                    catalog.UnresolvedScriptPayloads.UnionWith(ScriptLists(instance));
                    Unresolved("target is not a leveled-list property", ScriptItems(instance), ""); continue;
                }
                var args = SplitArguments(direct.Groups["args"].Value);
                if (args.Length != 3)
                {
                    catalog.ScriptUnresolvedReceivers.Add(target.Value);
                    catalog.UnresolvedScriptPayloads.UnionWith(ScriptLists(instance));
                    Unresolved("unrecognized AddForm arguments", ScriptItems(instance), ""); continue;
                }
                // A possible competing insertion can close a highest-eligible
                // source window even when that insertion's lifecycle is unproved.
                if (short.TryParse(args[1], out var possibleLevel) && possibleLevel >= 1)
                {
                    if (!catalog.ScriptReceiverLevels.TryGetValue(target.Value, out var levels)) catalog.ScriptReceiverLevels[target.Value] = levels = [];
                    levels.Add(possibleLevel);
                }
                else catalog.ScriptUnresolvedReceivers.Add(target.Value);
                var payloadName = Regex.Replace(args[0], @"(?i)\s+as\s+form\s*$", "").Trim();
                var propertyPayload = instance.Objects.TryGetValue(payloadName, out var payload);
                RuntimePropertyBinding? lookupBinding = null;
                var localArrayPayload = false;
                if (!propertyPayload)
                {
                    FormKey? lookup;
                    RuntimePropertyBinding lookupResult;
                    if (Regex.IsMatch(payloadName, @"^\w+\s*\["))
                    {
                        var arrayIdentity = FreshRuntimeArrayIdentity(payloadName, instance, function, position, catalog);
                        catalog.RuntimeLocalArrayIdentities.Add(arrayIdentity);
                        lookup = FreshLocalArrayPayload(arrayIdentity, catalog, out lookupResult);
                        localArrayPayload = true;
                    }
                    else lookup = ConstantRuntimeLookup(payloadName, instance, function, catalog, out lookupResult);
                    lookupBinding = lookupResult;
                    catalog.RuntimePayloadExpressions.Add(new(instance.Identity, fname, payloadName,
                        localArrayPayload ? (lookup is not null ? "Fresh local array read" : "Unresolved array expression")
                            : Regex.IsMatch(payloadName, @"(?i)^Game\s*\.\s*GetFormFromFile\b") ? "Constant native lookup" : "Unresolved expression",
                        lookup is { } found ? [found.ToString()] : [], true,
                        lookupResult.Problem, lookupResult.Evidence));
                    if (lookup is null)
                    {
                        catalog.UnresolvedScriptPayloads.UnionWith(ScriptLists(instance));
                        Unresolved("payload is not a property; " + lookupResult.Problem, ScriptItems(instance), payloadName); continue;
                    }
                    payload = lookup.Value;
                }
                // Possible fixed payloads are useful for proving a competitor
                // always contributes zero selected sources. They are not unlocks.
                var receiverBinding = Binding(instance, direct.Groups["recv"].Value, true);
                if (receiverBinding.Problem is null)
                {
                    var holderProblem = UnclosedRuntimeHolderProblem([], catalog, target.Value);
                    catalog.RuntimeHolderChecks.Add(new(instance.Identity, target.Value, holderProblem));
                    if (holderProblem is not null) receiverBinding = new(holderProblem, true);
                }
                var payloadBinding = lookupBinding ?? Binding(instance, payloadName, false);
                var literalLevel = short.TryParse(args[1], out var footprintLevel) && footprintLevel > 0;
                var literalCount = short.TryParse(args[2], out var footprintCount) && footprintCount > 0;
                var literalPayload = literalLevel && literalCount;
                if (receiverBinding.Problem is not null || payloadBinding.Problem is not null || !literalPayload)
                {
                    var reason = receiverBinding.Problem ?? payloadBinding.Problem ?? "positive literal AddForm level/count required";
                    catalog.RuntimeAddPayloadProblems.TryAdd(target.Value, $"{instance.Label}: {reason}");
                    if (receiverBinding.MayRetarget || payloadBinding.MayRetarget)
                        foreach (var list in BindingFrameLists(instance))
                        {
                            catalog.RuntimeAddPayloadProblems.TryAdd(list, $"{instance.Label}: {reason}");
                            Control(list, $"Runtime AddForm binding is unproved in {instance.Label}");
                            catalog.OtherRuntimeMutations.Add(list);
                        }
                    if (receiverBinding.MayRetarget) Taint([target.Value], instance, receiverBinding.Problem!, direct.Groups["recv"].Value, true);
                    if (payloadBinding.MayRetarget)
                        Taint(propertyPayload ? PropertyLists(instance, payloadName) : ScriptLists(instance), instance,
                            payloadBinding.Problem!, propertyPayload ? payloadName : null);
                }
                else
                {
                    if (!catalog.RuntimeAddPayloads.TryGetValue(target.Value, out var payloads)) catalog.RuntimeAddPayloads[target.Value] = payloads = [];
                    payloads.Add(new(payload, $"{instance.Label}.{fname}, level {footprintLevel}, count {footprintCount}; {instance.Evidence}; receiver {receiverBinding.Evidence}; payload {payloadBinding.Evidence}"));
                }
                // Even a conditional/unverified call is a possible incoming route.
                // Retain the inserted list itself; leaf unlocks cannot express this edge.
                if (catalog.Lists.ContainsKey(payload))
                {
                    if (!catalog.ScriptInsertionParents.TryGetValue(payload, out var incoming))
                        catalog.ScriptInsertionParents[payload] = incoming = [];
                    incoming.Add(target.Value);
                }
                var payloadLabel = catalog.Lists.ContainsKey(payload) ? Name(payload) : payload.ToString();
                var trace = TracePayload(payload, catalog);
                if (!short.TryParse(args[1], out var level) || level < 1 || !short.TryParse(args[2], out var count) || count < 1)
                { Unresolved("level or count is not a positive literal", trace.Leaves.Keys.Concat(trace.Blocked.Keys), payloadLabel); continue; }
                if (trace.Invalid is not null) { Unresolved(trace.Invalid, trace.Leaves.Keys.Concat(trace.Blocked.Keys), payloadLabel); continue; }
                if (receiverBinding.Problem is not null || payloadBinding.Problem is not null)
                {
                    Unresolved("runtime property binding frame is unproved: " + (receiverBinding.Problem ?? payloadBinding.Problem),
                        trace.Leaves.Keys.Concat(trace.Blocked.Keys), payloadLabel);
                    continue;
                }
                if (!propertyPayload)
                {
                    Unresolved((localArrayPayload ? "Fresh local array evidence" : "Native lookup")
                        + " proves a possible payload identity or None only; execution, successful lookup and insertion lifecycle remain unproved.",
                        trace.Leaves.Keys.Concat(trace.Blocked.Keys), payloadLabel);
                    continue;
                }
                var triggers = function is null ? [] : EntryTriggers(instance, function, position - function.BodyStart);
                if (triggers.Count == 0 && function is not null && function.Kind == "function"
                    && !instance.Quest.VirtualMachineAdapter!.Fragments.Any(x => x.ScriptName.Equals(instance.Name, StringComparison.OrdinalIgnoreCase)
                        && x.FragmentName.Equals(function.Name, StringComparison.OrdinalIgnoreCase)))
                    triggers = CallerTriggers(instance, function, position - function.BodyStart);
                if (triggers.Count == 0)
                {
                    if (!stageRetries.TryGetValue(instance, out var retry)) stageRetries[instance] = retry = ProveStageRetry(instance, catalog,
                        quest => VerifyRuntimeStageGuard(quest, rawByQuest.GetValueOrDefault(quest, []), LoadInstance, catalog));
                    var guarded = function is not null && retry.Trigger is not null && retry.Calls.Any(c => c.Function == function.Name.ToLowerInvariant()
                        && Flow(instance, function).At(position - function.BodyStart).Statement?.Start == c.Offset);
                    if (guarded) triggers.Add(retry.Trigger!);
                    else
                    {
                        Unresolved(ConditionalRuntimeDescription(instance, function, position, all, catalog,
                            retry.Problem.Length > 0 ? retry.Problem : "no unconditional entry or proved conditional retry reaches this call", CallTargets),
                            trace.Leaves.Keys.Concat(trace.Blocked.Keys), payloadLabel);
                        continue;
                    }
                }
                foreach (var (item, why) in trace.Blocked) Block(item, $"{instance.Name}: {why}");
                foreach (var trigger in triggers)
                foreach (var (item, leaf) in trace.Leaves)
                    unlocks.Add((item, new ScriptUnlock(trigger.Quest, trigger.Stage, Math.Max(level, leaf.Gate), target.Value,
                        $"{trigger.Evidence}; {instance.Evidence}", (short)Math.Min(short.MaxValue, count * leaf.Count), trigger.AtStart)
                        { Payload = payload, OwnerQuest = trigger.OwnerQuest, InsertionLevel = level }));
                Diag(instance, fname, raw, method, targetLabel, payloadLabel,
                    string.Join(", ", triggers.Select(t => t.AtStart ? "game start" : $"stage {t.Stage}")),
                    "Verified", $"{trace.Leaves.Count} item(s) traced" + (trace.Blocked.Count > 0 ? $"; {trace.Blocked.Count} blocked inside the payload" : ""));
            }
        }

        bool Protected(ScriptUnlock u) => ForeignRevert(u.OriginalTarget, u, catalog)
            && RevertSwitchProblem(u.OriginalTarget, catalog) is null;
        // A later Revert/RemoveAddedForm on a destination can undo a script insertion there. Judge each
        // target separately: drop only insertions into resettable lists, and protect an item only when
        // none of its insertions survive.
        foreach (var group in unlocks.GroupBy(u => u.Item))
        {
            var lost = group.Where(u => reverted.ContainsKey(u.Unlock.OriginalTarget) && !Protected(u.Unlock)).ToArray();
            if (lost.Length == 0) continue;
            if (lost.Length == group.Count())
                Block(group.Key, $"Script insertion target can be reset: {reverted[lost[0].Unlock.OriginalTarget]}. "
                    + $"Light switch unavailable: {(ForeignRevert(lost[0].Unlock.OriginalTarget, lost[0].Unlock, catalog) ? RevertSwitchProblem(lost[0].Unlock.OriginalTarget, catalog) : "source-owned reset or other unverified mutations")}");
            else
                catalog.DistributionNotes.Add($"{group.Key}: {lost.Select(u => u.Unlock.OriginalTarget).Distinct().Count()} of its script insertion target(s) can be reset by another script and are ignored; the remaining target(s) are used.");
        }
        var kept = unlocks.Where(u => !reverted.ContainsKey(u.Unlock.OriginalTarget) || Protected(u.Unlock)).ToList();
        foreach (var u in kept.Where(u => reverted.ContainsKey(u.Unlock.OriginalTarget)))
            catalog.DistributionNotes.Add($"{u.Item}: verified foreign Revert on {Name(u.Unlock.OriginalTarget)}; any accepted runtime route to this destination requires a saved-global light switch. The source mod's own AddForm remains outside XEP protection.");

        // Mod conflicts that break another mod's script insertions in game, with or without XEP.
        string Who(ScriptInstance i) => $"{i.Name} ({i.Quest.EditorID ?? i.Quest.FormKey.ToString()}, {i.Quest.FormKey.ModKey.FileName})";
        foreach (var pair in resetters.SelectMany(r => r.Value.SelectMany(x => inserters.GetValueOrDefault(r.Key, []).Where(y => y.Quest.FormKey.ModKey != x.Quest.FormKey.ModKey)
                .Select(y => (Resetter: x, Inserter: y, List: r.Key))))
            .GroupBy(x => (Who(x.Resetter), Who(x.Inserter))).OrderBy(g => g.Key.Item1).ThenBy(g => g.Key.Item2))
        {
            var lists = pair.Select(x => x.List).Distinct().ToArray();
            catalog.ScriptConflicts.Add($"Script conflict: {pair.Key.Item1} resets {lists.Length} leveled list(s) that {pair.Key.Item2} adds items to "
                + $"({string.Join(", ", lists.Take(3).Select(Name))}{(lists.Length > 3 ? $", +{lists.Length - 3} more" : "")}). "
                + "In game, those additions are removed whenever the resetting script runs, with or without XEP.");
        }
        foreach (var (item, reason) in blocked) catalog.UnsafeAvailability[item] = reason;
        foreach (var (item, unlock) in kept.Where(u => !blocked.ContainsKey(u.Item)))
        {
            if (!catalog.ScriptUnlocks.TryGetValue(item, out var rules)) catalog.ScriptUnlocks[item] = rules = [];
            if (!rules.Contains(unlock)) rules.Add(unlock);
        }
        bool Equipment(FormKey k) => catalog.Weapons.ContainsKey(k) || catalog.Armors.ContainsKey(k);
        catalog.DistributionNotes.Add($"Availability analysis: {analysed.Values.Sum(x => x.Count)} quest/alias scripts analysed; {diagnostics.Count(d => d.Verdict == "Verified")} list insertions verified, {diagnostics.Count(d => d.Verdict == "Unresolved")} unresolved; "
            + $"{catalog.ScriptUnlocks.Keys.Count(Equipment)} equipment records have verified script insertions with supported execution lifecycles; {catalog.UnsafeAvailability.Keys.Count(Equipment)} are protected by unresolved script contexts; "
            + $"{catalog.RuntimeControlledLists.Count} leveled lists are reset, reconfigured or unverifiable. Ordinary quest reward references are consumers, not runtime controls.");
    }

    private sealed record PayloadLeaf(short Gate, int Count);
    private sealed record PayloadTrace(Dictionary<FormKey, PayloadLeaf> Leaves, Dictionary<FormKey, string> Blocked, string? Invalid);

    // Every record an AddForm payload can hand out, of any type. UseAll bundles and multi-count
    // entries are accepted. Problems that apply to the whole payload make the call unresolved;
    // an entry-level problem (ownership/condition data) only blocks the items beneath that entry.
    private static PayloadTrace TracePayload(FormKey payload, Catalog catalog)
    {
        var leaves = new Dictionary<FormKey, PayloadLeaf>(); var blocked = new Dictionary<FormKey, string>(); string? invalid = null;
        void Visit(FormKey key, short gate, int count, HashSet<FormKey> path, string? entryProblem)
        {
            if (invalid is not null) return;
            if (!path.Add(key)) { invalid = "payload list is cyclic"; return; }
            if (catalog.Lists.TryGetValue(key, out var l))
            {
                if (l.IsDeleted) invalid = $"payload list {l.EditorID} is deleted";
                else if (l.Global.IsNull && l.ChanceNone.Value >= 1) invalid = $"payload list {l.EditorID} never yields an item";
                else
                    foreach (var e in l.Entries ?? [])
                        if (e.Data is { } d)
                            Visit(d.Reference.FormKey, Math.Max(gate, SelectionEntryMinimum(l, d.Level)), count * Math.Max((int)d.Count, 1), path,
                                entryProblem ?? (e.ExtraData is not null ? $"payload entry in {l.EditorID} carries ownership/condition data" : null));
            }
            else if (entryProblem is not null) blocked.TryAdd(key, entryProblem);
            else if (!leaves.TryGetValue(key, out var old) || gate < old.Gate) leaves[key] = new(gate, count);
            path.Remove(key);
        }
        Visit(payload, 1, 1, [], null);
        foreach (var key in blocked.Keys) leaves.Remove(key);
        return new(leaves, blocked, invalid);
    }

    private static List<ScriptFunction> ParseFunctions(string text)
    {
        var result = new List<ScriptFunction>();
        foreach (Match m in Regex.Matches(text, @"(?ims)^[ \t]*(?:(?<return>[\w\[\]]+)[ \t]+)?(?<kind>function|event)[ \t]+(?<name>\w+)[ \t]*\((?<params>[^)\n]*)\)(?![^\n]*\bnative\b)[^\n]*\n(?<body>.*?)^[ \t]*end(?:function|event)\b"))
            result.Add(new(m.Groups["name"].Value, m.Groups["kind"].Value.ToLowerInvariant(), m.Groups["params"].Value, m.Groups["body"].Value,
                m.Index, m.Index + m.Length) { BodyStart = m.Groups["body"].Index, ReturnType = m.Groups["return"].Value });
        return result;
    }

    // Declared Papyrus type of a local, parameter or property name, if the decompiled text states one.
    private static string? DeclaredType(string text, ScriptFunction function, string name)
    {
        var n = Regex.Escape(name);
        var local = Regex.Match(function.Body, @"(?im)^\s*(\w+)(?:\[\])?\s+" + n + @"\s*(=|$)");
        if (local.Success) return local.Groups[1].Value;
        var parameter = Regex.Match(function.Parameters, @"(?i)(?:^|,)\s*(\w+)(?:\[\])?\s+" + n + @"\b");
        if (parameter.Success) return parameter.Groups[1].Value;
        var property = Regex.Match(text, @"(?im)^\s*(\w+)(?:\[\])?\s+property\s+" + n + @"\b");
        return property.Success ? property.Groups[1].Value : null;
    }

    private static string[] SplitArguments(string args)
    {
        var parts = new List<string>(); var depth = 0; var start = 0;
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == '(') depth++;
            else if (args[i] == ')') depth--;
            else if (args[i] == ',' && depth == 0) { parts.Add(args[start..i].Trim()); start = i + 1; }
        }
        parts.Add(args[start..].Trim());
        return parts.Where(p => p.Length > 0).ToArray();
    }

    private static bool HasStageConditions(IQuestGetter quest, ushort stage)
    {
        // A stage may have multiple conditioned log entries. GetStageDone alone cannot prove which fragment ran.
        var entries = quest.Stages.Where(s => s.Index == stage).SelectMany(s => s.LogEntries ?? []).ToArray();
        return entries.Length > 1 || entries.Any(e => e.Conditions.Count > 0);
    }

    // Both original and already-enchanted source variants may establish distribution controls.
    private static FormKey[] SourceGlobalControls(FormKey source, Catalog catalog)
        => GlobalControlsFrom(source, (catalog.SourceEquipment?.GetValueOrDefault(source, []) ?? [])
            .Select(item => item.FormKey).Append(source), catalog);

    private static FormKey[] GlobalControlsFrom(FormKey source, IEnumerable<FormKey> items, Catalog catalog)
    {
        var seen=new HashSet<FormKey>(); var globals=new HashSet<FormKey>();
        var queue=new Queue<FormKey>(items);
        while(queue.TryDequeue(out var key))
        {
            if(!seen.Add(key))continue;
            foreach(var p in catalog.DirectParents.GetValueOrDefault(key,[]))
            {
                var list=catalog.Lists[p.List];
                if(!list.Global.IsNull)globals.Add(list.Global.FormKey);
                // Stop at cross-mod consumer contexts. Their merchant/drop settings do not define the item's availability.
                if(p.List.ModKey==source.ModKey)queue.Enqueue(p.List);
            }
        }
        return globals.OrderBy(k=>k).ToArray();
    }

    // Omit a second application only when every currently known incoming route
    // already passes the same global. Direct consumers and unresolved roots make
    // the proof fail closed; one controlled ancestor is not sufficient.
    private static bool GlobalGuaranteedAt(FormKey target, FormKey global, Catalog catalog)
    {
        if (global.IsNull) return false;
        return ControlGuaranteedAt(target, catalog, (_, list) => list.Global.FormKey == global);
    }

    // Equal LVLG values on different lists are independent engine evaluations.
    // Omit a source application only when the destination already passes through
    // the same imposing list (or one of its context-isolation copies).
    private static bool ProvenanceGlobalGuaranteedAt(FormKey target, FormKey imposingList, FormKey global, Catalog catalog)
    {
        if (global.IsNull || imposingList.IsNull) return false;
        return ControlGuaranteedAt(target, catalog,
            (key, list) => SuppliesGlobalApplication(key, list, imposingList, global, catalog));
    }

    private static bool SuppliesGlobalApplication(FormKey key, ILeveledItemGetter list,
        FormKey imposingList, FormKey global, Catalog catalog)
    {
        if (list.Global.FormKey != global) return false;
        if (catalog.MaterialGlobalApplications.GetValueOrDefault(key) == (imposingList, global)) return true;
        var seen = new HashSet<FormKey>();
        while (seen.Add(key) && catalog.SpecializedCloneOriginals.TryGetValue(key, out var original)) key = original;
        return key == imposingList && !catalog.RuntimeControlledLists.ContainsKey(key);
    }

    private static bool FixedChanceGuaranteedAt(FormKey target, MaterialChance chance, Catalog catalog)
        => ControlGuaranteedAt(target, catalog, (key, list) =>
        {
            if (!list.Global.IsNull || list.ChanceNone.Value != chance.Value) return false;
            if (catalog.MaterialChanceApplications.GetValueOrDefault(key) == chance) return true;
            var seen = new HashSet<FormKey>();
            while (seen.Add(key) && catalog.SpecializedCloneOriginals.TryGetValue(key, out var original)) key = original;
            return key == chance.List && !catalog.RuntimeControlledLists.ContainsKey(key);
        });

    private sealed partial class Catalog
    {
        public ListReferenceIndex? ChangedListParents { get; set; }
        // Material expansion's graph: every current list's entries (deleted lists skipped) and
        // its own edge/copy views, refreshed only at the start of each expansion call so its
        // snapshot is not advanced by other planners' refreshes mid-call.
        public ListReferenceIndex? AllListParents { get; set; }
        public PlanningEdgeIndex? ExpansionPlanningEdges { get; set; }
        public CloneCopyIndex? ExpansionCloneCopies { get; set; }
        public PlanningEdgeIndex? PlanningEdges { get; set; }
        public void ListReferencesChanged(FormKey list)
        {
            AllListParents?.Changed(list);
            ChangedListParents?.Changed(list);
            CopyTopology.ReferencesChanged(list);
        }
    }

    private static void MarkEntryReferencesChanged(FormKey list, Catalog catalog)
        => catalog.ListReferencesChanged(list);

    // Own copy mappings so every membership/replacement change invalidates their
    // reverse views. Entry edits use MarkEntryReferencesChanged. Availability
    // verdicts and planned edges remain live; only these structural views are reused.
    private sealed class PlanningCopyTopology
    {
        private readonly Dictionary<FormKey, FormKey> originals = [], families = [];
        public IReadOnlyDictionary<FormKey, FormKey> Originals { get; }
        public IReadOnlyDictionary<FormKey, FormKey> Families { get; }
        private readonly ListReferenceIndex parents = new(PlanningListScope.Copies);
        private readonly CloneCopyIndex copies = new();
        private readonly Dictionary<FormKey, int> trackedLists = [];
        public event Action<FormKey, FormKey?, FormKey?>? CloneChanged;
        public event Action<FormKey>? FamilyChanged;

        public PlanningCopyTopology()
        {
            Originals = new System.Collections.ObjectModel.ReadOnlyDictionary<FormKey, FormKey>(originals);
            Families = new System.Collections.ObjectModel.ReadOnlyDictionary<FormKey, FormKey>(families);
        }
        public void RegisterClone(FormKey copy, FormKey original)
        {
            var existed = originals.TryGetValue(copy, out var prior);
            if (existed && prior == original) return;
            originals[copy] = original;
            if (!existed) Track(copy, 1);
            copies.Changed(copy);
            CloneChanged?.Invoke(copy, existed ? prior : null, original);
        }
        public void PreserveFamily(FormKey selector, FormKey contents)
        {
            if (families.TryGetValue(selector, out var prior)) Track(prior, -1);
            families[selector] = contents;
            Track(contents, 1);
            // The selector's entries were moved into contents, including when the
            // selector is itself a copy. Its record replacement must be observed.
            parents.Changed(selector);
            FamilyChanged?.Invoke(selector);
        }
        public bool Tracks(FormKey list) => trackedLists.ContainsKey(list);
        public void ReferencesChanged(FormKey list)
        { if (Tracks(list)) parents.Changed(list); }
        private void Track(FormKey list, int amount)
        {
            var count = trackedLists.GetValueOrDefault(list) + amount;
            if (count == 0) trackedLists.Remove(list); else trackedLists[list] = count;
            parents.Changed(list);
        }
        public void Prune(IReadOnlySet<FormKey> removed)
        {
            if (removed.Count == 0) return;
            foreach (var key in families.Keys.Where(k => removed.Contains(k) || removed.Contains(families[k])).ToArray())
            {
                var contents = families[key]; families.Remove(key); Track(contents, -1);
                FamilyChanged?.Invoke(key);
            }
            foreach (var key in originals.Keys.Where(removed.Contains).ToArray())
            {
                var prior = originals[key]; originals.Remove(key);
                Track(key, -1); copies.Changed(key);
                CloneChanged?.Invoke(key, prior, null);
            }
        }
        public ListReferenceIndex Parents(Catalog catalog) => parents.Refresh(catalog);
        public ILookup<FormKey, FormKey> Copies(Catalog catalog) => copies.Refresh(catalog);
    }

    private enum PlanningListScope { All, Copies, ChangedStock }

    // Each reader consumes its own pending keys only at its existing refresh point.
    // Bootstrap enumerates the scope once; later captures never rediscover unchanged
    // records. Parent order remains first-indexed order, as with the previous index.
    private sealed class ListReferenceIndex(PlanningListScope scope)
    {
        private readonly Dictionary<FormKey, FormKey[]> references = [];
        private readonly Dictionary<FormKey, List<FormKey>> parents = [];
        private readonly HashSet<FormKey> pending = [];
        private bool initialized;

        public void Changed(FormKey list) { if (initialized) pending.Add(list); }
        public ListReferenceIndex Refresh(Catalog catalog)
        {
            if (!initialized)
            {
                foreach (var list in Members(catalog)) pending.Add(list);
                initialized = true;
            }
            foreach (var list in pending)
            {
                if (references.Remove(list, out var prior))
                    foreach (var child in prior)
                    {
                        var owners = parents[child]; owners.Remove(list);
                        if (owners.Count == 0) parents.Remove(child);
                    }
                if (!Includes(catalog, list)) continue;
                var record = catalog.Lists.GetValueOrDefault(list);
                var entries = record is not null && (scope == PlanningListScope.Copies || !record.IsDeleted)
                    ? record.Entries ?? [] : [];
                var children = entries.Where(e => e.Data is { } data && (scope != PlanningListScope.ChangedStock || data.Count > 0))
                    .Select(e => e.Data!.Reference.FormKey).Distinct().ToArray();
                references[list] = children;
                foreach (var child in children)
                {
                    if (!parents.TryGetValue(child, out var owners)) parents[child] = owners = [];
                    owners.Add(list);
                }
            }
            pending.Clear();
            return this;
        }

        private IEnumerable<FormKey> Members(Catalog catalog) => scope switch
        {
            PlanningListScope.All => catalog.Lists.Keys,
            PlanningListScope.Copies => catalog.SpecializedCloneOriginals.Keys.Concat(catalog.MaterialFamilyOriginals.Values),
            _ => catalog.Fallbacks.Keys.Concat(catalog.SpecializedRewrites.Keys)
        };
        private bool Includes(Catalog catalog, FormKey list) => scope switch
        {
            PlanningListScope.All => catalog.Lists.ContainsKey(list),
            PlanningListScope.Copies => catalog.CopyTopology.Tracks(list),
            _ => catalog.Fallbacks.ContainsKey(list) || catalog.SpecializedRewrites.ContainsKey(list)
        };
        public bool Contains(FormKey list) => references.ContainsKey(list);
        public IEnumerable<FormKey> Parents(FormKey child) => parents.TryGetValue(child, out var owners) ? owners : [];
    }

    // Independent readers retain clone membership until their own next refresh.
    private sealed class CloneCopyIndex : ILookup<FormKey, FormKey>
    {
        private readonly Dictionary<FormKey, FormKey> clones = [];
        private readonly Dictionary<FormKey, List<FormKey>> copies = [];
        private readonly HashSet<FormKey> pending = [];
        private bool initialized;

        public void Changed(FormKey copy) { if (initialized) pending.Add(copy); }
        public CloneCopyIndex Refresh(Catalog catalog)
        {
            if (!initialized)
            {
                pending.UnionWith(catalog.SpecializedCloneOriginals.Keys);
                initialized = true;
            }
            foreach (var clone in pending)
            {
                var exists = catalog.SpecializedCloneOriginals.TryGetValue(clone, out var original);
                if (clones.TryGetValue(clone, out var prior))
                {
                    if (exists && prior == original) continue;
                    var old = copies[prior]; old.Remove(clone);
                    if (old.Count == 0) copies.Remove(prior);
                    clones.Remove(clone);
                }
                if (!exists) continue;
                clones[clone] = original;
                if (!copies.TryGetValue(original, out var list)) copies[original] = list = [];
                list.Add(clone);
            }
            pending.Clear();
            return this;
        }

        public IEnumerable<FormKey> this[FormKey key] => copies.TryGetValue(key, out var list) ? list : [];
        public int Count => copies.Count;
        public bool Contains(FormKey key) => copies.TryGetValue(key, out var list) && list.Count > 0;
        public IEnumerator<IGrouping<FormKey, FormKey>> GetEnumerator()
            => copies.Where(c => c.Value.Count > 0).SelectMany(c => c.Value.Select(v => (c.Key, v)))
                .GroupBy(x => x.Key, x => x.v).GetEnumerator();
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    // Most planning changes append immutable edge records. Collection's mutation
    // hooks also cover IList callers, so same-count replacements/removals cannot
    // leave an index stale. Readers consume replacement slots or positional shifts
    // at their own refresh boundary.
    private sealed class PlanningAdditions<T> : System.Collections.ObjectModel.Collection<T>
    {
        public event Action<T>? Added;
        public event Action<T>? Removed;
        public event Action<T, T>? Updated;
        public event Action<int>? Replaced;
        public int PositionShiftVersion { get; private set; }
        protected override void InsertItem(int index, T item)
        {
            var append = index == Count;
            base.InsertItem(index, item);
            if (!append) PositionShiftVersion++;
            Added?.Invoke(item);
        }
        protected override void SetItem(int index, T item)
        {
            var old = this[index]; base.SetItem(index, item);
            Updated?.Invoke(old, item);
            Replaced?.Invoke(index);
        }
        protected override void RemoveItem(int index)
        {
            var old = this[index]; base.RemoveItem(index); PositionShiftVersion++;
            Removed?.Invoke(old);
        }
        protected override void ClearItems()
        {
            if (Removed is not null) foreach (var item in this) Removed(item);
            base.ClearItems(); PositionShiftVersion++;
        }
        public void AddRange(IEnumerable<T> items)
        { foreach (var item in items) Add(item); }
        public void RemoveAll(Predicate<T> match)
        { for (var i = Count - 1; i >= 0; i--) if (match(this[i])) RemoveAt(i); }
        public void RemoveRange(int index, int count)
        { for (var i = index + count - 1; i >= index; i--) RemoveAt(i); }
    }

    // Per-reader pending slots. Replacing one record must not force discovery of
    // every preceding record; a real positional shift still needs one rebuild.
    private sealed class PlanningCollectionChanges<T>
    {
        private PlanningAdditions<T>? owner;
        private readonly HashSet<int> replaced = [];
        private int count, shifts;
        private void Replaced(int index) { if (index < count) replaced.Add(index); }
        public bool Changed(PlanningAdditions<T> items) => !ReferenceEquals(owner, items)
            || count != items.Count || shifts != items.PositionShiftVersion || replaced.Count > 0;

        public void Refresh(PlanningAdditions<T> items, Action clear, Action<int, T> update)
        {
            if (!ReferenceEquals(owner, items))
            {
                if (owner is not null) owner.Replaced -= Replaced;
                owner = items; owner.Replaced += Replaced;
                count = 0; shifts = items.PositionShiftVersion; replaced.Clear(); clear();
            }
            if (shifts != items.PositionShiftVersion)
            { count = 0; shifts = items.PositionShiftVersion; replaced.Clear(); clear(); }
            foreach (var index in replaced) update(index, items[index]);
            for (var i = count; i < items.Count; i++) update(i, items[i]);
            count = items.Count; replaced.Clear();
        }
    }

    // Ordered positions for writers that edit existing slots. Metadata-only
    // replacements preserve the key groups; only affected slots are considered.
    private sealed class PlanningPositions<T>(Func<T, FormKey> key)
    {
        private readonly List<FormKey> keys = [];
        private readonly Dictionary<FormKey, List<int>> positions = [];
        private readonly PlanningCollectionChanges<T> changes = new();

        public IReadOnlyDictionary<FormKey, List<int>> Refresh(PlanningAdditions<T> items)
        {
            if (changes.Changed(items)) changes.Refresh(items, Clear, Update);
            return positions;
        }
        private void Clear() { keys.Clear(); positions.Clear(); }
        private void Update(int index, T item)
        {
            var current = key(item);
            if (index < keys.Count)
            {
                if (keys[index] == current) return;
                Remove(keys[index], index); keys[index] = current;
            }
            else keys.Add(current);
            if (!positions.TryGetValue(current, out var at)) positions[current] = at = [];
            at.Insert(~at.BinarySearch(index), index);
        }
        private void Remove(FormKey key, int index)
        {
            var at = positions[key];
            at.RemoveAt(at.BinarySearch(index));
            if (at.Count == 0) positions.Remove(key);
        }
    }

    // Immutable records grouped in original collection order. The records, as
    // well as their keys, stay at the reader's last refresh until it refreshes again.
    private sealed class PlanningRecordIndex<T>(Func<T, FormKey> key)
    {
        private readonly List<FormKey> keys = [];
        private readonly Dictionary<FormKey, SortedList<int, T>> groups = [];
        private readonly PlanningCollectionChanges<T> changes = new();
        public void Refresh(PlanningAdditions<T> items)
        { if (changes.Changed(items)) changes.Refresh(items, Clear, Update); }
        public IEnumerable<T> Items(FormKey child) => groups.TryGetValue(child, out var values) ? values.Values : [];
        private void Clear() { keys.Clear(); groups.Clear(); }
        private void Update(int index, T item)
        {
            var current = key(item);
            if (index < keys.Count)
            {
                if (keys[index] != current)
                {
                    var prior = groups[keys[index]]; prior.Remove(index);
                    if (prior.Count == 0) groups.Remove(keys[index]);
                    keys[index] = current;
                }
            }
            else keys.Add(current);
            if (!groups.TryGetValue(current, out var values)) groups[current] = values = [];
            values[index] = item;
        }
    }

    private sealed class PlanningEdgeIndex
    {
        private readonly PlanningRecordIndex<FallbackEdge> edges = new(e => e.Child);
        private readonly PlanningRecordIndex<RuntimePlacement> placements = new(p => p.Child);

        public PlanningEdgeIndex Refresh(Catalog catalog)
        {
            edges.Refresh(catalog.FallbackEdges); placements.Refresh(catalog.RuntimePlacements);
            return this;
        }
        public IEnumerable<FallbackEdge> EdgesTo(FormKey child) => edges.Items(child);
        public IEnumerable<RuntimePlacement> PlacementsTo(FormKey child) => placements.Items(child);
    }

    // Whether a list has an entry referencing the key. A binary overlay never changes,
    // so its entry references are read once; a mutable in-memory list is read each time.
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<ILeveledItemGetter, HashSet<FormKey>> FrozenEntryReferences = new();
    private static bool ListReferences(ILeveledItemGetter list, FormKey key)
    {
        if (list is LeveledItem) return (list.Entries ?? []).Any(e => e.Data?.Reference.FormKey == key);
        var references = FrozenEntryReferences.TryGetValue(list, out var known) ? known
            : FrozenEntryReferences.GetValue(list, static l => (l.Entries ?? []).Where(e => e.Data is not null).Select(e => e.Data!.Reference.FormKey).ToHashSet());
        return references.Contains(key);
    }

    private static bool ControlGuaranteedAt(FormKey target, Catalog catalog, Func<FormKey, ILeveledItemGetter, bool> suppliesControl)
    {
        using var timing = PhaseTiming.Count("availability.control-guarantee");
        var graph = SpecializedGraph(catalog);
        // The walk's tables come from the thread's pool; a re-entrant call allocates its own.
        var memo = pooledGuaranteeMemo ?? new Dictionary<FormKey, bool>(); pooledGuaranteeMemo = null;
        var active = pooledGuaranteeActive ?? new HashSet<FormKey>(); pooledGuaranteeActive = null;
        ILookup<FormKey, FormKey>? copies = null;
        ListReferenceIndex? copied = null; PlanningEdgeIndex? edges = null;
        // Refresh the shared reverse views at most once per call, and only when an
        // authored path leaves the answer open. Orders match a full per-list rescan.
        void Index()
        {
            if (copied is not null) return;
            copies = catalog.CopyTopology.Copies(catalog);
            copied = catalog.CopyTopology.Parents(catalog);
            edges = (catalog.PlanningEdges ??= new()).Refresh(catalog);
        }
        bool Visit(FormKey key)
        {
            if (memo.TryGetValue(key, out var found)) return found;
            if (!catalog.Lists.TryGetValue(key, out var list)) return false;
            if (catalog.RuntimeControlledLists.ContainsKey(key)) return false;
            if (suppliesControl(key, list)) return true;
            if (catalog.UnresolvedScriptPayloads.Contains(key)) return false;
            if (!active.Add(key)) return false;
            try
            {
                foreach (var use in graph.Uses.GetValueOrDefault(key, [])) if (!use.ScopeOnly) return memo[key] = false;
                // Authored parents come first, as before; any unguaranteed one decides
                // the result, so the reverse view is built only when they all pass.
                // Distinct lists in first-appearance order, visited in that order, as the
                // former Distinct/All pipeline did; no iterator or array per node.
                var parents = new List<FormKey>();
                foreach (var parent in catalog.DirectParents.GetValueOrDefault(key, []))
                    if (ListReferences(catalog.Lists[parent.List], key) && !parents.Contains(parent.List)) parents.Add(parent.List);
                var authoredCount = parents.Count;
                for (var i = 0; i < authoredCount; i++) if (!Visit(parents[i])) return memo[key] = false;
                Index();
                void AddDistinct(FormKey list) { if (!parents.Contains(list)) parents.Add(list); }
                foreach (var parent in copied!.Parents(key)) AddDistinct(parent);
                foreach (var edge in edges!.EdgesTo(key))
                {
                    AddDistinct(edge.Parent.List);
                    if (InheritableEdge(edge, catalog))
                        foreach (var target in InheritedCopyTargets(edge.Parent.List, copies!, catalog)) AddDistinct(target);
                }
                foreach (var placement in edges.PlacementsTo(key))
                {
                    AddDistinct(placement.Target.List);
                    foreach (var target in InheritedCopyTargets(placement.Target.List, copies!, catalog)) AddDistinct(target);
                }
                foreach (var parent in catalog.ScriptInsertionParents.GetValueOrDefault(key, [])) AddDistinct(parent);
                if (parents.Count == 0) return memo[key] = false;
                foreach (var parent in parents) if (!Visit(parent)) return memo[key] = false;
                return memo[key] = true;
            }
            finally { active.Remove(key); }
        }
        try { return Visit(target); }
        finally { memo.Clear(); active.Clear(); pooledGuaranteeMemo = memo; pooledGuaranteeActive = active; }
    }
    [ThreadStatic] private static Dictionary<FormKey, bool>? pooledGuaranteeMemo;
    [ThreadStatic] private static HashSet<FormKey>? pooledGuaranteeActive;

    // Ordinary quest insertion has no actor-level maximum mechanism. Reuse the
    // payload window proof; a controller's minimum alone cannot preserve a cutoff.
    private static string? OrdinaryScriptWindowProblem(FormKey source, ScriptUnlock unlock, Catalog catalog)
    {
        if (!catalog.Lists.TryGetValue(unlock.OriginalTarget, out var receiver)) return "original runtime receiver is missing";
        if (catalog.RuntimeControlledLists.ContainsKey(unlock.OriginalTarget)
            && !(ForeignRevert(unlock.OriginalTarget, unlock, catalog) && RevertSwitchProblem(unlock.OriginalTarget, catalog) is null))
            return "original runtime receiver is mutated or unverifiable without a supported Revert switch";
        if (HasHighestEligibleCutoff(receiver) && catalog.ScriptUnresolvedReceivers.Contains(unlock.OriginalTarget))
            return "highest-eligible runtime receiver has an insertion with unknown level";
        var payload = PayloadPathControls(unlock.Payload.IsNull ? source : unlock.Payload, source, catalog, requireStaticPayload: true);
        if (payload.Problem is not null) return payload.Problem;
        var insertionLevel = unlock.InsertionLevel ?? unlock.Level;
        var maximum = SelectionEntryMaximum(receiver, insertionLevel, catalog.ScriptReceiverLevels.GetValueOrDefault(unlock.OriginalTarget, []));
        var window = IntersectBands(payload.Window, [(insertionLevel, maximum)]);
        return window.Count != 1 || window[0].Maximum < ushort.MaxValue
            ? "finite or disjoint runtime source window cannot be preserved by an ordinary quest insertion" : null;
    }

    private static void ProtectSourceEdges(FormKey source, FormKey global, ScriptUnlock? unlock, int firstEdge, Catalog catalog,
        IReadOnlySet<FallbackEdge>? protectedEdges = null, MaterialAvailabilityPath? availability = null)
    {
        foreach(var edge in catalog.FallbackEdges.Skip(firstEdge).Where(e=>!catalog.Fallbacks.ContainsKey(e.Parent.List) && protectedEdges?.Contains(e) != true).ToArray())
        {
            if (availability is not null)
            {
                catalog.FallbackEdges.Remove(edge);
                var target = unlock is null ? edge.Parent : edge.Parent with { Level = Math.Max(edge.Parent.Level, unlock.Level) };
                AttachOtherMaterialBranch(source, new(target, edge.Child, availability, unlock), catalog, share: true);
                continue;
            }
            if(!global.IsNull && catalog.Lists[edge.Parent.List].Global.FormKey != global)
            {
                var root=(LeveledItem)catalog.Lists[edge.Child];
                root.Global.SetTo(global);
                catalog.DistributionNotes.Add($"{source}: generated branch {root.EditorID} shares source LVLG {global}; changes from the source MCM/quest apply directly.");
            }
            if(unlock is not null)
            {
                catalog.FallbackEdges.Remove(edge);
                var target=edge.Parent with {Level=Math.Max(edge.Parent.Level,unlock.Level)};
                catalog.RuntimePlacements.Add(new(source,target,edge.Child,unlock));
                catalog.DistributionNotes.Add($"Runtime: {source} → {catalog.Lists[target.List].EditorID}, level {target.Level}, after {unlock.Quest} stage {unlock.Stage}. Evidence: {unlock.Evidence}");
            }
        }
    }

    // Every script name availability reads from the Data folder: the winning quests' own
    // and alias scripts, the base scripts, every winning record's attached scripts and
    // ActiveMagicEffect. Load-order facts only, so a scan can resolve them early.
    private static HashSet<string> AvailabilityScriptNames(IReadOnlyList<ISkyrimModGetter> mods,
        Dictionary<FormKey, (ISkyrimMajorRecordGetter Record, ModKey Plugin)> winners)
    {
        var quests = mods.SelectMany(m => m.Quests).GroupBy(q => q.FormKey).Select(g => g.Last())
            .Where(q => !q.IsDeleted && q.VirtualMachineAdapter is not null);
        var wanted = quests.SelectMany(q => q.VirtualMachineAdapter!.Scripts.Select(s => s.Name)
            .Concat(q.VirtualMachineAdapter.Aliases.SelectMany(a => a.Scripts.Select(s => s.Name)))).ToHashSet(StringComparer.OrdinalIgnoreCase);
        wanted.UnionWith(new[] { "Quest", "Alias", "ReferenceAlias", "Actor", "ObjectReference", "Debug", "Game", "LeveledItem", "Form", "Keyword", "GlobalVariable" });
        wanted.UnionWith(winners.Values.Where(w => !w.Record.IsDeleted)
            .Select(w => w.Record).OfType<IHaveVirtualMachineAdapterGetter>()
            .SelectMany(w => w.VirtualMachineAdapter?.Scripts.Select(s => s.Name) ?? []));
        wanted.Add("ActiveMagicEffect");
        return wanted;
    }

    // Script assets resolved in the background for one Data folder and plugin order.
    private sealed record PendingScriptAssets(string Data, ModKey[] Order, Task<(HashSet<string> Wanted, ScriptAssets Assets)> Work)
    {
        public static PendingScriptAssets Start(string data, IReadOnlyList<ISkyrimModGetter> mods,
            Dictionary<FormKey, (ISkyrimMajorRecordGetter Record, ModKey Plugin)> winners)
        {
            var order = mods.Select(m => m.ModKey).ToArray();
            return new(data, order, Task.Run(() =>
            {
                // Timing sessions are single-threaded; the scan times the wait instead.
                using var detached = PhaseTiming.Detach();
                var wanted = AvailabilityScriptNames(mods, winners);
                return (wanted, new ScriptAssets(data, order, wanted));
            }));
        }
    }

    private sealed class ScriptAssets
    {
        private readonly Dictionary<string, byte[]> bytes = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> evidence = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> unverified = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> diagnosticEvidence = new(StringComparer.OrdinalIgnoreCase);
        public string? AnalysisWarning { get; }
        private readonly string? compiler;
        private readonly string? compilerHash;
        // Scripts no record attaches (custom parents, export receivers) are indexed
        // by their winning archive and read only when an analysis asks for them.
        // The winning entry itself is kept, so a later read never reopens its archive.
        private readonly Dictionary<string, (string Path, IArchiveFile File)> archived = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> wanted;
        private readonly string data = "";
        private string? unreadableArchive;
        public ScriptAssets(string data, IEnumerable<ModKey> order, HashSet<string> wanted)
        {
            using var timing = PhaseTiming.Measure("availability.assets");
            this.wanted = wanted; this.data = data;
            var tool = new ScriptAnalysisTool();
            compiler = tool.ExecutablePath;
            AnalysisWarning = tool.Warning;
            compilerHash = compiler is null ? null : Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(compiler)));
            if(!Directory.Exists(data))return;
            // Associated archives follow plugin order; loose files win over every archive.
            // Every archive and loose file is read in parallel; the results are merged in
            // that order, so the winning entry and every diagnostic are unchanged.
            var mods = order.ToArray();
            var suffixes = new[]{".bsa", " - Main.bsa", " - Misc.bsa"};
            var archiveJobs = new (string Path, bool Observed, string? Failure, List<(string Name, IArchiveFile File, byte[]? Bytes)> Files)[mods.Length * suffixes.Length];
                ParallelInOrder(archiveJobs.Length, job =>
                {
                    var path=Path.Combine(data,Path.GetFileNameWithoutExtension(mods[job / suffixes.Length].FileName.String)+suffixes[job % suffixes.Length]);
                    var observedArchive = false;
                    var files = new List<(string Name, IArchiveFile File, byte[]? Bytes)>();
                    try
                    {
                        File.GetAttributes(path);
                        observedArchive = true;
                        var archive=Archive.CreateReader(GameRelease.SkyrimSE,path);
                        foreach(var file in archive.Files.Where(f=>f.Path.Replace('/','\\').StartsWith("scripts\\",StringComparison.OrdinalIgnoreCase)
                            && f.Path.EndsWith(".pex",StringComparison.OrdinalIgnoreCase)))
                        {
                            var name=Path.GetFileNameWithoutExtension(file.Path);
                            files.Add((name, file, wanted.Contains(name) ? file.GetBytes() : null));
                        }
                        archiveJobs[job] = (path, true, null, files);
                    }
                    catch(Exception ex) when(!observedArchive && ex is (FileNotFoundException or DirectoryNotFoundException)) { archiveJobs[job] = (path, false, null, files); }
                    catch(Exception ex) when(ex is IOException or InvalidDataException or UnauthorizedAccessException or ArchiveException)
                    { archiveJobs[job] = (path, observedArchive, $"winning archive candidate is unreadable: {path}; {ex.GetType().Name}", files); }
                });
            foreach(var (path, _, failure, files) in archiveJobs)
            {
                if(failure is not null)
                {
                    // Without an inventory, any wanted name may have a higher
                    // priority entry here. Do not certify retained earlier bytes.
                    foreach (var name in wanted) unverified[name] = failure;
                    unreadableArchive = failure;
                    continue;
                }
                foreach(var (name, file, fileBytes) in files)
                {
                    if(fileBytes is null){archived[name]=(path,file);continue;}
                    bytes[name]=fileBytes;evidence[name]=path;unverified.Remove(name);
                }
            }
            var looseNames = wanted.Where(name => Path.GetFileName(name) == name).ToArray();
            var looseJobs = new (byte[]? Bytes, string? Failure)[looseNames.Length];
                ParallelInOrder(looseNames.Length, job =>
                {
                    var loose=Path.Combine(data,"Scripts",looseNames[job]+".pex");
                    var observedLoose = false;
                    try
                    {
                        File.GetAttributes(loose);
                        observedLoose = true;
                        looseJobs[job] = (File.ReadAllBytes(loose), null);
                    }
                    catch(Exception ex) when(!observedLoose && ex is (FileNotFoundException or DirectoryNotFoundException)) { }
                    catch(Exception ex) when(ex is IOException or UnauthorizedAccessException)
                    { looseJobs[job] = (null, $"winning loose script is unreadable: {loose}; {ex.GetType().Name}"); }
                });
            for (var job = 0; job < looseNames.Length; job++)
            {
                var name = looseNames[job];
                var (looseBytes, failure) = looseJobs[job];
                if (looseBytes is not null) { bytes[name]=looseBytes;evidence[name]=Path.Combine(data,"Scripts",name+".pex");unverified.Remove(name); }
                else if (failure is not null) unverified[name] = failure;
            }
        }

        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string?> prefetchedTexts = new(StringComparer.Ordinal);
        private static string CacheDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"XEnchantmentPatcher","ScriptAnalysis","completed-v2");
        private string CacheKey(string name, byte[] data) => Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(
            name.ToLowerInvariant()+"|"+Convert.ToHexString(SHA256.HashData(data))+"|"+compilerHash)));

        // Read already completed analyses for these names in parallel. Decompile uses
        // a prefetched text exactly where the cache lookup would have read that file.
        public void Prefetch(IEnumerable<string> names)
        {
            if (compiler is null) return;
            var cache = CacheDirectory;
            var keys = names.Distinct(StringComparer.OrdinalIgnoreCase)
                .Where(name => !unverified.ContainsKey(name) && bytes.ContainsKey(name))
                .Select(name => CacheKey(name, bytes[name])).Distinct(StringComparer.Ordinal).ToArray();
            // Advisory only: a text that cannot be read here is left to Decompile's own
            // cache read, which reports it exactly as without the prefetch.
            Parallel.ForEach(keys, key =>
            {
                try
                {
                    var path = Path.Combine(cache, key + ".psc");
                    if (File.Exists(path)) prefetchedTexts[key] = AtomicFile.ReadText(path);
                }
                catch (Exception) { }
            });
        }

        public string? Decompile(string name,out string detail)
        {
            using var timing = PhaseTiming.Measure("availability.decompile");
            detail="PEX unavailable in the configured Data folder";
            if (unverified.TryGetValue(name, out var failure)) { detail = failure; return null; }
            if (!wanted.Contains(name) && !bytes.ContainsKey(name) && Path.GetFileName(name) == name)
            {
                if (unreadableArchive is not null) { detail = unreadableArchive; return null; }
                var loose = Path.Combine(this.data, "Scripts", name + ".pex");
                try
                {
                    if (File.Exists(loose)) { bytes[name] = File.ReadAllBytes(loose); evidence[name] = loose; }
                    else if (archived.TryGetValue(name, out var archivedFile))
                    { bytes[name] = archivedFile.File.GetBytes(); evidence[name] = archivedFile.Path; }
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArchiveException)
                { detail = $"winning script candidate is unreadable: {name}; {ex.GetType().Name}"; return null; }
            }
            if(!bytes.TryGetValue(name,out var data))return null;
            detail="Champollion.exe is required for script analysis (restore the bundled Champollion folder beside the patcher executable, or set XEP_CHAMPOLLION_PATH)";
            if(compiler is null)return null;
            var hash=Convert.ToHexString(SHA256.HashData(data));
            var key=Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(name.ToLowerInvariant()+"|"+hash+"|"+compilerHash)));
            var cache=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"XEnchantmentPatcher","ScriptAnalysis","completed-v2");
            failure="Decompiler could not analyze this PEX";
            var text=prefetchedTexts.TryGetValue(key,out var prefetched)?prefetched:CompletedScriptCache.Get(cache,key,() =>
            {
                var work=Path.Combine(cache,".work-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(work);
                try
                {
                    var pex=Path.Combine(work,name+".pex");var psc=Path.Combine(work,name+".psc");
                    File.WriteAllBytes(pex,data);
                    var start=ScriptAnalysisTool.DecompileStart(compiler,pex,work);
                    using var process=Process.Start(start)!;
                    var stdout=process.StandardOutput.ReadToEndAsync();var stderr=process.StandardError.ReadToEndAsync();
                    if(!process.WaitForExit(15000)){process.Kill(true);process.WaitForExit();failure="Decompiler timed out";return null;}
                    Task.WaitAll(stdout,stderr);
                    return process.ExitCode==0&&File.Exists(psc)?File.ReadAllText(psc):null;
                }
                finally
                {
                    var ownedWork=Path.GetFullPath(work);
                    var cacheRoot=Path.TrimEndingDirectorySeparator(Path.GetFullPath(cache))+Path.DirectorySeparatorChar;
                    if(!ownedWork.StartsWith(cacheRoot,StringComparison.OrdinalIgnoreCase))throw new IOException("Script work directory escaped its cache root.");
                    Directory.Delete(ownedWork,true);
                }
            });
            if(text is null){detail=failure;return null;}
            detail=$"{evidence[name]}; SHA256 {hash}";
            diagnosticEvidence[name]=$"{detail}; Decompiler SHA256 {compilerHash}; CacheKey {key}; PSC text UTF8 SHA256 {Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text)))}";
            return text;
        }
        public string DiagnosticEvidence(string name) => diagnosticEvidence.GetValueOrDefault(name, "Asset association unavailable; see proof evidence failure.");

    }
}
