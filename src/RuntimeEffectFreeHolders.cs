using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;

namespace XEnchantmentPatcher;

public static partial class PatcherEngine
{
    private sealed record RuntimePublicHolder(FormKey Owner, int ScriptSlot, int PropertyIndex, string Property, short Alias, int ArrayIndex);
    private sealed record RuntimeHolderAsset(string? Text, string Evidence, string DiagnosticEvidence);
    private sealed record RuntimeHolderAttachment(FormKey Owner, string WinningPlugin, string Domain, int ScriptSlot,
        IScriptEntryGetter Entry, RuntimeHolderAsset Asset, FormKey? Base)
    {
        public string Identity => $"{Owner}/{Domain}/{ScriptSlot}/{Entry.Name}";
    }
    private sealed record RuntimeHolderViewCheck(FormKey Target, string? Problem, string Evidence,
        string[] CoveredAttachments, string FailureIdentity = "", string Function = "", string Line = "");
    private static readonly HashSet<string> HolderQueryMethods = new(StringComparer.OrdinalIgnoreCase)
        { "GetStage", "GetStageDone", "GetCurrentStageID", "IsStageDone", "IsRunning", "IsStarting", "IsStopping", "IsStopped", "IsActive", "IsCompleted" };
    private static bool HolderPrimitive(string type) => new[] { "Int", "Float", "Bool", "String" }.Contains(type, StringComparer.OrdinalIgnoreCase);

    // Complete the known reverse carrier component before publishing any
    // success. This is a universal property invariant over an effect template,
    // or a record-backed reference view, never an execution/lifecycle proof.
    private static RuntimeHolderViewCheck RuntimeEffectFreeHolderView(FormKey target, Catalog catalog)
        => RuntimeHolderComponentView(target, catalog.RuntimeNonQuestHolderIndex!.GetValueOrDefault(target, []), catalog);

    // ADR 0008/0009 (E1): only the source mod's own non-Quest holders matter, and each
    // is an in-scope script whose uses of slice values the mod's slice flow checks.
    // Another mod's holders are outside the proof.
    private static RuntimeHolderViewCheck RuntimeSliceHolderView(FormKey target, Catalog catalog, RuntimeSliceScope slice)
    {
        if (catalog.RuntimeSliceHolderViews.TryGetValue((target, slice.Source), out var cached)) return cached;
        var holders = catalog.RuntimeNonQuestHolderIndex!.GetValueOrDefault(target, []).Where(h => InSliceScope(h.Owner, slice.Source, catalog))
            .Select(h => h.Owner).Distinct().Order().ToArray();
        var result = holders.Length > 512
            ? new RuntimeHolderViewCheck(target, $"in-mod non-Quest holders of {target} exceed 512 attached scripts", "", [], holders[0].ToString())
            : new RuntimeHolderViewCheck(target, null, "slice-holder-view-v2 " + SliceEvidence(holders.Select(h => $"{target}/{h}")), []);
        return catalog.RuntimeSliceHolderViews[(target, slice.Source)] = result;
    }

    private static RuntimeHolderViewCheck RuntimeHolderComponentView(FormKey target, RuntimePublicHolder[] holders, Catalog catalog)
    {
        var winners = RuntimeRecordWinners(catalog);
        var completed = new HashSet<FormKey>(); var active = new HashSet<FormKey>();
        var component = new List<RuntimeHolderAttachment>(); string? graphProblem = null;
        var usedAssets = new Dictionary<string, RuntimeHolderAsset>(StringComparer.OrdinalIgnoreCase);
        void Canonical(string name)
        {
            var value = catalog.RuntimeBaseScripts.GetValueOrDefault(name);
            usedAssets["canonical:" + name] = new(value.Text, value.Evidence,
                catalog.RuntimeBaseScriptDiagnosticEvidence.GetValueOrDefault(name, ""));
        }
        RuntimeHolderAsset Load(string name)
        {
            if (usedAssets.TryGetValue(name, out var value)) return value;
            return usedAssets[name] = catalog.RuntimeHolderAssetResolver?.Invoke(name)
                ?? new(null, "holder asset resolver unavailable", "");
        }
        bool Visit(FormKey owner, int depth)
        {
            if (active.Contains(owner)) { graphProblem = $"cyclic non-Quest public carrier component at {owner}"; return false; }
            if (completed.Contains(owner)) return true;
            if (depth > 16 || completed.Count + active.Count >= 8192)
            { graphProblem = "non-Quest public carrier component exceeds 16 edges or 8192 states"; return false; }
            active.Add(owner);
            if (!winners.TryGetValue(owner, out var winner) || winner.Record.IsDeleted
                || winner.Record is not IHaveVirtualMachineAdapterGetter { VirtualMachineAdapter: { } adapter })
            { graphProblem = $"winning attachment metadata unavailable for {owner}"; return false; }
            var domain = winner.Record switch { IMagicEffectGetter => "ActiveMagicEffect-template", IPlacedObjectGetter => "ObjectReference-record",
                _ => "unsupported-attachment-domain" };
            FormKey? baseKey = winner.Record switch { IPlacedObjectGetter placed => placed.Base.FormKey, IPlacedNpcGetter placed => placed.Base.FormKey, _ => null };
            // Load every sibling even if a later domain/composition check fails.
            foreach (var (entry, slot) in adapter.Scripts.Select((s, i) => (s, i)))
            {
                if (component.Count >= 512) { graphProblem = "non-Quest public carrier component exceeds 512 attached scripts"; return false; }
                var row = new RuntimeHolderAttachment(owner, winner.Plugin.ToString(), domain, slot, entry, Load(entry.Name), baseKey);
                component.Add(row); catalog.RuntimeHolderAttachments[row.Identity] = row;
            }
            if (adapter.Scripts.Count == 0 || adapter.Scripts.GroupBy(s => s.Name, StringComparer.OrdinalIgnoreCase).Any(g => g.Count() != 1))
            { graphProblem = $"missing or duplicate bound sibling attachment on {owner}"; return false; }
            if (domain == "unsupported-attachment-domain") { graphProblem = $"typed runtime attachment domain is unproved for {owner}"; return false; }
            if (baseKey is { } key && (key.IsNull || !winners.TryGetValue(key, out var baseWinner) || baseWinner.Record.IsDeleted
                || domain == "ObjectReference-record" && baseWinner.Record is not IPlaceableObjectGetter
                || baseWinner.Record is IHaveVirtualMachineAdapterGetter { VirtualMachineAdapter: not null }))
            { graphProblem = $"placed/base runtime attachment composition is unproved for {owner}, base {key}"; return false; }
            // These domains have ordinary VMADs. Specialized adapter fragments
            // require an exact root-to-binding association outside this contract.
            if (adapter is IQuestAdapterGetter or IPackageAdapterGetter or ISceneAdapterGetter or IPerkAdapterGetter or IDialogResponsesAdapterGetter)
            { graphProblem = $"typed fragment attachment association is unproved for {owner}"; return false; }
            if (catalog.RuntimeFormListHolderParents!.GetValueOrDefault(owner, []).Length > 0)
            { graphProblem = $"reverse FormList carrier of holder {owner} has no closed public-view component"; return false; }
            // Native reference APIs and conditional alias fills can expose
            // authored record links without VMAD properties. Refuse every
            // incoming authored link to a placed holder in this slice.
            // Child major records have their own resolved winners. Do
            // not resurrect losing links through a containing CELL/WRLD.
            if (domain == "ObjectReference-record" && RuntimeDirectLinkCarrier(owner, winners, catalog) is { } linkCarrier)
            { graphProblem = $"authored record carrier {linkCarrier} of placed holder {owner} has no closed public-view component"; return false; }
            if (RuntimeQuestAliasCarriers(winners, catalog).TryGetValue(owner, out var aliasQuest))
            { graphProblem = $"authored Quest alias carrier on {aliasQuest} of holder {owner} has no closed public-view component"; return false; }
            if (catalog.RuntimeQuestHolderIndex!.GetValueOrDefault(owner, []).FirstOrDefault() is { } questCarrier)
            { graphProblem = $"incoming Quest carrier {questCarrier.Identity}/{questCarrier.Property} of {owner} has no effect-free non-Quest view proof"; return false; }
            foreach (var carrier in catalog.RuntimeNonQuestHolderIndex!.GetValueOrDefault(owner, []))
            {
                if (carrier.Alias != -1 || carrier.ArrayIndex != -1)
                { graphProblem = $"incoming alias/array carrier of {owner} is unproved"; return false; }
                if (!Visit(carrier.Owner, depth + 1)) return false;
            }
            active.Remove(owner); completed.Add(owner); return true;
        }
        foreach (var holder in holders)
        {
            if (holder.Alias != -1 || holder.ArrayIndex != -1)
                return new(target, "relevant non-Quest alias/array capability is outside the effect-free view", "", []);
            if (!Visit(holder.Owner, 0)) return new(target, graphProblem, "", []);
        }
        foreach (var row in component.OrderBy(r => r.Identity, StringComparer.Ordinal))
        {
            var result = ValidateEffectFreeHolder(row, catalog, Load, Canonical);
            if (result is not null) return new(target, result.Value.Problem, "", [], row.Identity, result.Value.Function, result.Value.Line);
        }
        var signature = "effect-free-holder-view-v1|" + target + "|" + string.Join('|', component.OrderBy(r => r.Identity, StringComparer.Ordinal).Select(r =>
            $"{r.Identity}/{r.WinningPlugin}/{r.Base}/{r.Asset.Evidence}/{r.Asset.Text}/" + string.Join(';', r.Entry.Properties.Select(p =>
                $"{p.Name}/{p.GetType().Name}/{p.Flags}/" + (p is IScriptObjectPropertyGetter h ? $"{h.Object.FormKey}/{h.Alias}"
                    : p is IScriptObjectListPropertyGetter a ? string.Join(',', a.Objects.Select(h => $"{h.Object.FormKey}/{h.Alias}")) : "primitive-or-opaque-value")))))
            + "|" + string.Join('|', usedAssets.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase).Select(p => $"{p.Key}/{p.Value.Evidence}/{p.Value.Text}"));
        // Attachment identities are published only after the whole component passes.
        return new(target, null, "effect-free-holder-view-v1 " + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(signature))),
            component.Select(r => r.Identity).Order(StringComparer.Ordinal).ToArray());
    }

    // The first winning non-deleted Quest, in winner order, whose alias forces each
    // reference: what scanning every winner per visit found.
    private static Dictionary<FormKey, FormKey> RuntimeQuestAliasCarriers(
        Dictionary<FormKey, (ISkyrimMajorRecordGetter Record, ModKey Plugin)> winners, Catalog catalog)
        => SharedOnce(catalog, () => catalog.RuntimeQuestAliasCarrierIndex, value => catalog.RuntimeQuestAliasCarrierIndex = value, () =>
        {
            var index = new Dictionary<FormKey, FormKey>();
            foreach (var quest in winners.Values.Select(w => w.Record).OfType<IQuestGetter>().Where(q => !q.IsDeleted))
                foreach (var alias in quest.Aliases)
                    if (alias.ForcedReference?.FormKey is { } forced) index.TryAdd(forced, quest.FormKey);
            return index;
        });

    // The first winning non-deleted record other than the holder itself, in winner
    // order, with a direct (non-nested) link to it. Two carriers per target suffice:
    // only a record's own self-link is skipped.
    private static FormKey? RuntimeDirectLinkCarrier(FormKey owner,
        Dictionary<FormKey, (ISkyrimMajorRecordGetter Record, ModKey Plugin)> winners, Catalog catalog)
    {
        var index = SharedOnce(catalog, () => catalog.RuntimeDirectLinkCarrierIndex, value => catalog.RuntimeDirectLinkCarrierIndex = value, () =>
        {
            var carriers = new Dictionary<FormKey, (FormKey First, FormKey? Second)>();
            foreach (var carrier in winners.Values.Select(w => w.Record).Where(p => !p.IsDeleted))
                foreach (var link in carrier.EnumerateFormLinks(iterateNestedRecords: false))
                {
                    if (!carriers.TryGetValue(link.FormKey, out var known)) carriers[link.FormKey] = (carrier.FormKey, null);
                    else if (known.Second is null && known.First != carrier.FormKey) carriers[link.FormKey] = (known.First, carrier.FormKey);
                }
            return carriers;
        });
        if (!index.TryGetValue(owner, out var found)) return null;
        return found.First != owner ? found.First : found.Second;
    }

    private sealed partial class Catalog
    {
        public Dictionary<FormKey, FormKey>? RuntimeQuestAliasCarrierIndex;
        public Dictionary<FormKey, (FormKey First, FormKey? Second)>? RuntimeDirectLinkCarrierIndex;
    }

    private static (string Problem, string Function, string Line)? ValidateEffectFreeHolder(RuntimeHolderAttachment row, Catalog catalog,
        Func<string, RuntimeHolderAsset> load, Action<string> canonical)
    {
        (string, string, string) Refuse(string problem, string function = "", string line = "") => (problem, function, line.Trim());
        var text = row.Asset.Text;
        if (text is null || text.Length > 1_000_000) return Refuse("winning holder text unavailable or exceeds the text bound");
        if (HolderCanonicalParentProblem(row, text, catalog, load, canonical, out var callbackHeaders) is { } parentProblem) return parentProblem;
        if (row.Entry.Properties.GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase).Any(g => g.Count() != 1))
            return Refuse("duplicate holder VMAD property bindings");
        if (row.Entry.Properties.Any(p => p is IScriptObjectListPropertyGetter || p is IScriptObjectPropertyGetter h && h.Alias != -1))
            return Refuse("holder arrays/aliases are outside the effect-free view");
        var functions = ParseFunctions(text);
        if (functions.Count > 512 || functions.GroupBy(f => f.Name, StringComparer.OrdinalIgnoreCase).Any(g => g.Count() != 1))
            return Refuse("holder functions exceed the bound or have ambiguous dispatch");
        var primitives = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var readonlyPrimitives = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var quests = new Dictionary<string, IQuestGetter>(StringComparer.OrdinalIgnoreCase);
        var outside = MaskScriptStrings(text).ToCharArray();
        foreach (var f in functions) Array.Fill(outside, ' ', f.Start, f.End - f.Start);
        foreach (var raw in new string(outside).Split('\n'))
        {
            var line = raw.Trim(); if (line.Length == 0 || line.StartsWith("scriptname ", StringComparison.OrdinalIgnoreCase)) continue;
            var property = Regex.Match(line, @"(?i)^(\w+)\s+property\s+(\w+)\s+(auto|autoreadonly)(?:\s+hidden)?$");
            if (!property.Success) return Refuse("holder state/accessor/global declaration is outside the effect-free view", "", raw);
            var type = property.Groups[1].Value; var name = property.Groups[2].Value;
            if (HolderPrimitive(type))
            { if (!primitives.Add(name)) return Refuse("duplicate primitive holder declaration"); if (property.Groups[3].Value.Equals("autoreadonly", StringComparison.OrdinalIgnoreCase)) readonlyPrimitives.Add(name); continue; }
            var binding = row.Entry.Properties.OfType<IScriptObjectPropertyGetter>().SingleOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (binding is null || binding.Object.FormKey.IsNull || !RuntimeRecordWinners(catalog).TryGetValue(binding.Object.FormKey, out var winner)
                || winner.Record.IsDeleted || winner.Record is not IQuestGetter quest || quest.VirtualMachineAdapter is null)
                return Refuse($"holder property {name} is not an exact winning scalar Quest view", "", raw);
            if (!(type.Equals("Quest", StringComparison.OrdinalIgnoreCase) || quest.VirtualMachineAdapter.Scripts.Any(s => s.Name.Equals(type, StringComparison.OrdinalIgnoreCase))))
                return Refuse($"holder property {name} has an unproved typed Quest view", "", raw);
            if (!quests.TryAdd(name, quest)) return Refuse("duplicate holder Quest declaration");
        }
        if (row.Entry.Properties.OfType<IScriptObjectPropertyGetter>().Any(p => !quests.ContainsKey(p.Name)))
            return Refuse("unmatched holder object binding is outside the effect-free view");
        foreach (var function in functions)
        {
            if (function.Kind == "function" && function.ReturnType.Length > 0 && !HolderPrimitive(function.ReturnType))
                return Refuse("holder function exports a nonprimitive return", function.Name);
            var parameters = SplitArguments(function.Parameters).Select(p => Regex.Match(p, @"(?i)^\s*(\w+)\s+(\w+)\s*$")).ToArray();
            if (parameters.Any(p => !p.Success)) return Refuse("holder parameter/default syntax is unproved", function.Name);
            if (function.Kind == "event")
            {
                if (!callbackHeaders.TryGetValue(function.Name, out var inherited)
                    || !SplitArguments(inherited.Parameters).Select(p => Regex.Match(p, @"^\s*(\w+)\s+\w+\s*$").Groups[1].Value)
                        .SequenceEqual(parameters.Select(p => p.Groups[1].Value), StringComparer.OrdinalIgnoreCase))
                    return Refuse("holder callback signature has no exact canonical parent declaration", function.Name);
            }
            else if (parameters.Any(p => !HolderPrimitive(p.Groups[1].Value))) return Refuse("holder function has a nonprimitive parameter", function.Name);
            var locals = new HashSet<string>(primitives, StringComparer.OrdinalIgnoreCase);
            foreach (var p in parameters)
            {
                if (quests.ContainsKey(p.Groups[2].Value) || primitives.Contains(p.Groups[2].Value)) return Refuse("holder parameter shadows a bound member", function.Name);
                if (HolderPrimitive(p.Groups[1].Value) && !locals.Add(p.Groups[2].Value)) return Refuse("duplicate holder parameter", function.Name);
            }
            foreach (var raw in function.Body.Split('\n'))
            {
                var line = raw.Trim(); if (line.Length == 0) continue;
                if (Regex.IsMatch(line, @"(?i)^(endif|else)$")) continue;
                var control = Regex.Match(line, @"(?i)^(if|elseif|return)\b(.*)$");
                if (control.Success)
                {
                    if (control.Groups[1].Value.Equals("return", StringComparison.OrdinalIgnoreCase) && control.Groups[2].Value.Trim().Length == 0) continue;
                    if (!PureHolderExpression(control.Groups[2].Value, locals, quests, catalog, load, canonical)) return Refuse("holder control/return expression has an unclosed effect or handle use", function.Name, raw);
                    continue;
                }
                var local = Regex.Match(line, @"(?i)^(int|float|bool|string)\s+(\w+)(?:\s*=\s*(.*))?$");
                if (local.Success)
                {
                    var name = local.Groups[2].Value;
                    if (quests.ContainsKey(name) || !locals.Add(name)) return Refuse("holder local shadows a bound member/parameter", function.Name, raw);
                    if (local.Groups[3].Success && !PureHolderExpression(local.Groups[3].Value, locals, quests, catalog, load, canonical)) return Refuse("holder local initializer has an unclosed effect or handle use", function.Name, raw);
                    continue;
                }
                var assignment = Regex.Match(line, @"^(\w+)\s*[+\-*/]?=(?!=)\s*(.*)$");
                if (!assignment.Success || !locals.Contains(assignment.Groups[1].Value) || readonlyPrimitives.Contains(assignment.Groups[1].Value)
                    || !PureHolderExpression(assignment.Groups[2].Value, locals, quests, catalog, load, canonical))
                    return Refuse("holder call/write/export is outside the effect-free view", function.Name, raw);
            }
        }
        return null;
    }

    // The holder declares its exact attachment domain, and its canonical native
    // parents have no callback/helper bodies, states or properties.
    private static (string Problem, string Function, string Line)? HolderCanonicalParentProblem(RuntimeHolderAttachment row, string text,
        Catalog catalog, Func<string, RuntimeHolderAsset> load, Action<string> canonical, out Dictionary<string, ScriptFunction> callbackHeaders)
    {
        (string, string, string) Refuse(string problem, string function = "", string line = "") => (problem, function, line.Trim());
        callbackHeaders = new(StringComparer.OrdinalIgnoreCase);
        var headers = Regex.Matches(MaskScriptStrings(text), @"(?im)^\s*scriptname\s+(\w+)\s+extends\s+(\w+)(?:\s+hidden)?\s*$");
        var expected = row.Domain.Split('-')[0];
        if (headers.Count != 1 || !headers[0].Groups[1].Value.Equals(row.Entry.Name, StringComparison.OrdinalIgnoreCase)
            || !headers[0].Groups[2].Value.Equals(expected, StringComparison.OrdinalIgnoreCase))
            return Refuse("holder declaration/parent does not match its exact attachment domain");
        // Read the exact associated AME declaration instead of inferring its
        // native ancestry from the MagicEffect record or a synthetic fixture.
        var effectParent = expected == "ActiveMagicEffect" ? ScriptParent(load("ActiveMagicEffect").Text ?? "") : "";
        if (expected == "ActiveMagicEffect" && effectParent.Length > 0 && !effectParent.Equals("Form", StringComparison.OrdinalIgnoreCase))
            return Refuse("ActiveMagicEffect native parent is outside the supported declared chain");
        var bases = expected == "ActiveMagicEffect" ? (effectParent.Length == 0 ? new[] { "ActiveMagicEffect" } : new[] { "ActiveMagicEffect", "Form" })
            : new[] { "ObjectReference", "Form" };
        foreach (var name in bases)
        {
            var asset = load(name); var body = asset.Text;
            var parent = name == "ActiveMagicEffect" ? effectParent : name == "ObjectReference" ? "Form" : "";
            if (body is null || body.Length > 1_000_000 || !Regex.IsMatch(body, @"(?im)^\s*scriptname\s+" + Regex.Escape(name) + @"\b")
                || Regex.Matches(MaskScriptStrings(body), @"(?im)^\s*scriptname\b").Count != 1
                || !ScriptParent(body).Equals(parent, StringComparison.OrdinalIgnoreCase))
                return Refuse($"canonical holder parent {name} declaration is unproved");
            var outsideBase = MaskScriptStrings(body).ToCharArray();
            foreach (var f in ParseFunctions(body))
            {
                Array.Fill(outsideBase, ' ', f.Start, f.End - f.Start);
                if (!string.IsNullOrWhiteSpace(MaskScriptStrings(f.Body)))
                {
                    if (name == "Form" && f.Name.Equals("HasKeywordString", StringComparison.OrdinalIgnoreCase))
                    {
                        canonical("Form"); canonical("Keyword");
                        if (FormKeywordHelperFrameContract(catalog, f)) continue;
                    }
                    return Refuse($"inherited holder callback/helper {name}.{f.Name} has an unclosed body");
                }
                if (f.Kind == "event" && !callbackHeaders.TryAdd(f.Name, f)) return Refuse("ambiguous inherited holder callback declaration");
            }
            if (Regex.IsMatch(MaskScriptStrings(body), @"(?im)^\s*(?:auto\s+)?state\b|\bproperty\b"))
                return Refuse($"inherited holder state/property capabilities in {name} are unproved");
            foreach (var raw in new string(outsideBase).Split('\n'))
            {
                var line = raw.Trim(); if (line.Length == 0 || line.StartsWith("scriptname ", StringComparison.OrdinalIgnoreCase)) continue;
                var declaration = Regex.Match(line, @"(?i)^(?:\w+(?:\[\])?\s+)?function\s+(\w+)\s*\([^()]*\)\s+(?:global\s+)?native(?:\s+global)?$");
                if (!declaration.Success || declaration.Groups[1].Value.StartsWith("On", StringComparison.OrdinalIgnoreCase))
                    return Refuse($"unparsed or native inherited callback/interface on {name}", "", raw);
            }
        }
        return null;
    }

    private static bool PureHolderExpression(string expression, HashSet<string> primitives, Dictionary<string, IQuestGetter> quests,
        Catalog catalog, Func<string, RuntimeHolderAsset> load, Action<string> canonical)
    {
        if (string.IsNullOrWhiteSpace(expression)) return false;
        var pure = true;
        var text = Regex.Replace(expression, "\"(?:\\\\.|[^\"\\\\])*\"", "0");
        text = Regex.Replace(text, @"(?i)\b(\w+)\s*\.\s*(\w+)\s*\(([^()]*)\)", match =>
        {
            canonical("Quest");
            if (!quests.TryGetValue(match.Groups[1].Value, out var quest) || !HolderQueryMethods.Contains(match.Groups[2].Value)
                || !QuestNativeContract(catalog, match.Groups[2].Value, out var dependencies, out var count)) { pure = false; return "0"; }
            var args = SplitArguments(match.Groups[3].Value);
            if (args.Length != count || args.Any(a => !PureHolderExpression(a, primitives, [], catalog, load, canonical))) { pure = false; return "0"; }
            var adapter = quest.VirtualMachineAdapter!;
            if (adapter.Scripts.GroupBy(s => s.Name, StringComparer.OrdinalIgnoreCase).Any(g => g.Count() != 1)
                || adapter.Fragments.Select(f => f.ScriptName).Prepend(adapter.FileName).Where(n => !string.IsNullOrWhiteSpace(n))
                    .Any(n => !adapter.Scripts.Any(s => s.Name.Equals(n, StringComparison.OrdinalIgnoreCase)))) { pure = false; return "0"; }
            foreach (var sibling in adapter.Scripts)
            {
                var body = load(sibling.Name).Text;
                if (body is null || !ScriptParent(body).Equals("Quest", StringComparison.OrdinalIgnoreCase)
                    || Regex.Matches(MaskScriptStrings(body), @"(?im)^\s*scriptname\b").Count != 1
                    || !Regex.IsMatch(body, @"(?im)^\s*scriptname\s+" + Regex.Escape(sibling.Name) + @"\b")
                    || dependencies.Any(d => HasScriptMethod(body, d))) { pure = false; return "0"; }
            }
            return "0";
        });
        if (Regex.IsMatch(text, @"\b\w+\s*\(")) return false;
        text = Regex.Replace(text, @"(?i)\b[A-Za-z_]\w*\b", match =>
        {
            if (!primitives.Contains(match.Value) && !new[] { "true", "false", "and", "or", "as", "int", "float", "bool", "string" }.Contains(match.Value, StringComparer.OrdinalIgnoreCase)) pure = false;
            return "0";
        });
        // No call, member, indexing, assignment or unparsed token survives.
        text = Regex.Replace(text, @"\d+(?:\.\d+)?", "0");
        return pure && Regex.IsMatch(text, @"^[\s0+*/%<>=!&|()\-]+$") && !Regex.IsMatch(text, @"(?<![<>=!])=(?![=])");
    }
}
