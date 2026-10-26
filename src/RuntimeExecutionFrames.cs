using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Mutagen.Bethesda.Plugins;

namespace XEnchantmentPatcher;

public static partial class PatcherEngine
{
    private static (string? Problem, string Evidence) VerifyRuntimeStageGuard(FormKey quest, IReadOnlyList<ScriptInstance> attached,
        Action<ScriptInstance> load, Catalog catalog)
    {
        if (!QuestNativeContract(catalog, "GetStageDone", out var dependencies, out var count) || count != 1)
            return ("winning Quest.GetStageDone/IsStageDone contract is unavailable", "");
        var scripts = attached.Where(i => !i.Alias).ToArray();
        if (scripts.Length > 512) return ("guard quest dispatch inventory exceeds 512 attached script instances", "");
        if (scripts.GroupBy(i => i.Name, StringComparer.OrdinalIgnoreCase).Any(g => g.Count() != 1))
            return ("guard quest has duplicate attached class dispatch", "");
        foreach (var script in scripts)
        {
            load(script);
            if (script.Text is not { Length: <= 1_000_000 } text || script.BindingMetadataProblem is not null
                || !ScriptParent(text).Equals("Quest", StringComparison.OrdinalIgnoreCase))
                return ("guard quest attached inheritance/identity is unavailable", "");
            var declarations = Regex.Matches(MaskScriptStrings(text), @"(?im)^\s*scriptname\s+(\w+)\b");
            if (declarations.Count != 1 || !declarations[0].Groups[1].Value.Equals(script.Name, StringComparison.OrdinalIgnoreCase)
                || dependencies.Any(d => HasScriptMethod(text, d))) return ("guard quest overrides the native stage predicate or has mismatched class identity", "");
        }
        var signature = quest + "|" + string.Join('|', scripts.OrderBy(i => i.Identity).Select(i => $"{i.Identity}/{i.Evidence}/{i.Text}"))
            + "|" + string.Join('|', new[] { "Quest", "Form" }.Select(n => catalog.RuntimeBaseScripts[n].Evidence + "/" + catalog.RuntimeBaseScripts[n].Text));
        return (null, "winning-native-stage-dispatch-v1 " + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(signature))));
    }

    private static bool GuaranteedRuntimePrefix(ScriptInstance instance, ScriptFunction function, int position, Catalog catalog)
    {
        var flow = Flow(instance, function);
        if (!flow.UnconditionalAt(position)) return false;
        var target = flow.At(position).Statement!;
        return flow.Nodes.Where(n => n.Start < target.Start).All(n => RuntimePrefixStatementReturns(n.Text, instance, function, catalog));
    }

    // A fixed possible AddForm footprint does not prove its execution. A prior
    // local/opaque helper may never return; only these native statements close
    // that completion obligation. The payload footprint is recorded separately.
    private static bool RuntimePrefixStatementReturns(string raw, ScriptInstance instance, ScriptFunction function, Catalog catalog)
    {
        var line = MaskScriptStrings(raw);
        if (!Regex.IsMatch(line, @"\b\w+\s*\("))
        {
            var declaration = Regex.Match(raw, @"(?i)^\s*(Int|Float|Bool|String)\s+\w+(?:\s*=\s*(.+))?\s*$");
            if (declaration.Success) return !declaration.Groups[2].Success || PurePrimitivePrefixValue(declaration.Groups[2].Value.Trim(), instance, function);
            var assignment = Regex.Match(raw, @"(?i)^\s*(\w+)\s*=\s*(.+)\s*$");
            if (assignment.Success) return PurePrimitivePrefixValue(assignment.Groups[1].Value, instance, function)
                && (!PlainPrimitiveAutoMember(instance.Text!, assignment.Groups[1].Value, out var readOnly) || !readOnly)
                && PurePrimitivePrefixValue(assignment.Groups[2].Value.Trim(), instance, function);
            var cast = Regex.Match(line, @"(?i)^\s*(\w+)\s+\w+\s*=\s*(\w+)\s+as\s+(\w+)\s*$");
            return cast.Success && cast.Groups[1].Value.Equals(cast.Groups[3].Value, StringComparison.OrdinalIgnoreCase)
                && (cast.Groups[2].Value.Equals("self", StringComparison.OrdinalIgnoreCase)
                    || Regex.IsMatch(function.Body, @"(?im)^\s*\w+\s+" + Regex.Escape(cast.Groups[2].Value) + @"\s*="));
        }
        var mutation = DirectMutation.Match(line);
        if (mutation.Success && mutation.Groups["m"].Value.Equals("AddForm", StringComparison.OrdinalIgnoreCase)
            && instance.Objects.TryGetValue(mutation.Groups["recv"].Value, out var target) && catalog.Lists.ContainsKey(target)
            && StableAutoProperty(instance, mutation.Groups["recv"].Value) && NativeListFrameContract(catalog, "AddForm"))
        {
            var args = SplitArguments(mutation.Groups["args"].Value);
            var payload = args.Length == 3 ? Regex.Replace(args[0], @"(?i)\s+as\s+form\s*$", "").Trim() : "";
            return args.Length == 3 && instance.Objects.ContainsKey(payload) && StableAutoProperty(instance, payload)
                && short.TryParse(args[1], out var level) && level > 0 && short.TryParse(args[2], out var count) && count > 0;
        }
        var log = Regex.Match(raw, @"(?i)^\s*Debug\s*\.\s*(Trace|TraceStack)\s*\(\s*""(?:\\.|[^""\\])*""\s*,\s*[0-9]+\s*\)\s*$");
        if (log.Success && !SymbolShadowed(instance, function, "Debug")
            && NativeFrameContract(catalog, "Debug", log.Groups[1].Value, "", ["String", "Int"], true)) return true;
        var update = Regex.Match(line, @"(?i)^\s*(?:self\s*\.\s*)?(\w+)\s*\(([^()]*)\)\s*$");
        return update.Success && SplitArguments(update.Groups[2].Value).All(a => PurePrimitivePrefixValue(a, instance, function))
            && NativeUpdateFrameCall(update.Groups[1].Value, SplitArguments(update.Groups[2].Value), instance,
            function, instance.Quest.FormKey, [instance], catalog, instance.Alias);
    }

    private static bool PurePrimitivePrefixValue(string value, ScriptInstance instance, ScriptFunction function)
    {
        if (Regex.IsMatch(value, @"(?i)^\s*(?:true|false|[+-]?\d+(?:\.\d+)?(?:\s+as\s+(?:Int|Float))?|""(?:\\.|[^""\\])*"")\s*$")) return true;
        if (!Regex.IsMatch(value, @"^\w+$")) return false;
        var name = Regex.Escape(value);
        return Regex.IsMatch(function.Body, @"(?im)^\s*(?:Int|Float|Bool|String)\s+" + name + @"\s*(?:=|$)")
            || Regex.IsMatch(function.Parameters, @"(?i)(?:^|,)\s*(?:Int|Float|Bool|String)\s+" + name + @"\b")
            || PlainPrimitiveAutoMember(instance.Text!, value, out _);
    }
}
