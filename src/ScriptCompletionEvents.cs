using System.Globalization;
using System.Text.RegularExpressions;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;

namespace XEnchantmentPatcher;

public static partial class PatcherEngine
{
    private sealed record ScriptCompletion(string? Event, string? Problem, string[] Warnings);

    // Admission owns this proof; the controller only observes the accepted signal.
    // Never infer completion from an entry trigger or a menu closing.
    private static ScriptCompletion ScriptCompletionEvent(ScriptInstance instance, string functionName, Catalog catalog)
    {
        ScriptCompletion Refuse(string reason) => new(null, "source completion is not signalled by an event: " + reason, []);
        if (instance.Text is not { } text || Regex.IsMatch(MaskScriptStrings(text), @"(?im)^\s*(?:auto\s+)?state\b"))
            return Refuse("script state dispatch is unproved");
        // Resolve parents for dispatch only; completion timing still belongs to the
        // original function, not the concatenated parent/child bodies.
        var (dispatch, chainProblem) = SliceChainView(instance, catalog);
        if (dispatch is null) return Refuse("SendModEvent dispatch is unproved: " + chainProblem);
        // A self call dispatches through the sender's own chain; sibling scripts on
        // the same form are separate objects and cannot replace it.
        if (!NativeSendModEventContract(catalog, instance.Alias) || HasScriptMethod(dispatch.Text!, "SendModEvent"))
            return Refuse("SendModEvent dispatch is unproved");
        var functions = instance.Functions.Where(f => f.Name.Equals(functionName, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (functions.Length != 1) return Refuse("owning function is ambiguous");
        var function = functions[0];
        var warnings = new List<string>();
        var flow = Flow(instance, function);
        if (flow.Problem is { } flowProblem) return Refuse(flowProblem);
        var statements = CompletionStatements(flow.Nodes).ToArray();
        var lastMutation = statements.Where(n => MutationCall.IsMatch(MaskScriptStrings(n.Text))).Select(n => n.Start).DefaultIfEmpty(-1).Max();
        var firstReturn = statements.Where(n => Regex.IsMatch(MaskScriptStrings(n.Text), @"(?i)^return\b")).Select(n => n.Start).DefaultIfEmpty(int.MaxValue).Min();
        var direct = flow.Nodes.LastOrDefault(n => n.Kind == "statement" && CompletionSend(n.Text) is not null && n.Start > lastMutation && n.Start < firstReturn);
        if (direct is not null) return new(CompletionSend(direct.Text), null, []);
        // A single update on this script is the source's own continuation. Any other
        // registration/cancellation could replace it; no retry can repair that proof.
        var registrations = Regex.Matches(MaskScriptStrings(text), @"(?i)\b(?:UnregisterForUpdate|RegisterFor(?:Single)?Update)(?:GameTime)?\s*\(");
        var updates = flow.Nodes.Where(ScriptFlowAnalysis.SchedulesRetry).ToArray();
        if (updates.Length != 1 || updates[0].Start <= lastMutation || firstReturn < updates[0].Start || registrations.Count != 1)
            return Refuse("no final literal send or exclusive single-update continuation");
        var continuations = instance.Functions.Where(f => f.Name.Equals("OnUpdate", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (continuations.Length != 1) return Refuse("OnUpdate continuation is ambiguous or absent");
        var continuation = continuations[0];
        if (MutationCall.IsMatch(MaskScriptStrings(continuation.Body))) return Refuse("continuation changes leveled lists");
        var continuationFlow = Flow(instance, continuation);
        if (continuationFlow.Problem is { } continuationProblem) return Refuse(continuationProblem);
        var continuationStatements = CompletionStatements(continuationFlow.Nodes).ToArray();
        foreach (var node in continuationStatements)
        {
            if (CompletionSend(node.Text) is not { } eventName) continue;
            var context = continuationFlow.At(node.Start);
            if (context.Loop || continuationStatements.Any(n => n.Start < node.Start && Regex.IsMatch(MaskScriptStrings(n.Text), @"(?i)^return\b")))
                return Refuse("continuation send can be skipped by return or loop");
            foreach (var predicate in context.Predicates)
            {
                var inverse = predicate.StartsWith("!(", StringComparison.Ordinal) && predicate.EndsWith(')');
                var condition = inverse ? predicate[2..^1] : predicate;
                if (CompletionGuardProblem(instance, continuation, flow.Nodes, updates[0].Start, condition, !inverse, catalog, warnings) is { } guardProblem)
                    return Refuse($"completion guard is not established: {predicate} ({guardProblem})");
            }
            foreach (var other in catalog.RuntimeScriptInstances.Values.Where(i => i.Quest.FormKey == instance.Quest.FormKey && i.Identity != instance.Identity))
                if (other.Text is null || Regex.IsMatch(MaskScriptStrings(other.Text), @"(?i)\b(?:UnregisterForUpdate|RegisterFor(?:Single)?Update)\s*\("))
                    warnings.Add($"{instance.Label}: another script on the same form registers/cancels updates or is unverifiable ({other.Label}); per-script update registration is a pending runtime premise.");
            return new(eventName, null, warnings.Distinct().ToArray());
        }
        return Refuse("continuation has no literal completion send");
    }

    private static IEnumerable<ScriptFlowAnalysis.Node> CompletionStatements(IEnumerable<ScriptFlowAnalysis.Node> nodes)
    {
        foreach (var node in nodes)
        {
            if (node.Kind == "statement") yield return node;
            foreach (var nested in CompletionStatements(node.Then.Concat(node.Else))) yield return nested;
        }
    }

    private static string? CompletionSend(string line)
    {
        var match = Regex.Match(line, "(?i)^(?:self\\s*\\.\\s*)?SendModEvent\\s*\\(\\s*\"(?<name>[^\"\\\\]+)\"(?:\\s*,\\s*\"[^\"\\\\]*\"(?:\\s*,\\s*-?\\d+(?:\\.\\d+)?(?:\\s+as\\s+float)?)?)?\\s*\\)$");
        return match.Success ? match.Groups["name"].Value : null;
    }

    private static string? CompletionGuardProblem(ScriptInstance instance, ScriptFunction function, IEnumerable<ScriptFlowAnalysis.Node> sourceNodes, int updatePosition,
        string condition, bool positive, Catalog catalog, List<string> warnings)
    {
        var plugin = Regex.Match(condition, "(?i)^(?<not>!)?\\s*Game\\s*\\.\\s*GetFormFromFile\\s*\\(\\s*(?<id>0x[0-9a-f]+|\\d+)\\s*,\\s*\"(?<plugin>[^\"\\\\]+)\"\\s*\\)$");
        if (plugin.Success)
        {
            if (RuntimeLookupGameShadowed(instance, function)
                || !NativeFrameContract(catalog, "Game", "GetFormFromFile", "Form", ["Int", "String"], true)) return "plugin-presence lookup dispatch is unproved";
            var expression = condition.TrimStart('!', ' ');
            var id = plugin.Groups["id"].Value;
            if (id.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            {
                if (!int.TryParse(id[2..], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var literal) || literal < 0) return "plugin-presence lookup ID is not a literal";
                expression = expression.Replace(id, literal.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal);
            }
            var present = ConstantRuntimeLookup(expression, instance, function, catalog, out _) is not null;
            var required = positive != plugin.Groups["not"].Success;
            if (present != required) warnings.Add($"{instance.Label}: source completion event guard for plugin {plugin.Groups["plugin"].Value} takes the non-sending branch in this load order; gates refresh only at load and initialization until the source signals completion.");
            return null; // ADR 0007: follow source intent, warn about its plugin-presence defect.
        }
        var global = Regex.Match(condition, @"(?i)^(?<not>!)?\s*(?<property>\w+)\s*\.\s*GetValue(?:Int)?\(\s*\)$");
        if (!global.Success || condition.Contains("GetValueInt", StringComparison.OrdinalIgnoreCase)) return "condition is not a Global or plugin-presence test";
        var property = global.Groups["property"].Value;
        if (!instance.Objects.TryGetValue(property, out var key) || !StableAutoProperty(instance, property)
            || !AutoPropertyType(instance.Text!, property)!.Equals("GlobalVariable", StringComparison.OrdinalIgnoreCase)
            || !RuntimeRecordWinners(catalog).TryGetValue(key, out var globalRecord) || globalRecord.Record is not IGlobalGetter
            || !NativeFrameContract(catalog, "GlobalVariable", "GetValue", "Float", [], false)
            || !NativeFrameContract(catalog, "GlobalVariable", "SetValue", "", ["Float"], false)) return "guard is not a stable bound GlobalVariable";
        var zero = positive == global.Groups["not"].Success;
        bool Satisfies(string value) => float.TryParse(Regex.Replace(value.Trim(), @"(?i)\s+as\s+float\s*$", ""), NumberStyles.Float,
            CultureInfo.InvariantCulture, out var literal) && float.IsFinite(literal) && ((literal == 0) == zero);
        var write = @"(?i)^\s*" + Regex.Escape(property) + @"\s*\.\s*SetValue(?:Int)?\(\s*(?<value>.*?)\s*\)\s*$";
        var established = sourceNodes.Where(n => n.Kind == "statement" && n.Start < updatePosition)
            .Select(n => Regex.Match(n.Text, write)).Where(m => m.Success).LastOrDefault() is { } assignment
            && Satisfies(assignment.Groups["value"].Value);
        if (!established) return "the owning function does not set the guard to a satisfying literal before its continuation";
        // The continuation may run after any analysed writer in the source mod
        // (ADR 0008): its quest scripts with their chains, and its non-Quest holders.
        // Every use of this bound Global must be an immutable binding and an
        // understood read/write; another mod's writers are outside every proof.
        var source = SliceSourceMod(instance);
        // Code we cannot read matters only when it holds the guard or another slice value:
        // through its own bindings, or as `self` on a slice quest. A script holding none
        // of them is irrelevant (ADR 0009).
        bool Relevant(IEnumerable<FormKey> bound) => bound.Any(k => k == key || SliceRecord(k, source, catalog));
        var inScope = SliceScopeInstances(source, catalog);
        var views = new List<ScriptInstance>();
        foreach (var candidate in inScope)
        {
            var relevant = Relevant(candidate.Objects.Values.Concat(candidate.Arrays.Values.SelectMany(k => k)).Append(candidate.Quest.FormKey));
            if (candidate.Text is null) { if (relevant) return $"in-mod script {candidate.Label} is unreadable: {candidate.Evidence}"; continue; }
            var (view, chainProblem) = SliceChainView(candidate, catalog);
            if (view is not null) views.Add(view);
            else if (relevant) return chainProblem;
        }
        if (views.Any(i => i.VmadHandles.Any(h => h.Record == key && (h.Index >= 0 || h.Alias != -1)))) return "guard is held in an array or alias";
        if (catalog.RuntimeFormLists?.Values.Any(l => InSliceScope(l.FormKey, source, catalog) && l.Items.Any(i => i.FormKey == key)) == true) return "guard is carried by a FormList";
        string? UsesProblem(string text, string name)
        {
            foreach (var raw in text.Split('\n'))
            {
                var masked = MaskScriptStrings(raw);
                if (IsPropertyDeclaration(masked, name) || !Regex.IsMatch(masked, @"(?i)\b" + Regex.Escape(name) + @"\b")) continue;
                var set = Regex.Match(raw, @"(?i)^\s*" + Regex.Escape(name) + @"\s*\.\s*SetValue(?:Int)?\(\s*(?<value>.*?)\s*\)\s*$");
                if (set.Success) { if (raw.Contains("SetValueInt", StringComparison.OrdinalIgnoreCase) || !Satisfies(set.Groups["value"].Value)) return "guard write breaks the guard: " + raw.Trim(); }
                else
                {
                    var afterReads = Regex.Replace(masked, @"(?i)\b" + Regex.Escape(name) + @"\s*\.\s*GetValue\s*\(\s*\)", "0");
                    if (Regex.IsMatch(afterReads, @"(?i)\b" + Regex.Escape(name) + @"\b")) return "guard use is unproved: " + raw.Trim();
                }
            }
            return null;
        }
        // A lookup is a capability carrier even without a GlobalVariable cast at
        // the acquisition. Keep only literal lookups of another record, primitive
        // queries and casts to native record classes that cannot retain a Global.
        string? LookupProblem(string raw, Func<bool> shadowed)
        {
            var masked = MaskScriptStrings(raw);
            var tokens = Regex.Matches(masked, @"(?i)\bGame\s*\.\s*(GetFormFromFile|GetForm)\s*\(");
            var lookups = Regex.Matches(masked, @"(?i)\bGame\s*\.\s*(?<method>GetFormFromFile|GetForm)\s*\([^()\r\n]*\)");
            if (tokens.Count != lookups.Count) return "unparsed lookup: " + raw.Trim();
            foreach (Match lookup in lookups)
            {
                if (shadowed() || !NativeFrameContract(catalog, "Game", lookup.Groups["method"].Value, "Form",
                        lookup.Groups["method"].Value.Equals("GetForm", StringComparison.OrdinalIgnoreCase) ? ["Int"] : ["Int", "String"], true)) return "lookup dispatch is unproved: " + raw.Trim();
                var before = masked[..lookup.Index]; var after = masked[(lookup.Index + lookup.Length)..];
                if (Regex.IsMatch(before, @"(?i)^\s*(if|elseif|while)\s*!?\s*$") && string.IsNullOrWhiteSpace(after)) continue;
                if (Regex.IsMatch(after, @"(?i)^\s+as\s+(LeveledItem|Armor|Weapon|Quest|Actor|ObjectReference|Keyword|FormList)\b")) continue;
                // A literal local ID names one record of that plugin, or None.
                var literal = Regex.Match(raw.Substring(lookup.Index, lookup.Length),
                    "(?i)GetFormFromFile\\s*\\(\\s*(?<id>0x[0-9a-f]+|\\d+)\\s*,\\s*\"(?<plugin>[^\"\\\\]+)\"\\s*\\)$");
                if (!literal.Success || SliceLiteralLookup(literal.Groups["id"].Value, literal.Groups["plugin"].Value, catalog) == key) return "lookup might return the guard: " + raw.Trim();
            }
            return null;
        }
        foreach (var other in views)
        {
            foreach (var otherFunction in other.Functions)
                foreach (var raw in otherFunction.Body.Split('\n'))
                    if (LookupProblem(raw, () => RuntimeLookupGameShadowed(other, otherFunction)) is { } lookupProblem) return $"{other.Label}: {lookupProblem}";
            foreach (var binding in other.Objects.Where(p => p.Value == key))
            {
                if (!StableAutoProperty(other, binding.Key) || !AutoPropertyType(other.Text!, binding.Key)!.Equals("GlobalVariable", StringComparison.OrdinalIgnoreCase))
                    return $"{other.Label}/{binding.Key} is not a stable GlobalVariable property";
                if (catalog.RuntimeSliceBindingResolver?.Invoke(other, binding.Key, false).Problem is { } bindingProblem) return $"{other.Label}/{binding.Key}: {bindingProblem}";
                if (UsesProblem(string.Join('\n', other.Functions.Select(f => f.Body)), binding.Key) is { } useProblem) return $"{other.Label}: {useProblem}";
            }
        }
        var winners = RuntimeRecordWinners(catalog);
        foreach (var (script, records) in SliceScriptAttachments(catalog))
            foreach (var record in records.Where(r => InSliceScope(r, source, catalog) && winners[r].Record is not IQuestGetter))
            {
                var text = catalog.RuntimeHolderAssetResolver?.Invoke(script).Text;
                var entry = ((IHaveVirtualMachineAdapterGetter)winners[record].Record).VirtualMachineAdapter!.Scripts
                    .FirstOrDefault(e => e.Name.Equals(script, StringComparison.OrdinalIgnoreCase));
                if (text is null)
                {
                    if (Relevant((entry?.Properties ?? []).SelectMany(p => p is IScriptObjectPropertyGetter h ? [h.Object.FormKey]
                        : p is IScriptObjectListPropertyGetter l ? l.Objects.Select(o => o.Object.FormKey) : [])))
                        return $"in-mod holder script {script} on {record} is unreadable";
                    continue;
                }
                var gameShadowed = Regex.IsMatch(MaskScriptStrings(text), @"(?im)^\s*\w+(?:\[\])?\s+(?:property\s+)?Game\b");
                if (text.Split('\n').Select(raw => LookupProblem(raw, () => gameShadowed)).FirstOrDefault(p => p is not null) is { } holderLookup) return $"{script} on {record}: {holderLookup}";
                foreach (var bound in entry?.Properties ?? [])
                {
                    if (bound is IScriptObjectListPropertyGetter array && array.Objects.Any(o => o.Object.FormKey == key)) return $"{script} on {record} holds the guard in an array";
                    if (bound is not IScriptObjectPropertyGetter handle || handle.Object.FormKey != key) continue;
                    if (handle.Alias != -1) return $"{script} on {record} holds the guard through an alias";
                    if (UsesProblem(text, bound.Name) is { } holderUse) return $"{script} on {record}: {holderUse}";
                }
            }
        return null;
    }
}
