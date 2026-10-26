using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;

namespace XEnchantmentPatcher;

public static partial class PatcherEngine
{
    private sealed record RuntimeQuestDispatch(FormKey Quest, string View, string Method, string CallerIdentity);
    // This frame covers winning attached scripts and observed VMAD-bound
    // callers. It is not a closed-world claim about arbitrary external code.
    // A slice scope (script distribution only, ADR 0008/0009) analyses custom
    // inheritance chains as one body (E2) and proves FormList carrier edges (E3); its
    // callers are the source mod's own. Other routes pass none.
    private static RuntimePropertyBinding RuntimePropertyFrame(ScriptInstance owner, string name, bool listReceiver,
        IReadOnlyList<ScriptInstance> callers, Catalog catalog, RuntimeSliceScope? slice = null)
    {
        using var timing = PhaseTiming.Measure("availability.property-frame");
        var text = owner.Text ?? "";
        // Slice proofs receive callers already filtered by the contract's slice bound.
        if (slice is null && catalog.RuntimeFrameClosureProblems.TryGetValue(owner.Quest.FormKey, out var closureProblem)) return new(closureProblem, true);
        if (text.Length > 1_000_000 || callers.Count > 512) return new("runtime binding frame exceeds its script/text bound", true);
        if (!StableAutoProperty(owner, name)) return new("plain Auto binding without rebinding/local/parameter shadow is required", listReceiver);
        if (listReceiver && !(AutoPropertyType(text, name) is { } listType && new[] { "Form", "LeveledItem" }.Contains(listType, StringComparer.OrdinalIgnoreCase)))
            return new("list property dispatch type is outside the verified native frame", true);
        var frame = callers.Append(owner).Distinct().ToArray();
        if (slice is not null)
        {
            var views = new List<ScriptInstance>();
            foreach (var member in frame)
            {
                var (view, chainProblem) = SliceChainView(member, catalog);
                if (view is null) return new(chainProblem + " in " + member.Label, true);
                views.Add(view);
            }
            frame = views.ToArray();
            owner = frame.Single(i => i.Identity == owner.Identity);
            text = owner.Text ?? "";
        }
        if (RuntimeHolderFrameProblem(frame, catalog, null, out var holderEvidence, slice) is { } holderProblem) return new(holderProblem, true);
        var questKeys = frame.Select(i => i.Quest.FormKey).ToHashSet();
        if (frame.Where(i => !i.Alias).GroupBy(i => (i.Quest.FormKey, Name: i.Name.ToLowerInvariant())).Any(g => g.Count() > 1))
            return new("duplicate attached script dispatch is unproved", true);
        var aliasBudget = 0;
        RuntimePropertyBinding? CallerFailure(ScriptInstance caller)
        {
            if (caller.BindingMetadataProblem is not null) return new(caller.BindingMetadataProblem, true);
            if (caller.Text is null || caller.Text.Length > 1_000_000 || caller.Functions.Count > 512)
                return new("bound caller property writes are unverifiable or exceed the frame bound", true);
            var syntax = TextSyntax(caller.Text);
            var declarations = Regex.Matches(syntax.Masked, @"(?im)^\s*scriptname\s+(\w+)\b");
            if (declarations.Count != 1 || !declarations[0].Groups[1].Value.Equals(caller.Name, StringComparison.OrdinalIgnoreCase))
                return new("winning script name does not match its attached VMAD class", true);
            var callerParent = ScriptParent(caller.Text);
            // Slice views have already resolved a custom chain to its canonical parent.
            if (slice is null && !callerParent.Equals(caller.Alias ? "ReferenceAlias" : "Quest", StringComparison.OrdinalIgnoreCase))
                return new("bound caller inheritance is outside the canonical frame", true);
            if (!SafeInheritedQuestFrame(catalog)) return new("inherited quest/alias callbacks or handle uses are unproved: "
                + catalog.RuntimeNativeContractProblems.GetValueOrDefault("inherited-quest-frame", "native frame evidence is unavailable"), true);
            if (!caller.Alias && catalog.RuntimeNativeContracts.GetValueOrDefault("form-keyword-helper")
                && new[] { "HasKeyword", "HasKeywordString" }.Any(method => HasScriptMethod(caller.Text, method)))
                return new($"attached {caller.Name} overrides inherited Form keyword query dispatch", true);
            if (caller.Alias && (caller.AliasIdentity?.Quest != caller.Quest.FormKey || !ValidRuntimeAlias(caller.AliasIdentity, frame)))
                return new("attached alias identity does not resolve to a winning reference alias", true);
            // Slice proofs check every use of a slice value in the slice flow instead.
            foreach (var aliasProperty in caller.AliasBindings.Keys.Where(_ => slice is null))
                for (var k = 0; k < syntax.Lines.Count; k++)
                {
                    var raw = syntax.Lines[k]; var line = syntax.MaskedLines[k];
                    if (Regex.IsMatch(line, @"(?i)\b" + Regex.Escape(aliasProperty) + @"\b") && !IsPropertyDeclaration(line, aliasProperty))
                        if (!NativeAliasFrameUse(line, caller, frame, catalog))
                            return FrameFailure("VMAD alias-property capability use is outside the bounded frame", caller, aliasProperty, raw);
                }
            foreach (var array in caller.ArrayBindings.Where(p => p.Value.Any(v => v.Alias != -1)))
            {
                if (array.Value.Any(v => v.Alias < 0) || !array.Value.All(v => ValidRuntimeAlias((v.Record, v.Alias), frame))
                    || !StableRuntimeAliasArray(caller, array.Key))
                    return new("alias array has mixed, invalid or unstable typed bindings", true);
                for (var k = 0; slice is null && k < syntax.Lines.Count; k++)
                {
                    var raw = syntax.Lines[k]; var line = syntax.MaskedLines[k];
                    if (Regex.IsMatch(line, @"(?i)\b" + Regex.Escape(array.Key) + @"\b") && !IsPropertyDeclaration(line, array.Key)
                        && !NativeAliasFrameUse(line, caller, frame, catalog))
                        return FrameFailure("alias array reference escapes or has an unproved use", caller, array.Key, raw);
                }
            }
            if (slice is null && caller.Arrays.Values.Any(keys => keys.Any(questKeys.Contains)))
                return new("quest-reference array use is outside the bounded property frame", true);
            catalog.RuntimeFormLists ??= catalog.Mods.SelectMany(m => m.FormLists).GroupBy(l => l.FormKey).ToDictionary(g => g.Key, g => g.Last());
            bool ListCapability(FormKey key)
            {
                var pending = new Queue<(FormKey Key, int Depth)>(); pending.Enqueue((key, 0)); var seen = new HashSet<FormKey>();
                while (pending.TryDequeue(out var current))
                {
                    if (catalog.Lists.ContainsKey(current.Key) || current.Depth > 16 || seen.Count >= 8192) return true;
                    if (!seen.Add(current.Key) || !catalog.RuntimeFormLists.TryGetValue(current.Key, out var collection)) continue;
                    foreach (var item in collection.Items) pending.Enqueue((item.FormKey, current.Depth + 1));
                }
                return false;
            }
            // Arrays and FLST handles are additional routes to a leveled-list
            // capability even when the direct list-property token is absent.
            foreach (var property in caller.Arrays.Where(p => slice is null && p.Value.Any(ListCapability)).Select(p => p.Key)
                .Concat(caller.Objects.Where(p => slice is null && catalog.RuntimeFormLists.ContainsKey(p.Value) && ListCapability(p.Value)).Select(p => p.Key)))
                for (var k = 0; k < syntax.Lines.Count; k++)
                {
                    var raw = syntax.Lines[k]; var line = syntax.MaskedLines[k];
                    if (Regex.IsMatch(line, @"(?i)\b" + Regex.Escape(property) + @"\b") && !IsPropertyDeclaration(line, property))
                        return FrameFailure("collection/array leveled-list capability use is outside the bounded frame", caller, property, raw);
                }
            var references = new Dictionary<string, RuntimeQuestView>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in caller.Objects.Where(p => questKeys.Contains(p.Value)))
            {
                var type = AutoPropertyType(caller.Text, p.Key);
                if (type is null || !StableAutoProperty(caller, p.Key)) return new("bound quest pointer is not a stable plain Auto property", true);
                references[p.Key] = new(type, p.Value);
            }
            if (!caller.Alias)
            {
                if (slice is null && !ScriptParent(caller.Text).Equals("Quest", StringComparison.OrdinalIgnoreCase))
                    return new("attached quest-script inheritance is outside the bounded frame", true);
                references["self"] = new(caller.Name, caller.Quest.FormKey);
            }
            var owningAlias = caller.Alias && Regex.IsMatch(syntax.Masked, @"(?i)\bGetOwningQuest\s*\(");
            if (slice is null && !owningAlias && Regex.IsMatch(syntax.Masked, @"(?i)\bGetOwningQuest\s*\("))
                return new("owning-quest reference outside the exact self-alias adapter is unproved", true);
            if (owningAlias && (!ScriptParent(caller.Text).Equals("ReferenceAlias", StringComparison.OrdinalIgnoreCase)
                || !NativeFrameContract(catalog, "Alias", "GetOwningQuest", "Quest", [], false)
                || !BaseHasParentWithoutMethod(catalog, "ReferenceAlias", "Alias", "GetOwningQuest")
                || HasScriptMethod(caller.Text, "GetOwningQuest")))
                return new("owning-quest alias API/dispatch is unverified", true);
            if (slice is not null) return null;

            // Outside function bodies, only ordinary declarations may mention
            // a quest pointer. Global aliases/initializers are not inferred.
            var outside = syntax.Masked.ToCharArray();
            foreach (var f in caller.Functions) Array.Fill(outside, ' ', f.Start, f.End - f.Start);
            foreach (var line in new string(outside).Split('\n'))
                foreach (var reference in references.Keys)
                    if (Regex.IsMatch(line, @"(?i)\b" + Regex.Escape(reference) + @"\b")
                        && !IsPropertyDeclaration(line, reference)) return new("quest-reference use outside a bounded function", true);

            foreach (var f in caller.Functions)
            {
                var local = new Dictionary<string, RuntimeQuestView>(references, StringComparer.OrdinalIgnoreCase);
                if (local.Keys.Any(r => HasLocalShadow(f, r))) return new("quest pointer is shadowed by a local/parameter", true);
                var aliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var body = TextSyntax(f.Body);
                for (var k = 0; k < body.Lines.Count; k++)
                {
                    var rawLine = body.Lines[k]; var maskedLine = body.MaskedLines[k];
                    var raw = rawLine;
                    var implicitProblem = ImplicitRuntimeFrameCalls(raw, maskedLine, caller, f, owner, frame, catalog);
                    if (implicitProblem is not null) return FrameFailure(implicitProblem + $" in {caller.Label}.{f.Name}", caller, "self", raw, f.Name);
                    if (owningAlias)
                    {
                        // An alias can acquire only its own quest here. Every
                        // use of that resulting handle is checked below.
                        if (Regex.IsMatch(maskedLine, @"(?i)\bGetOwningQuest\s*\(")
                            && !Regex.IsMatch(maskedLine, @"(?i)\bself\s*\.\s*GetOwningQuest\s*\(\s*\)"))
                            return new("unparsed owning-quest reference", true);
                        if (raw.Contains("__xep_owning_quest", StringComparison.OrdinalIgnoreCase)) return new("reserved analysis-token collision", true);
                        raw = Regex.Replace(raw, @"(?i)\bself\s*\.\s*GetOwningQuest\s*\(\s*\)", "__xep_owning_quest");
                        if (Regex.IsMatch(MaskScriptStrings(raw), @"(?i)\bGetOwningQuest\s*\(")) return new("unparsed owning-quest reference remains after exact self calls", true);
                        local["__xep_owning_quest"] = new("Quest", caller.Quest.FormKey);
                    }
                    // Regex.Replace returns its input instance when nothing was replaced.
                    var line = ReferenceEquals(raw, rawLine) ? maskedLine : MaskScriptStrings(raw);
                    if (caller.Alias && Regex.IsMatch(line, @"(?i)\bself\b") && !NativeAliasFrameUse(line, caller, frame, catalog))
                        return new("alias self handle has a use outside its exact native owning-quest adapter", true);
                    var declaration = Regex.Match(line, @"(?i)^\s*(?<type>\w+)\s+(?<local>\w+)\s*=\s*(?<origin>\w+)\s+as\s+(?<cast>\w+)\s*$");
                    if (declaration.Success && local.ContainsKey(declaration.Groups["origin"].Value))
                    {
                        var type = declaration.Groups["type"].Value; var alias = declaration.Groups["local"].Value;
                        if (!type.Equals(declaration.Groups["cast"].Value, StringComparison.OrdinalIgnoreCase)
                            || !(type.Equals("Quest", StringComparison.OrdinalIgnoreCase)
                                || frame.Any(i => !i.Alias && i.Quest.FormKey == local[declaration.Groups["origin"].Value].Quest && i.Name.Equals(type, StringComparison.OrdinalIgnoreCase)))
                            || local.ContainsKey(alias) || ++aliasBudget > 256 || HasParameter(f, alias)
                            || Regex.Matches(body.Masked, @"(?im)^\s*\w+\s+" + Regex.Escape(alias) + @"\s*(?:=|$)").Count != 1)
                            return new("local quest alias has an unverified type, shadow or bound", true);
                        local[alias] = new(type, local[declaration.Groups["origin"].Value].Quest); aliases.Add(alias); continue;
                    }
                    foreach (var reference in local)
                        foreach (Match use in Regex.Matches(line, @"(?i)\b" + Regex.Escape(reference.Key) + @"\b"))
                        {
                            if (aliases.Contains(reference.Key) && Regex.IsMatch(line[(use.Index + use.Length)..], @"^\s*(?:[+\-*/]?=|\[)"))
                                return new("local quest alias is rebound/indexed", true);
                            var problem = QuestReferenceUse(raw, line, use, reference.Key, reference.Value.Type, caller, f, owner, frame, catalog, reference.Value.Quest);
                            if (problem is not null) return FrameFailure(problem + $" in {caller.Label}.{f.Name}", caller, reference.Key, rawLine, f.Name);
                        }
                }
            }
            return null;
        }
        foreach (var caller in frame)
            if (CallerFailure(caller) is { } failure) return failure;
        if (listReceiver && slice is null)
            foreach (var line in TextSyntax(text).MaskedLines)
            {
                if (IsPropertyDeclaration(line, name)) continue;
                foreach (Match use in Regex.Matches(line, @"(?i)\b" + Regex.Escape(name) + @"\b"))
                {
                    var method = Regex.Match(line[(use.Index + use.Length)..], @"(?i)^\s*(?:as\s+(?<cast>\w+)\s*\))?\s*\.\s*(?<method>\w+)\s*\(");
                    var view = method.Groups["cast"].Success ? method.Groups["cast"].Value : AutoPropertyType(text, name);
                    if (method.Success && view?.Equals("LeveledItem", StringComparison.OrdinalIgnoreCase) == true
                        && FootprintListMethods.Contains(method.Groups["method"].Value) && NativeListFrameContract(catalog, method.Groups["method"].Value)) continue;
                    var call = DirectMutation.Match(line); var args = call.Success ? SplitArguments(call.Groups["args"].Value) : [];
                    if (call.Success && call.Groups["m"].Value.Equals("AddForm", StringComparison.OrdinalIgnoreCase)
                        && args.Length == 3 && Regex.Replace(args[0], @"(?i)\s+as\s+form\s*$", "").Trim().Equals(name, StringComparison.OrdinalIgnoreCase)
                        && ((owner.Objects.TryGetValue(call.Groups["recv"].Value, out var target) && !target.IsNull)
                            || UnfilledRuntimeListReceiver(owner, call.Groups["recv"].Value)) && NativeListFrameContract(catalog, "AddForm")) continue;
                    var f = owner.Functions.FirstOrDefault(f => Regex.IsMatch(f.Body, @"(?i)\b" + Regex.Escape(name) + @"\b"));
                    if (NativeItemConsumption(line, name, owner, f, catalog)) continue;
                    return new("unrecognized list call or unproved list-reference escape", true);
                }
            }
        var evidenceKey = (owner.Quest.FormKey, owner.Identity + "/" + name.ToLowerInvariant() + (slice is null ? "" : "/slice"), listReceiver);
        if (!catalog.RuntimeFrameEvidence.TryGetValue(evidenceKey, out var evidence))
        {
            using var evidenceTiming = PhaseTiming.Measure("availability.property-frame.evidence");
            // The SHA-256 of the same UTF-8 signature as one joined string; script texts
            // contribute their cached bytes instead of being copied into it on every call.
            using var signature = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            void Append(string part) => signature.AppendData(Encoding.UTF8.GetBytes(part));
            void AppendText(string? text) { if (text is not null) signature.AppendData(TextSyntax(text).Utf8); }
            var first = true;
            foreach (var i in frame.OrderBy(i => i.Identity, StringComparer.Ordinal))
            {
                Append((first ? "" : "|") + $"{i.Identity}/{i.Evidence}/"); first = false;
                AppendText(i.Text);
                Append("/" + string.Join(';', i.Objects.OrderBy(p => p.Key).Select(p => $"{p.Key}={p.Value}")) + "/"
                    + string.Join(';', i.AliasBindings.OrderBy(p => p.Key).Select(p => $"{p.Key}={p.Value.Quest}/{p.Value.Alias}")) + "/"
                    + string.Join(';', i.ArrayBindings.OrderBy(p => p.Key).Select(p => $"{p.Key}={string.Join(',', p.Value)}")) + "/"
                    + string.Join(';', i.Quest.Aliases.OrderBy(a => a.ID).Select(a => $"{a.ID}/{a.Type}")));
            }
            Append("|");
            first = true;
            foreach (var p in catalog.RuntimeBaseScripts.OrderBy(p => p.Key))
            {
                Append((first ? "" : "|") + $"{p.Key}/{p.Value.Evidence}/"); first = false;
                AppendText(p.Value.Text);
            }
            if (holderEvidence.Length > 0)
            {
                Append("|" + RuntimeHolderEvidenceDomain(holderEvidence) + "|");
                catalog.HolderEvidence.AppendUtf8(signature, holderEvidence);
            }
            if (slice is not null) Append("|distribution-slice-v1|" + slice.Source);
            catalog.RuntimeFrameEvidence[evidenceKey] = evidence = Convert.ToHexString(signature.GetHashAndReset());
        }
        return new(null, false) { Evidence = $"{(slice is null ? "winning-frame-v3" : "slice-frame-v1")} {evidence}/{name}/{listReceiver}",
            CoveredInstanceIdentities = frame.Select(i => i.Identity).Order(StringComparer.Ordinal).ToArray() };

    }

    private static string ScriptParent(string text) => Regex.Match(text, @"(?im)^\s*scriptname\s+\w+\s+extends\s+(\w+)\b").Groups[1].Value;
    private static string? AutoPropertyType(string text, string name) => TextSyntax(text).AutoPropertyType(name);
    private static bool IsPropertyDeclaration(string line, string name) => Regex.IsMatch(line,
        @"(?i)^\s*\w+(?:\[\])?\s+property\s+" + Regex.Escape(name) + @"(?:\s+auto(?:readonly)?(?:\s+hidden)?)?\s*$");
    private static bool HasParameter(ScriptFunction f, string name) => TextSyntax(f.Parameters).HasParameter(name);
    private static bool HasLocalShadow(ScriptFunction f, string name) => HasParameter(f, name) || TextSyntax(f.Body).DeclaresLocal(name);
    private static bool StableAutoProperty(ScriptInstance i, string name) => i.Text is { } text && AutoPropertyType(text, name) is not null
        && !TextSyntax(text).Rebinds(name)
        && !i.Functions.Any(f => HasLocalShadow(f, name));
    // The winning VMAD never fills this immutable binding, so its direct native
    // calls have no receiver. The ordinary frame still refuses external rebinding.
    private static bool UnfilledRuntimeListReceiver(ScriptInstance i, string name) => !i.Objects.ContainsKey(name)
        && !i.AliasBindings.ContainsKey(name) && !i.Arrays.ContainsKey(name)
        && AutoPropertyType(i.Text ?? "", name)?.Equals("LeveledItem", StringComparison.OrdinalIgnoreCase) == true
        && StableAutoProperty(i, name);
    private static bool HasScriptMethod(string text, string name) => TextSyntax(text).HasMethod(name);

    private static string? QuestReferenceUse(string raw, string line, Match use, string reference, string view,
        ScriptInstance caller, ScriptFunction function, ScriptInstance owner, ScriptInstance[] frame, Catalog catalog, FormKey? referencedQuest = null)
    {
        var quest = referencedQuest ?? owner.Quest.FormKey;
        var suffix = line[(use.Index + use.Length)..];
        var cast = Regex.Match(suffix, @"(?i)^\s+as\s+(\w+)\s*\)\s*(?=\.)");
        if (cast.Success) { view = cast.Groups[1].Value; suffix = suffix[cast.Length..]; }
        if (!view.Equals("Quest", StringComparison.OrdinalIgnoreCase)
            && !frame.Any(i => !i.Alias && i.Quest.FormKey == quest && i.Name.Equals(view, StringComparison.OrdinalIgnoreCase)))
            return "quest-reference view type is outside the winning attached frame";
        var call = Regex.Match(suffix, @"(?i)^\s*\.\s*(\w+)\s*\(([^()]*)\)");
        if (call.Success)
        {
            var method = call.Groups[1].Value; var args = SplitArguments(call.Groups[2].Value);
            catalog.RuntimeObservedQuestCalls.Add(new(quest, view.ToLowerInvariant(), method.ToLowerInvariant(), caller.Identity));
            if (QuestNativeContract(catalog, method, out var dependencies, out var argumentCount) && args.Length == argumentCount
                && !frame.Where(i => i.Quest.FormKey == quest && !i.Alias).Any(i => dependencies.Any(d => HasScriptMethod(i.Text!, d)))
                && args.All(a => PrimitiveFrameExpression(a, caller, function))) return null;
            if (NativeUpdateFrameCall(method, args, caller, function, quest, frame, catalog)) return null;
            var rawCall = Regex.Match(raw[(use.Index + use.Length)..], @"(?i)^\s*\.\s*SendModEvent\s*\((.*)\)");
            if (rawCall.Success && NativeCompletionEventFrameCall(SplitArguments(rawCall.Groups[1].Value), caller, function, quest, frame, catalog)) return null;
            if (Regex.IsMatch(method, @"(?i)^(GetStage|GetStageDone|GetCurrentStageID|IsStageDone|SetStage|SetCurrentStageID|IsRunning|IsStarting|IsStopping|IsStopped|IsActive|IsCompleted|Start|Stop|SetActive|SetObjectiveDisplayed|SetObjectiveCompleted|SetObjectiveFailed)$"))
                return $"native quest API contract/override is unproved for {method}";
            var target = frame.SingleOrDefault(i => !i.Alias && i.Quest.FormKey == quest && i.Name.Equals(view, StringComparison.OrdinalIgnoreCase));
            if (target is not null && PrimitiveRuntimeHelperCall(target, method, args, caller, function)) return null;
            return $"unverified quest API/helper dispatch {method}";
        }
        var member = Regex.Match(suffix, @"(?i)^\s*\.\s*(\w+)\b(?!\s*\()");
        if (member.Success)
        {
            var property = member.Groups[1].Value;
            var primitiveOwner = frame.SingleOrDefault(i => !i.Alias && i.Quest.FormKey == quest && i.Name.Equals(view, StringComparison.OrdinalIgnoreCase));
            if (primitiveOwner is not null && PlainPrimitiveAutoMember(primitiveOwner.Text!, property, out var readOnly))
            {
                var tail = suffix[member.Length..];
                var assignment = Regex.Match(tail, @"^\s*[+\-*/]?=(?!=)\s*(.*)$");
                if (assignment.Success)
                    return !readOnly && PrimitiveFrameExpression(assignment.Groups[1].Value, caller, function) ? null : "primitive Auto member write has an unproved value or readonly setter";
                return Regex.IsMatch(tail, @"^\s*(?:\.|\[)") ? "primitive Auto member tail is unproved" : null;
            }
            if (Regex.IsMatch(suffix[member.Length..], @"^\s*(?:[+\-*/]?=|\[)")) return "bound quest property write/index is unproved";
            var target = frame.SingleOrDefault(i => !i.Alias && i.Quest.FormKey == quest && i.Name.Equals(view, StringComparison.OrdinalIgnoreCase));
            if (target is null || !target.Objects.TryGetValue(property, out var key) || !StableAutoProperty(target, property)
                || key.IsNull || key == owner.Quest.FormKey) return "quest member read is not a stable winning non-self binding";
            if (!catalog.Lists.ContainsKey(key))
            {
                // Collections and other quests can expose more handles. A
                // member read is not evidence that its tail consumes safely.
                if (catalog.Mods.Any(m => m.FormLists.Any(l => l.FormKey == key) || m.Quests.Any(q => q.FormKey == key)))
                    return "bound collection/quest member capability is outside the bounded frame";
                // Only an item consumed by the same checked native operation
                // is admitted; opaque member tails remain a refusal.
                return NativeItemConsumption(line, reference + "." + property, caller, function, catalog) ? null
                    : "bound member reference is not a verified native item consumption";
            }
            if (NativeItemConsumption(line, reference + "." + property, caller, function, catalog)) return null;
            return "bound quest list member is not a verified native item consumption";
        }
        // Conversion to a primitive never hands the Quest handle to the callee.
        if (Regex.IsMatch(suffix, @"(?i)^\s+as\s+bool\b(?!\s*\)?\s*\.)")) return null;
        if (Regex.IsMatch(suffix, @"(?i)^\s*(?:==|!=)\s*none\b")) return null;
        var stringUse = Regex.Match(raw, @"(?i)^\s*debug\s*\.\s*(Trace|TraceStack)\s*\(\s*" + Regex.Escape(reference)
            + @"\s+as\s+string(?:\s*\+\s*""(?:\\.|[^""\\])*"")*\s*,\s*[0-9]+\s*\)\s*$");
        if (stringUse.Success && !SymbolShadowed(caller, function, "Debug")
            && NativeFrameContract(catalog, "Debug", stringUse.Groups[1].Value, "", ["String", "Int"], true)) return null;
        return "bound quest reference cast/alias/escape is unproved";
    }

    private static bool PrimitiveFrameExpression(string expression, ScriptInstance i, ScriptFunction f)
    {
        if (Regex.IsMatch(expression, @"(?i)^\s*(?:true|false|[+-]?\d+(?:\.\d+)?(?:\s+as\s+(?:Int|Float))?)\s*$")) return true;
        return Regex.IsMatch(expression, @"^\w+$") && DeclaredType(i.Text!, f, expression) is { } type
            && new[] { "Int", "Float", "Bool", "String" }.Contains(type, StringComparer.OrdinalIgnoreCase);
    }

    private static bool PrimitiveRuntimeHelperCall(ScriptInstance target, string method, string[] args,
        ScriptInstance caller, ScriptFunction function)
    {
        var helpers = target.Functions.Where(f => f.Name.Equals(method, StringComparison.OrdinalIgnoreCase)).ToArray();
        return helpers.Length == 1 && Regex.Matches(TextSyntax(target.Text!).Masked, @"(?im)^\s*(?:\w+(?:\[\])?\s+)?(?:function|event)\s+" + Regex.Escape(method) + @"\s*\(").Count == 1
            && new[] { "", "Int", "Float", "Bool", "String" }.Contains(helpers[0].ReturnType, StringComparer.OrdinalIgnoreCase)
            && SplitArguments(helpers[0].Parameters).All(p => Regex.IsMatch(p, @"(?i)^\s*(int|float|bool|string)\s+\w+(?:\s*=.*)?$"))
            && args.Length == SplitArguments(helpers[0].Parameters).Length && args.All(a => PrimitiveFrameExpression(a, caller, function));
    }

    private static bool NativeUpdateFrameCall(string method, string[] args, ScriptInstance caller, ScriptFunction function,
        FormKey quest, ScriptInstance[] frame, Catalog catalog, bool alias = false)
    {
        var types = method.ToLowerInvariant() switch
        {
            "registerforsingleupdate" or "registerforsingleupdategametime" => new[] { "Float" },
            "unregisterforupdate" or "unregisterforupdategametime" => Array.Empty<string>(),
            _ => null
        };
        var script = alias ? "Alias" : "Form";
        return types is not null && args.Length == types.Length && args.All(a => PrimitiveFrameExpression(a, caller, function))
            && NativeFrameContract(catalog, script, method, "", types, false)
            && BaseHasParentWithoutMethod(catalog, alias ? "ReferenceAlias" : "Quest", script, method)
            && !frame.Where(i => i.Quest.FormKey == quest && i.Alias == alias
                && (!alias || i.AliasIdentity == caller.AliasIdentity)).Any(i => HasScriptMethod(i.Text!, method));
    }

    private static bool NativeCompletionEventFrameCall(string[] args, ScriptInstance caller, ScriptFunction function,
        FormKey quest, ScriptInstance[] frame, Catalog catalog)
    {
        const string method = "SendModEvent";
        return args.Length is >= 1 and <= 3 && args.Take(Math.Min(2, args.Length)).All(a => Regex.IsMatch(a, "^\\s*\"(?:\\\\.|[^\"\\\\])*\"\\s*$"))
            && (args.Length < 3 || PrimitiveFrameExpression(args[2], caller, function))
            && NativeSendModEventContract(catalog, alias: false)
            // An external Quest-typed reference can bind to any attached script,
            // so no script on that quest may replace the native sender.
            && !frame.Where(i => i.Quest.FormKey == quest && !i.Alias).Any(i => HasScriptMethod(i.Text!, method));
    }

    // The native SendModEvent a Quest or ReferenceAlias script inherits: the exact
    // Form/Alias native declaration, reached through an unshadowed canonical base.
    // Each caller separately proves its own dispatch scope leaves it unreplaced.
    private static bool NativeSendModEventContract(Catalog c, bool alias)
    {
        var native = alias ? "Alias" : "Form";
        return NativeFrameContract(c, native, "SendModEvent", "", ["String", "String", "Float"], false)
            && BaseHasParentWithoutMethod(c, alias ? "ReferenceAlias" : "Quest", native, "SendModEvent");
    }

    private static string? ImplicitRuntimeFrameCalls(string raw, string line, ScriptInstance caller, ScriptFunction function,
        ScriptInstance owner, ScriptInstance[] frame, Catalog catalog)
    {
        foreach (Match call in Regex.Matches(line, @"(?i)(?<!\w)(?<name>\w+)\s*\("))
        {
            if (line[..call.Index].TrimEnd().EndsWith('.')) continue;
            var method = call.Groups["name"].Value;
            if (new[] { "if", "elseif", "while", "return" }.Contains(method, StringComparer.OrdinalIgnoreCase)) continue;
            var arguments = Regex.Match(line[call.Index..], @"^\w+\s*\(([^()]*)\)");
            if (!arguments.Success) return "implicit instance call has unproved nested arguments";
            var args = SplitArguments(arguments.Groups[1].Value);
            if (caller.Alias)
            {
                var explicitLine = line.Insert(call.Index, "self.");
                if (NativeAliasFrameUse(explicitLine, caller, frame, catalog)
                    || NativeUpdateFrameCall(method, args, caller, function, caller.Quest.FormKey, frame, catalog, true)
                    || PrimitiveRuntimeHelperCall(caller, method, args, caller, function)) continue;
                return $"unverified implicit alias API/helper dispatch {method}";
            }
            var explicitRaw = raw.Insert(call.Index, "self.");
            var explicitMasked = MaskScriptStrings(explicitRaw);
            // Match offsets refer to the whole expression, as required by the use checker.
            var use = Regex.Matches(explicitMasked, @"(?i)\bself\b").First(m => m.Index == call.Index);
            var problem = QuestReferenceUse(explicitRaw, explicitMasked, use, "self", caller.Name,
                caller, function, owner, frame, catalog, caller.Quest.FormKey);
            if (problem is not null) return problem;
        }
        return null;
    }
    private static bool SymbolShadowed(ScriptInstance i, ScriptFunction? f, string name) => i.Objects.ContainsKey(name) || i.Arrays.ContainsKey(name)
        || Regex.IsMatch(TextSyntax(i.Text!).Masked, @"(?im)^\s*\w+(?:\[\])?\s+(?:property\s+)?" + Regex.Escape(name) + @"\b")
        || i.Functions.Any(f => HasParameter(f, name));

    private static bool NativeItemConsumption(string line, string expression, ScriptInstance i, ScriptFunction? f, Catalog catalog)
    {
        var match = Regex.Match(line, @"(?i)^\s*(?<receiver>game\s*\.\s*GetPlayer\s*\(\s*\)|\w+)\s*\.\s*AddItem\s*\(\s*"
            + Regex.Escape(expression).Replace(@"\.", @"\s*\.\s*") + @"(?:\s+as\s+form)?\s*,\s*[1-9][0-9]*\s*,\s*(true|false)\s*\)\s*$");
        if (!match.Success || !NativeFrameContract(catalog, "ObjectReference", "AddItem", "", ["Form", "Int", "Bool"], false)
            || !BaseHasParentWithoutMethod(catalog, "Actor", "ObjectReference", "AddItem")) return false;
        var receiver = match.Groups["receiver"].Value;
        if (receiver.Contains('.')) return !SymbolShadowed(i, f, "Game") && NativeFrameContract(catalog, "Game", "GetPlayer", "Actor", [], true);
        var type = AutoPropertyType(i.Text!, receiver);
        if (!(type?.Equals("Actor", StringComparison.OrdinalIgnoreCase) == true || type?.Equals("ObjectReference", StringComparison.OrdinalIgnoreCase) == true)
            || !StableAutoProperty(i, receiver) || f is not null && HasLocalShadow(f, receiver) || !i.Objects.TryGetValue(receiver, out var key)) return false;
        catalog.RuntimeActorReferences ??= catalog.Mods.SelectMany(m => m.EnumerateMajorRecords<IPlacedNpcGetter>()).GroupBy(r => r.FormKey)
            .Where(g => !g.Last().IsDeleted && !g.Last().Base.IsNull).ToDictionary(g => g.Key, g => true);
        // Currently only placed actors; ObjectReference properties bound to
        // other placed record kinds remain unsupported.
        return catalog.RuntimeActorReferences.ContainsKey(key);
    }

    private static bool QuestNativeContract(Catalog catalog, string method, out string[] dependencies, out int argumentCount)
    {
        dependencies = [method];
        argumentCount = 0;
        var wrapper = method.ToLowerInvariant() switch { "getstage" => "GetCurrentStageID", "getstagedone" => "IsStageDone", "setstage" => "SetCurrentStageID", _ => null };
        var canonical = wrapper ?? method;
        var signature = canonical.ToLowerInvariant() switch
        {
            "getcurrentstageid" => ("Int", Array.Empty<string>()),
            "isstagedone" or "setcurrentstageid" => ("Bool", new[] { "Int" }),
            "isrunning" or "isstarting" or "isstopping" or "isstopped" or "isactive" or "iscompleted" or "start" => ("Bool", Array.Empty<string>()),
            "stop" or "reset" or "completeallobjectives" or "failallobjectives" or "completequest" => ("", Array.Empty<string>()),
            "isobjectivedisplayed" or "isobjectivecompleted" or "isobjectivefailed" => ("Bool", new[] { "Int" }),
            "setactive" => ("", new[] { "Bool" }),
            "setobjectivecompleted" or "setobjectivefailed" => ("", new[] { "Int", "Bool" }),
            "setobjectivedisplayed" => ("", new[] { "Int", "Bool", "Bool" }),
            _ => ("?", Array.Empty<string>())
        };
        if (signature.Item1 == "?" || !NativeFrameContract(catalog, "Quest", canonical, signature.Item1, signature.Item2, false)) return false;
        argumentCount = signature.Item2.Length;
        if (wrapper is null) return true;
        dependencies = [method, canonical];
        var text = catalog.RuntimeBaseScripts.GetValueOrDefault("Quest").Text;
        if (text is null) return false;
        var functions = ParseFunctions(text).Where(f => f.Name.Equals(method, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (functions.Length != 1 || functions[0].Parameters.Split(',', StringSplitOptions.RemoveEmptyEntries).Length != signature.Item2.Length
            || !Regex.IsMatch(text, @"(?im)^\s*" + Regex.Escape(signature.Item1) + @"\s+function\s+" + Regex.Escape(method) + @"\s*\(")
            || !SplitArguments(functions[0].Parameters).Select(p => Regex.Match(p, @"^\s*(\w+)\s+\w+\s*$").Groups[1].Value).SequenceEqual(signature.Item2, StringComparer.OrdinalIgnoreCase)) return false;
        var names = SplitArguments(functions[0].Parameters).Select(p => Regex.Match(p, @"^\s*\w+\s+(\w+)\s*$").Groups[1].Value).ToArray();
        return names.All(n => n.Length > 0) && Regex.IsMatch(functions[0].Body,
            @"(?i)^\s*return\s+self\s*\.\s*" + Regex.Escape(canonical) + @"\s*\(\s*" + string.Join(@"\s*,\s*", names.Select(Regex.Escape)) + @"\s*\)\s*$");
    }

    private static bool BaseHasParentWithoutMethod(Catalog c, string script, string parent, string method) => c.RuntimeBaseScripts.GetValueOrDefault(script).Text is { Length: <= 1_000_000 } text
        && ScriptParent(text).Equals(parent, StringComparison.OrdinalIgnoreCase) && !HasScriptMethod(text, method);
    private static bool NativeListFrameContract(Catalog c, string method)
    {
        var signature = method.ToLowerInvariant() switch
        {
            "addform" => ("", new[] { "Form", "Int", "Int" }),
            "revert" => ("", Array.Empty<string>()),
            "removeaddedform" => ("", new[] { "Form" }),
            "setchancenone" => ("", new[] { "Int" }),
            "setchanceglobal" => ("", new[] { "GlobalVariable" }),
            "setnthlevel" or "setnthcount" => ("", new[] { "Int", "Int" }),
            "getchancenone" or "getnumforms" => ("Int", Array.Empty<string>()),
            "getnthlevel" or "getnthcount" => ("Int", new[] { "Int" }),
            _ => ("?", Array.Empty<string>())
        };
        return signature.Item1 != "?" && NativeFrameContract(c, "LeveledItem", method, signature.Item1, signature.Item2, false);
    }
    private static bool FormKeywordHelperFrameContract(Catalog c, ScriptFunction function)
    {
        lock (c.SliceLock)
        {
            const string key = "form-keyword-helper";
            if (c.RuntimeNativeContracts.TryGetValue(key, out var old)) return old;
            bool Refuse() => c.RuntimeNativeContracts[key] = false;
            var text = c.RuntimeBaseScripts.GetValueOrDefault("Form").Text!;
            var parameter = Regex.Match(function.Parameters, @"(?i)^\s*String\s+(\w+)\s*$");
            if (function.Kind != "function" || !parameter.Success
                || Regex.Matches(text, @"(?im)^[ \t]*(?:\w+[ \t]+)?function[ \t]+HasKeywordString\b").Count != 1
                || !Regex.IsMatch(text, @"(?im)^[ \t]*Bool[ \t]+function[ \t]+HasKeywordString[ \t]*\([ \t]*String[ \t]+"
                    + Regex.Escape(parameter.Groups[1].Value) + @"[ \t]*\)[ \t]*\r?$")) return Refuse();
            var argument = parameter.Groups[1].Value;
            var body = Regex.Match(function.Body, @"(?is)^\s*Keyword\s+(?<local>\w+)\s*=\s*Keyword\s*\.\s*GetKeyword\s*\(\s*"
                + Regex.Escape(argument) + @"\s*\)\s+if\s+\k<local>\s*==\s*none\s+return\s+false\s+endif\s+return\s+self\s*\.\s*HasKeyword\s*\(\s*\k<local>\s*\)\s*$");
            if (!body.Success || new[] { "Keyword", "self" }.Contains(argument, StringComparer.OrdinalIgnoreCase)
                || new[] { "Keyword", "self", argument }.Contains(body.Groups["local"].Value, StringComparer.OrdinalIgnoreCase)
                || Regex.IsMatch(MaskScriptStrings(text), @"(?im)^[ \t]*\w+(?:\[\])?[ \t]+(?:property[ \t]+)?Keyword\b")
                || HasScriptMethod(text, "Keyword")) return Refuse();
            var keyword = c.RuntimeBaseScripts.GetValueOrDefault("Keyword").Text;
            return c.RuntimeNativeContracts[key] = keyword is not null && ScriptParent(keyword).Equals("Form", StringComparison.OrdinalIgnoreCase)
                && NativeFrameContract(c, "Keyword", "GetKeyword", "Keyword", ["String"], true)
                && NativeFrameContract(c, "Form", "HasKeyword", "Bool", ["Keyword"], false);
        }
    }

    private static bool SafeInheritedQuestFrame(Catalog c)
    {
        lock (c.SliceLock)
        {
            if (c.RuntimeNativeContracts.TryGetValue("inherited-quest-frame", out var old)) return old;
            bool Refuse(string detail)
            {
                c.RuntimeNativeContractProblems["inherited-quest-frame"] = detail;
                return c.RuntimeNativeContracts["inherited-quest-frame"] = false;
            }
            foreach (var name in new[] { "Quest", "Form", "Alias", "ReferenceAlias", "Actor", "ObjectReference", "LeveledItem" })
            {
                var script = c.RuntimeBaseScripts.GetValueOrDefault(name);
                var text = script.Text;
                if (text is null) return Refuse($"Base script {name} is unavailable: {script.Evidence}");
                if (text.Length > 1_000_000) return Refuse($"Base script {name} exceeds the text bound");
                if (!Regex.IsMatch(text, @"(?im)^\s*scriptname\s+" + Regex.Escape(name) + @"\b"))
                    return Refuse($"Base script {name} has an unproved class declaration");
                var expectedParent = name switch { "Quest" or "ObjectReference" or "LeveledItem" => "Form", "Actor" => "ObjectReference", "ReferenceAlias" => "Alias", _ => "" };
                if (!ScriptParent(text).Equals(expectedParent, StringComparison.OrdinalIgnoreCase))
                    return Refuse($"Base script {name} has unexpected parent '{ScriptParent(text)}'; expected '{expectedParent}'");
                foreach (var f in ParseFunctions(text))
                {
                    if (f.Kind == "event" || f.Name.StartsWith("On", StringComparison.OrdinalIgnoreCase))
                    { if (!string.IsNullOrWhiteSpace(MaskScriptStrings(f.Body))) return Refuse($"Inherited callback {name}.{f.Name} has an unproved body"); }
                    // Alias helpers cannot be called by the exact adapter, which
                    // admits only native GetOwningQuest. Unknown self calls fail.
                    if (name is not ("Quest" or "Form")) continue;
                    if (name == "Form" && f.Name.Equals("HasKeywordString", StringComparison.OrdinalIgnoreCase))
                    {
                        if (!FormKeywordHelperFrameContract(c, f))
                            return Refuse("Base helper Form.HasKeywordString is outside the verified keyword lookup/query contract");
                        foreach (var derived in new[] { "Quest", "ObjectReference", "Actor", "LeveledItem" })
                            if (new[] { "HasKeyword", "HasKeywordString" }.Any(method => HasScriptMethod(c.RuntimeBaseScripts.GetValueOrDefault(derived).Text ?? "", method)))
                                return Refuse($"Base script {derived} overrides inherited Form keyword query dispatch");
                        continue;
                    }
                    foreach (var line in MaskScriptStrings(f.Body).Split('\n'))
                        foreach (Match use in Regex.Matches(line, @"(?i)\bself\b"))
                        {
                            var call = Regex.Match(line[(use.Index + use.Length)..], @"(?i)^\s*\.\s*(\w+)\s*\(");
                            if (!call.Success || !(QuestNativeContract(c, call.Groups[1].Value, out _, out _)
                                || call.Groups[1].Value.Equals("UpdateCurrentInstanceGlobal", StringComparison.OrdinalIgnoreCase)
                                    && NativeFrameContract(c, "Quest", "UpdateCurrentInstanceGlobal", "Bool", ["GlobalVariable"], false)))
                                return Refuse($"Base helper {name}.{f.Name} has an unproved self use: {line.Trim()}");
                        }
                }
            }
            return c.RuntimeNativeContracts["inherited-quest-frame"] = true;
        }
    }
    private static bool NativeFrameContract(Catalog c, string script, string method, string returns, string[] parameters, bool global)
    {
        var key = script + "/" + method;
        if (c.RuntimeNativeContracts.TryGetValue(key, out var cached)) return cached;
        var text = c.RuntimeBaseScripts.GetValueOrDefault(script).Text;
        if (text is null || text.Length > 1_000_000 || !Regex.IsMatch(text, @"(?im)^\s*scriptname\s+" + Regex.Escape(script) + @"\b")) return c.RuntimeNativeContracts[key] = false;
        if (script is "Quest" or "ObjectReference" or "LeveledItem" or "GlobalVariable" && !ScriptParent(text).Equals("Form", StringComparison.OrdinalIgnoreCase)) return c.RuntimeNativeContracts[key] = false;
        var headers = Regex.Matches(text, @"(?im)^[ \t]*(?:(?<return>\w+)[ \t]+)?function[ \t]+" + Regex.Escape(method) + @"[ \t]*\((?<params>[^)\n]*)\)(?<flags>[^\r\n]*)\r?$");
        if (headers.Count != 1) return c.RuntimeNativeContracts[key] = false;
        var header = headers[0]; var flags = header.Groups["flags"].Value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var types = SplitArguments(header.Groups["params"].Value).Select(p => Regex.Match(p, @"^\s*(\w+)\s+\w+(?:\s*=.*)?$").Groups[1].Value).ToArray();
        return c.RuntimeNativeContracts[key] = header.Groups["return"].Value.Equals(returns, StringComparison.OrdinalIgnoreCase)
            && types.SequenceEqual(parameters, StringComparer.OrdinalIgnoreCase) && flags.Contains("native", StringComparer.OrdinalIgnoreCase)
            && flags.Contains("global", StringComparer.OrdinalIgnoreCase) == global && flags.All(f => f.Equals("native", StringComparison.OrdinalIgnoreCase) || f.Equals("global", StringComparison.OrdinalIgnoreCase));
    }
}
