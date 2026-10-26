using System.Text.RegularExpressions;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;

namespace XEnchantmentPatcher;

public static partial class PatcherEngine
{
    private sealed record StageRetryProof(ScriptTrigger? Trigger, HashSet<(string Function, int Offset)> Calls, string Problem);

    private static ScriptFlowAnalysis.Result Flow(ScriptInstance instance, ScriptFunction function)
    {
        if (!instance.Flow.TryGetValue(function, out var flow)) instance.Flow[function] = flow = ScriptFlowAnalysis.Parse(function.Body);
        return flow;
    }

    private static StageRetryProof ProveStageRetry(ScriptInstance instance, Catalog catalog,
        Func<FormKey, (string? Problem, string Evidence)> verifyGuard)
    {
        StageRetryProof Reject(string why) => new(null, [], why);
        if (instance.Alias || !instance.Quest.Flags.HasFlag(Quest.Flag.StartGameEnabled) || !instance.Quest.Flags.HasFlag(Quest.Flag.RunOnce)
            || instance.Quest.VirtualMachineAdapter!.Scripts.Count != 1 || instance.Quest.VirtualMachineAdapter.Aliases.Count != 0)
            return Reject("conditional execution requires a single RunOnce start-game Quest script with a proved stage retry lifecycle");
        if (!Regex.IsMatch(instance.Text ?? "", @"(?im)^\s*scriptname\s+\w+\s+extends\s+quest\b")
            || Regex.IsMatch(instance.Text ?? "", @"(?im)^\s*(?:auto\s+)?state\b"))
            return Reject("stage retry requires a direct Quest base with no states");
        var init = instance.Functions.Where(f => f.Name.Equals("OnInit", StringComparison.OrdinalIgnoreCase) && f.Parameters.Trim().Length == 0).ToArray();
        var update = instance.Functions.Where(f => f.Name.Equals("OnUpdate", StringComparison.OrdinalIgnoreCase) && f.Parameters.Trim().Length == 0).ToArray();
        if (init.Length != 1 || update.Length != 1) return Reject("stage retry requires unique OnInit and OnUpdate callbacks");
        var start = Flow(instance, init[0]); var poll = Flow(instance, update[0]);
        if (start.Problem is not null || poll.Problem is not null) return Reject(start.Problem ?? poll.Problem!);
        bool NativeRetryRegistration(ScriptFlowAnalysis.Node node, ScriptFunction function)
        {
            var call = Regex.Match(node.Text, @"(?i)^(?:self\s*\.\s*)?RegisterForSingleUpdate\s*\(([^()]*)\)$");
            return call.Success && NativeUpdateFrameCall("RegisterForSingleUpdate", SplitArguments(call.Groups[1].Value),
                instance, function, instance.Quest.FormKey, [instance], catalog);
        }
        if (start.Nodes.Count != 1 || !ScriptFlowAnalysis.SchedulesRetry(start.Nodes[0]) || !NativeRetryRegistration(start.Nodes[0], init[0]))
            return Reject("OnInit does not prove a single initial native update registration");
        if (poll.Nodes.Count != 1 || poll.Nodes[0].Kind != "if") return Reject("OnUpdate is not one bounded retry conditional");
        var branch = poll.Nodes[0];
        var guard = Regex.Match(branch.Text, @"(?i)^\s*(?<property>\w+)\s*\.\s*GetStageDone\s*\(\s*(?<stage>\d+)\s*\)\s*$");
        if (!guard.Success || !ushort.TryParse(guard.Groups["stage"].Value, out var stage)
            || !instance.Objects.TryGetValue(guard.Groups["property"].Value, out var quest)
            || !catalog.Mods.SelectMany(m => m.Quests).GroupBy(q => q.FormKey).Select(g => g.Last()).Any(q => q.FormKey == quest && !q.IsDeleted))
            return Reject("retry predicate is not one positive bound quest.GetStageDone(literal) atom");
        var guardProperty = guard.Groups["property"].Value;
        if (AutoPropertyType(instance.Text!, guardProperty)?.Equals("Quest", StringComparison.OrdinalIgnoreCase) != true
            || !StableAutoProperty(instance, guardProperty)) return Reject("retry guard is not a stable plain Quest Auto binding");
        var guardDispatch = verifyGuard(quest);
        if (guardDispatch.Problem is not null) return Reject("retry guard native dispatch is unproved: " + guardDispatch.Problem);
        if (branch.Else.Count != 1 || !ScriptFlowAnalysis.SchedulesRetry(branch.Else[0]) || !NativeRetryRegistration(branch.Else[0], update[0]))
            return Reject("false stage branch does not guarantee another native update");
        var calls = new HashSet<(string, int)>(); var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { init[0].Name, update[0].Name };
        var active = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var completed = new HashSet<string>(StringComparer.OrdinalIgnoreCase); var expanded = 0;
        string? problem = null;
        void Success(IEnumerable<ScriptFlowAnalysis.Node> nodes, ScriptFunction function, int depth)
        {
            if (depth > 4 || visited.Count > 256) { problem = "stage retry call graph exceeds depth 4 or 256 functions"; return; }
            if (completed.Contains(function.Name)) { problem = "repeated helper invocation leaves runtime insertion multiplicity unproved"; return; }
            if (!active.Add(function.Name)) { problem = "cyclic stage retry call graph"; return; }
            foreach (var node in nodes)
            {
                if (++expanded > 8192) { problem = "stage retry proof exceeds 8192 expanded statements"; break; }
                if (node.Kind != "statement") { problem = "successful stage path has another conditional or loop"; break; }
                var mutation = DirectMutation.Match(node.Text);
                if (mutation.Success && mutation.Groups["m"].Value.Equals("AddForm", StringComparison.OrdinalIgnoreCase))
                {
                    if (!instance.Objects.TryGetValue(mutation.Groups["recv"].Value, out var target) || !catalog.Lists.ContainsKey(target))
                    { problem = "stage retry mutation receiver is not a bound leveled-list property"; break; }
                    var arguments = SplitArguments(mutation.Groups["args"].Value);
                    var payloadName = arguments.Length == 3 ? Regex.Replace(arguments[0], @"(?i)\s+as\s+form\s*$", "").Trim() : "";
                    if (arguments.Length != 3 || !instance.Objects.ContainsKey(payloadName)
                        || !short.TryParse(arguments[1], out var level) || level < 1
                        || !short.TryParse(arguments[2], out var count) || count < 1)
                    { problem = "stage retry AddForm arguments contain an unresolved payload, non-literal count/level or possible side effect"; break; }
                    calls.Add((function.Name.ToLowerInvariant(), node.Start)); continue;
                }
                var call = Regex.Match(node.Text, @"(?i)^(?:self\s*\.\s*)?(?<function>\w+)\s*\(\s*\)$");
                if (!call.Success) { problem = "successful stage path contains an unsupported statement or side effect"; break; }
                var name = call.Groups["function"].Value;
                var helpers = instance.Functions.Where(f => f.Name.Equals(name, StringComparison.OrdinalIgnoreCase) && f.Parameters.Trim().Length == 0
                    && !name.Equals("OnInit", StringComparison.OrdinalIgnoreCase) && !name.Equals("OnUpdate", StringComparison.OrdinalIgnoreCase)).ToArray();
                if (helpers.Length != 1) { problem = "successful stage path contains an unresolved or callback call"; break; }
                var helper = helpers[0]; var flow = Flow(instance, helper);
                if (flow.Problem is not null) { problem = flow.Problem; break; }
                visited.Add(helper.Name); Success(flow.Nodes, helper, depth + 1);
                if (problem is not null) break;
            }
            active.Remove(function.Name);
            if (problem is null) completed.Add(function.Name);
        }
        Success(branch.Then, update[0], 0);
        if (problem is not null || calls.Count == 0) return Reject(problem ?? "stage retry successful branch contains no supported insertion");
        if (instance.Functions.Any(f => !visited.Contains(f.Name))) return Reject("other script callbacks/functions leave one-shot execution or reconfiguration unresolved");
        if (catalog.RuntimeObservedQuestCalls.Any(c => c.Quest == instance.Quest.FormKey && c.CallerIdentity != instance.Identity
            && (c.View.Equals(instance.Name, StringComparison.OrdinalIgnoreCase) && visited.Contains(c.Method)
                || new[] { "registerforsingleupdate", "registerforsingleupdategametime", "unregisterforupdate", "unregisterforupdategametime",
                    "start", "stop", "completequest", "completeallobjectives", "setstage", "setcurrentstageid" }.Contains(c.Method, StringComparer.OrdinalIgnoreCase))))
            return Reject("incoming bound calls can repeat or interrupt the distributor callbacks/helpers or scheduling");
        return new(new(quest, stage, false,
            $"{instance.Label}: RunOnce OnInit registers retrying OnUpdate; positive {guard.Groups["property"].Value}.GetStageDone({stage}) [{quest}]; {guardDispatch.Evidence}; successful path stops scheduling; distributor owner {instance.Quest.FormKey}")
            { OwnerQuest = instance.Quest.FormKey }, calls, "");
    }

    private static string ConditionalRuntimeDescription(ScriptInstance instance, ScriptFunction? function, int position,
        IEnumerable<ScriptInstance> all, Catalog catalog, string lifecycleProblem,
        Func<ScriptInstance, ScriptFunction, Match, ScriptInstance, bool> callTargets)
    {
        if (function is null) return "call is outside a function";
        var flow = Flow(instance, function);
        if (flow.Problem is not null) return $"conditional call analysis unsupported: {flow.Problem}";
        var at = flow.At(position - function.BodyStart);
        string Bound(string expression)
        {
            foreach (var name in Regex.Matches(expression, @"\b\w+\b").Select(m => m.Value).Distinct(StringComparer.OrdinalIgnoreCase))
                if (instance.Objects.TryGetValue(name, out var key)) expression += $"; {name}=[{key}]";
            return expression;
        }
        var predicate = at.Predicates.Length == 0 ? "no local guard" : string.Join(" AND ", at.Predicates.Select(Bound));
        if (!instance.ConditionalCallers.TryGetValue(function, out var invocations))
        {
            var found = new List<string>(); var matcher = new ScriptCallMatcher();
            foreach (var caller in all.Where(i => i.Text is not null))
            foreach (var callerFunction in caller.Functions)
            foreach (Match call in matcher.Matches(function.Name, callerFunction.Body))
            {
                if (found.Count >= 8 || !callTargets(caller, callerFunction, call, instance)) continue;
                var callerFlow = Flow(caller, callerFunction);
                var callerAt = callerFlow.At(call.Index);
                var callerPredicate = callerFlow.Problem is not null ? "unsupported call flow: " + callerFlow.Problem
                    : callerAt.Predicates.Length == 0 ? "unconditional local call" : string.Join(" AND ", callerAt.Predicates);
                found.Add($"{caller.Label}.{callerFunction.Name} ({callerPredicate})");
            }
            instance.ConditionalCallers[function] = invocations = found.Distinct().ToArray();
        }
        var ownsReset = instance.HasOwnedReset ??= Regex.Matches(instance.Text ?? "",
                @"(?im)^\s*(?<property>\w+)\s*\.\s*(Revert|RemoveAddedForm)\s*\(")
            .Any(m => instance.Objects.TryGetValue(m.Groups["property"].Value, out var key) && catalog.Lists.ContainsKey(key));
        return $"Conditional runtime route recognized: {instance.Name}.{function.Name}; call predicates: {predicate}; "
            + (invocations.Length > 0 ? "observed bound callers: " + string.Join("; ", invocations) + "; " : "invocation event/entry occurrence unproved; ")
            + (ownsReset ? "source-owned Revert/repopulation requires final membership and repeat/reconfiguration synchronization; " : "")
            + (at.Loop ? "call is inside an unsupported loop; " : "")
            + "execution/lifecycle unsupported: " + lifecycleProblem;
    }
}
