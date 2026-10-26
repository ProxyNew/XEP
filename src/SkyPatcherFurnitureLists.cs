using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;

namespace XEnchantmentPatcher;

public static partial class PatcherEngine
{
    // A negative footprint proof, not a FormList distribution interpreter.
    // Every enabled FormList rule must stay inside the proved furniture surface.
    private static SkyPatcherInputReport WithFurnitureListIrrelevance(SkyPatcherInputReport inputs,
        NativeFormResolver resolver, Dictionary<FormKey, (ISkyrimMajorRecordGetter Record, ModKey Plugin)> winners,
        PendingScriptAssets? pending)
    {
        var prior = inputs.FurnitureProofInformation.ToHashSet(StringComparer.Ordinal);
        inputs = inputs with
        {
            IrrelevantFurnitureFiles = new HashSet<string>(), FurnitureProofInformation = [],
            Fingerprint = inputs.FurnitureProofBaseFingerprint ?? inputs.Fingerprint,
            FurnitureProofBaseFingerprint = inputs.FurnitureProofBaseFingerprint ?? inputs.Fingerprint,
            Diagnostics = inputs.Diagnostics.Where(d => !prior.Contains(d)).ToArray(),
            Information = inputs.Information.Where(d => !prior.Contains(d)).ToArray()
        };
        var files = inputs.Files.Where(f => f.Status == "other-category-unanalysed"
            && string.Equals(f.Path.Split('/').ElementAtOrDefault(1), "formList", StringComparison.OrdinalIgnoreCase)
            && SkyPatcherLists.CategoryMayRun(inputs, "formList") && f.Rules.Count > 0).ToArray();
        if (files.Length == 0 || inputs.Status != "input-contract-7.0") return inputs;
        var targets = new HashSet<FormKey>();
        var evidence = new List<string>();
        foreach (var file in files)
        foreach (var rule in file.Rules)
        {
            var pairs = rule.Text.Split(':').Select(c => c.Split('=')).ToArray();
            if (pairs.Length != 2 || pairs.Any(p => p.Length != 2 || p[1].Trim().Length == 0)
                || pairs[0][0].Trim() != "filterByFormLists" || pairs[1][0].Trim() != "formsToAdd") return inputs;
            var target = Resolve(pairs[0][1].Trim(), typeof(IFormListGetter));
            if (target.Status != "resolved" || target.FormKey is null) return inputs;
            var key = FormKey.Factory(target.FormKey);
            if (resolver.Winner(key) is not IFormListGetter list || list.IsDeleted) return inputs;
            foreach (var member in list.Items)
                if (resolver.Winner(member.FormKey) is not IFurnitureGetter furniture || furniture.IsDeleted) return inputs;
            targets.Add(key);
            evidence.Add($"{key}:" + string.Join(',', list.Items.Select(i => i.FormKey)));
            foreach (var raw in pairs[1][1].Split(','))
            {
                var added = Resolve(raw.Trim(), typeof(ISkyrimMajorRecordGetter));
                if (added.Status is "missing-plugin" or "missing-record") continue;
                if (added.Status != "resolved" || added.FormKey is null
                    || resolver.Winner(FormKey.Factory(added.FormKey)) is not IFurnitureGetter furniture || furniture.IsDeleted) return inputs;
                evidence.Add(added.FormKey);
            }
        }
        var texts = new Dictionary<string, (string? Text, string Evidence)>(StringComparer.OrdinalIgnoreCase);
        foreach (var target in targets.Order())
        {
            var consumers = 0;
            foreach (var (record, _) in winners.Values)
            {
                if (record.IsDeleted) continue;
                var links = record.EnumerateFormLinks().Count(l => l.FormKey == target);
                if (links == 0) continue;
                // DIAL enumerates nested INFO links too. Inspect its own fields
                // separately, then inspect the winning INFOs below.
                if (record is IDialogTopicGetter topic)
                {
                    if (topic.Responses.Any(r => !winners.ContainsKey(r.FormKey))) return inputs;
                    var shell = topic.DeepCopy();
                    shell.Responses.Clear();
                    if (shell.EnumerateFormLinks().Any(l => l.FormKey == target)) return inputs;
                    continue;
                }
                if (record is not IDialogResponsesGetter response || response.VirtualMachineAdapter is not { } adapter) return inputs;
                var structural = response.DeepCopy();
                structural.VirtualMachineAdapter = null;
                if (structural.EnumerateFormLinks().Any(l => l.FormKey == target)
                    || adapter.Scripts.SelectMany(s => s.Properties.OfType<IScriptObjectListPropertyGetter>())
                        .SelectMany(p => p.Objects).Any(p => p.Object.FormKey == target)) return inputs;
                var bindings = adapter.Scripts.SelectMany(s => s.Properties.OfType<IScriptObjectPropertyGetter>()
                    .Where(p => p.Object.FormKey == target).Select(p => (Script: s, Property: p))).ToArray();
                if (bindings.Length == 0) return inputs;
                foreach (var (script, property) in bindings)
                {
                    if (property.Alias != -1 || script.Properties.GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase).Any(g => g.Count() != 1)) return inputs;
                    if (adapter.ScriptFragments is { } fragments
                        && new[] { fragments.FileName, fragments.OnBegin?.ScriptName, fragments.OnEnd?.ScriptName }
                            .Any(n => !string.IsNullOrEmpty(n) && !n.Equals(script.Name, StringComparison.OrdinalIgnoreCase))) return inputs;
                    if (!texts.TryGetValue(script.Name, out var asset))
                    {
                        if (ScriptTextOverride is not null) asset = ScriptTextOverride(script.Name);
                        else if (pending is not null)
                        {
                            var assets = pending.Work.GetAwaiter().GetResult().Assets;
                            var text = assets.Decompile(script.Name, out var detail);
                            asset = (text, detail);
                        }
                        else return inputs;
                        texts.Add(script.Name, asset);
                    }
                    if (asset.Text is null || !FurnitureOwnershipConsumer(asset.Text, property.Name, script, resolver, adapter.ScriptFragments)) return inputs;
                    evidence.Add($"{record.FormKey}/{script.Name}/{property.Name}: {asset.Evidence}; {Digest(asset.Text)}");
                    consumers++;
                }
            }
            if (consumers == 0) return inputs;
        }
        var messages = files.Select(f => $"SkyPatcher {f.Path}: ignored furniture-only FormList additions; known consumers only use the list for nearest-furniture lookup and ownership. Captured rules are retained in the report.").ToArray();
        return inputs with
        {
            IrrelevantFurnitureFiles = files.Select(f => f.Path).ToHashSet(StringComparer.Ordinal),
            Diagnostics = inputs.Diagnostics.Concat(messages).ToArray(),
            Information = inputs.Information.Concat(messages).ToArray(),
            FurnitureProofInformation = messages,
            Fingerprint = Digest(inputs.Fingerprint + "\nfurniture-list-proof-v1\n" + string.Join('\n', evidence.Order(StringComparer.Ordinal)))
        };

        SkyPatcherReference Resolve(string text, Type expected)
            => text.Length == 0 || text.IndexOfAny(['~', '=', ':']) >= 0
                ? new("formList", text, "malformed-identifier", null)
                : !text.Contains('|') && !inputs.EditorIdCacheAvailable
                    ? new("formList", text, "editorid-cache-unavailable", null)
                : SkyPatcherInputs.Resolve(resolver, "formList", text, expected);
        static string Digest(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    }

    private static bool FurnitureOwnershipConsumer(string text, string property, IScriptEntryGetter binding, NativeFormResolver resolver, IScriptFragmentsGetter? fragments)
    {
        var masked = MaskPapyrusText(text, strings: false);
        if (masked is null || !Regex.IsMatch(masked, @"(?im)^\s*scriptname\s+" + Regex.Escape(binding.Name) + @"\s+extends\s+TopicInfo(?:\s+hidden)?\s*$")) return false;
        var name = Regex.Escape(property);
        var functions = ParseFunctions(masked);
        var consumers = functions.Where(f => Regex.IsMatch(f.Body, @"(?i)\b" + name + @"\b")).ToArray();
        if (consumers.Length != 1) return false;
        var function = consumers[0];
        var fragmentNames = new[] { fragments?.OnBegin?.FragmentName, fragments?.OnEnd?.FragmentName }
            .OfType<string>().Where(n => n.Length > 0).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (fragments is not null && (!fragmentNames.Contains(function.Name)
            || fragmentNames.Any(n => !functions.Any(f => f.Name.Equals(n, StringComparison.OrdinalIgnoreCase))))) return false;
        foreach (var sibling in functions.Where(f => f != function))
        {
            // TopicInfo scripts can retain a bound or unused cast-only fragment.
            // Admit that closed no-op body, not arbitrary helper actions.
            var parameter = Regex.Match(sibling.Parameters, @"(?i)^\s*ObjectReference\s+(?<speaker>\w+)\s*$");
            if (!parameter.Success || !Regex.IsMatch(sibling.Body, @"(?i)^\s*(?:actor\s+\w+\s*=\s*"
                + Regex.Escape(parameter.Groups["speaker"].Value) + @"\s+as\s+actor\s*)?$")) return false;
        }
        if (!Regex.IsMatch(masked, @"(?im)^\s*FormList\s+Property\s+" + name + @"\s+auto\s*$")
            || Regex.Matches(masked, @"(?i)\b" + name + @"\b").Count != 2
            || !string.Equals(DeclaredType(masked, function, property), "FormList", StringComparison.OrdinalIgnoreCase)) return false;
        // The complete body is closed: no branch, property/index use, escape,
        // equipment operation or action depending on the lookup result except ownership.
        var pattern = @"^\s*actor\s+(?<actor>\w+)\s*=\s*(?<speaker>\w+)\s+as\s+actor\s+"
            + @"(?<scene>\w+)\s*\.\s*Start\s*\(\s*\)\s+"
            + @"\k<actor>\s*\.\s*SetFactionRank\s*\(\s*(?<faction>\w+)\s*,\s*1\s*\)\s+"
            + @"ObjectReference\s+(?<bed>\w+)\s*=\s*game\s*\.\s*FindClosestReferenceOfAnyTypeInListFromRef\s*\(\s*" + name
            + @"\s*,\s*game\s*\.\s*GetPlayer\s*\(\s*\)\s+as\s+ObjectReference\s*,\s*[0-9]+(?:\.[0-9]+)?\s*\)\s+"
            + @"\k<bed>\s*\.\s*SetFactionOwner\s*\(\s*\k<faction>\s*\)\s*$";
        var match = Regex.Match(function.Body, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!match.Success || !Regex.IsMatch(function.Parameters, @"(?i)^\s*ObjectReference\s+" + Regex.Escape(match.Groups["speaker"].Value) + @"\s*$")) return false;
        return Bound("scene", typeof(ISceneGetter), "Scene") && Bound("faction", typeof(IFactionGetter), "Faction");

        bool Bound(string group, Type type, string scriptType)
        {
            var local = match.Groups[group].Value;
            var value = binding.Properties.OfType<IScriptObjectPropertyGetter>().SingleOrDefault(p => p.Name.Equals(local, StringComparison.OrdinalIgnoreCase));
            return value is not null && value.Alias == -1 && resolver.Winner(value.Object.FormKey) is { IsDeleted: false } record
                && type.IsInstanceOfType(record) && string.Equals(DeclaredType(masked, function, local), scriptType, StringComparison.OrdinalIgnoreCase);
        }
    }
}
