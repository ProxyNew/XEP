using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Mutagen.Bethesda.Plugins;

namespace XEnchantmentPatcher;

public static partial class PatcherEngine
{
    private sealed record RuntimeLocalArrayEffect(string Receiver, string CapturedTarget, string Method,
        string PayloadExpression, string Level, string Count, int Offset, bool TargetBindingProved);
    private sealed record RuntimeLocalArrayIdentity(string OwnerIdentity, string Function, string Expression,
        int Offset, int? Length, int? Index, string[] PossibleIdentities, bool MayBeNone,
        bool UnknownIdentityPossible, string? Problem, string Evidence, RuntimeLocalArrayEffect[] ObservedEffects);

    // An identity domain conditional on this exact expression being evaluated.
    // Identity alone does not establish a receiver binding, footprint,
    // availability, counts or controllers.
    // Live FLST/VMAD arrays and persistent storage are deliberately excluded.
    private static RuntimeLocalArrayIdentity FreshRuntimeArrayIdentity(string expression, ScriptInstance instance,
        ScriptFunction? function, int position, Catalog catalog)
    {
        RuntimeLocalArrayIdentity Refuse(string problem) => new(instance.Identity, function?.Name ?? "", expression,
            position, null, null, [], true, true, problem, "", []);
        if (instance.Text is null || function is null || instance.Text.Length > 1_000_000
            || !instance.Functions.Contains(function) || position < function.BodyStart || position >= function.End)
            return Refuse("local array identity has no bounded associated caller body");
        var read = Regex.Match(expression, @"(?i)^(\w+)\s*\[\s*(\d+)\s*\]$");
        if (!read.Success || !int.TryParse(read.Groups[2].Value, out var index))
            return Refuse("local array read requires one literal index");
        var name = read.Groups[1].Value; var token = @"(?i)\b" + Regex.Escape(name) + @"\b";
        if (HasParameter(function, name) || instance.Objects.ContainsKey(name) || instance.Arrays.ContainsKey(name))
            return Refuse("local array has a parameter or VMAD storage collision");
        var outside = MaskScriptStrings(instance.Text).ToCharArray();
        foreach (var member in instance.Functions) Array.Fill(outside, ' ', member.Start, member.End - member.Start);
        if (Regex.IsMatch(new string(outside), token)
            || Regex.IsMatch(MaskScriptStrings(instance.Text), @"(?im)^\s*(?:auto\s+)?state\b"))
            return Refuse("local array shadows persistent storage or belongs to an unproved state");
        if (instance.Functions.GroupBy(f => f.Name, StringComparer.OrdinalIgnoreCase).Any(g => g.Count() != 1)
            || Regex.Matches(MaskScriptStrings(instance.Text), @"(?im)^\s*scriptname\b").Count != 1
            || !Regex.IsMatch(instance.Text, @"(?im)^\s*scriptname\s+" + Regex.Escape(instance.Name) + @"\b"))
            return Refuse("local array associated class/function dispatch is ambiguous");
        var form = catalog.RuntimeBaseScripts.GetValueOrDefault("Form");
        bool CanonicalClass(string script, string parent = "")
        {
            var text = catalog.RuntimeBaseScripts.GetValueOrDefault(script).Text;
            return text is { Length: <= 1_000_000 }
                && Regex.Matches(MaskScriptStrings(text), @"(?im)^\s*scriptname\b").Count == 1
                && ScriptParent(text).Equals(parent, StringComparison.OrdinalIgnoreCase)
                && Regex.IsMatch(text, @"(?im)^[ \t]*scriptname[ \t]+" + Regex.Escape(script)
                    + (parent.Length == 0 ? "" : @"[ \t]+extends[ \t]+" + Regex.Escape(parent)) + @"(?:[ \t]+hidden)?[ \t]*\r?$");
        }
        if (!CanonicalClass("Form"))
            return Refuse("canonical Form root declaration is unproved");
        var masked = MaskScriptStrings(function.Body);
        var allocations = Regex.Matches(masked, @"(?im)^\s*Form\[\]\s+" + Regex.Escape(name)
            + @"\s*=\s*new\s+Form\[\s*(\d+)\s*\]\s*$");
        if (allocations.Count != 1 || !int.TryParse(allocations[0].Groups[1].Value, out var length) || length is < 1 or > 128)
            return Refuse("one fresh function-local Form array within the 1..128 analysis bound is required");
        if (index >= length) return Refuse("local array read index is outside its literal allocation");
        var cells = Enumerable.Range(0, length).Select(_ => (Key: (FormKey?)null, Evidence: "initial None")).ToArray();
        (FormKey? Key, string Evidence)? valueAtRead = null;
        var effects = new List<RuntimeLocalArrayEffect>(); var offset = function.BodyStart; var allocated = false;
        var stores = new List<string>();
        var primitives = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var lines = function.Body.Split('\n');
        if (lines.Length > 1024) return Refuse("local array function exceeds the 1024-line analysis bound");
        foreach (var raw in lines)
        {
            var start = offset; offset += raw.Length + 1; var line = raw.Trim();
            if (line.Length == 0) continue;
            var maskedLine = MaskScriptStrings(raw).Trim();
            var allocation = Regex.Match(maskedLine, @"(?i)^Form\[\]\s+" + Regex.Escape(name)
                + @"\s*=\s*new\s+Form\[\s*\d+\s*\]\s*$");
            if (allocation.Success) { if (allocated) return Refuse("local array allocation is repeated"); allocated = true; continue; }
            var store = Regex.Match(line, @"(?i)^" + Regex.Escape(name) + @"\s*\[\s*(\d+)\s*\]\s*=\s*(.+)$");
            if (store.Success)
            {
                if (!allocated || !int.TryParse(store.Groups[1].Value, out var slot) || slot >= length)
                    return Refuse("local array store precedes allocation or has an invalid literal index");
                var source = store.Groups[2].Value.Trim();
                if (source.Equals("None", StringComparison.OrdinalIgnoreCase)) cells[slot] = (null, "explicit None clear");
                else
                {
                    if (!CanonicalClass("Game")) return Refuse("canonical Game root declaration is unproved");
                    var key = ConstantRuntimeLookup(source, instance, function, catalog, out var lookup);
                    if (key is null) return Refuse(Regex.IsMatch(source, @"(?i)\bGetAt\s*\(")
                        ? "live FormList element identity, writers and lifetime are unproved"
                        : "local array initializer is unproved: " + lookup.Problem);
                    cells[slot] = (key, lookup.Evidence);
                    stores.Add(start + ":" + slot + ":" + key + ":" + lookup.Evidence);
                }
                continue;
            }
            var call = DirectMutation.Match(line); var args = call.Success ? SplitArguments(call.Groups["args"].Value) : [];
            if (call.Success && call.Groups["m"].Value.Equals("AddForm", StringComparison.OrdinalIgnoreCase)
                && args.Length == 3 && Regex.IsMatch(args[0], @"(?i)^" + Regex.Escape(name) + @"\s*\[\s*\d+\s*\]$")
                && Regex.Matches(maskedLine, token).Count == 1)
            {
                var receiver = call.Groups["recv"].Value;
                if (!allocated || call.Groups["cast"].Success || !StableAutoProperty(instance, receiver) || HasLocalShadow(function, receiver)
                    || !AutoPropertyType(instance.Text, receiver)!.Equals("LeveledItem", StringComparison.OrdinalIgnoreCase)
                    || !instance.Objects.TryGetValue(receiver, out var target) || !catalog.Lists.ContainsKey(target)
                    || !CanonicalClass("LeveledItem", "Form") || !NativeListFrameContract(catalog, "AddForm")
                    || !short.TryParse(args[1], out var level) || level <= 0 || !short.TryParse(args[2], out var count) || count <= 0)
                    return Refuse("local array consumption has an unproved native list receiver/effect or literal arguments");
                var cellMatch = Regex.Match(args[0], @"\[\s*(\d+)\s*\]");
                if (!int.TryParse(cellMatch.Groups[1].Value, out var slot) || slot >= length)
                    return Refuse("local array consumption index is outside its allocation");
                if (effects.Count >= 64) return Refuse("local array function exceeds the 64-consumption-site analysis bound");
                // Captured VMAD targets are observations, not invariant runtime bindings.
                effects.Add(new(receiver, target.ToString(), "AddForm", args[0], args[1], args[2], start, false));
                if (start == position && Regex.Replace(args[0], @"\s", "").Equals(Regex.Replace(expression, @"\s", ""), StringComparison.OrdinalIgnoreCase))
                    valueAtRead = cells[index];
                continue;
            }
            if (Regex.IsMatch(maskedLine, token)) return Refuse("local array is copied, replaced, exported, returned or used by an unproved helper/index");
            var primitive = Regex.Match(line, @"(?i)^(Int|Float|Bool|String)\s+(\w+)(?:\s*=\s*(.+))?$");
            bool Literal(string value) => Regex.IsMatch(value, @"(?i)^(?:[+-]?\d+(?:\.\d+)?|true|false|""(?:\\.|[^""\\])*"")$");
            if (primitive.Success && primitives.Add(primitive.Groups[2].Value) && !primitive.Groups[2].Value.Equals(name, StringComparison.OrdinalIgnoreCase)
                && (!primitive.Groups[3].Success || Literal(primitive.Groups[3].Value.Trim()))) continue;
            var assignment = Regex.Match(line, @"^(\w+)\s*=\s*(.+)$");
            if (assignment.Success && primitives.Contains(assignment.Groups[1].Value) && Literal(assignment.Groups[2].Value.Trim())) continue;
            if (line.Equals("Return", StringComparison.OrdinalIgnoreCase)) continue;
            return Refuse("local array function has unproved control flow, callback/helper, scheduling or other effects");
        }
        if (valueAtRead is not { } value) return Refuse("local array expression is not the exact observed consumption site");
        var signature = "fresh-local-array-identities-v1|" + instance.Identity + "|" + instance.Evidence + "|" + instance.Text
            + "|" + instance.DiagnosticAssetEvidence + "|" + string.Join('|', stores)
            + "|" + function.Start + "|" + position + "|" + expression + "|" + value.Key + "|" + value.Evidence
            + "|" + form.Evidence + "|" + form.Text + "|" + catalog.RuntimeBaseScripts.GetValueOrDefault("Game")
            + "|" + catalog.RuntimeBaseScripts.GetValueOrDefault("LeveledItem")
            + "|" + string.Join('|', new[] { "Form", "Game", "LeveledItem" }.Select(n => n + ":" + catalog.RuntimeBaseScriptDiagnosticEvidence.GetValueOrDefault(n)))
            + "|" + string.Join('|', effects.Select(e => e.ToString()));
        return new(instance.Identity, function.Name, expression, position, length, index,
            value.Key is { } found ? [found.ToString()] : [], true, false, null,
            "fresh-local-array-identities-v1 " + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(signature))), effects.ToArray());
    }
}
