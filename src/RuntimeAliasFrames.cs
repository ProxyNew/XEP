using System.Text.RegularExpressions;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;

namespace XEnchantmentPatcher;

public static partial class PatcherEngine
{
    private readonly record struct RuntimeQuestView(string Type, FormKey Quest);

    private static bool PlainPrimitiveAutoMember(string text, string name, out bool readOnly)
    {
        readOnly = false;
        var declarations = Regex.Matches(TextSyntax(text).Masked, @"(?im)^\s*\w+(?:\[\])?\s+property\s+" + Regex.Escape(name) + @"\b[^\r\n]*$");
        if (declarations.Count != 1) return false;
        var raw = text.Substring(declarations[0].Index, declarations[0].Length);
        var match = Regex.Match(raw, @"(?i)^\s*(int|float|bool|string)\s+property\s+" + Regex.Escape(name)
            + @"(?:\s*=\s*(?:[+-]?\d+(?:\.\d+)?|true|false|""(?:\\.|[^""\\])*""))?\s+auto(?<readonly>readonly)?(?:\s+hidden)?\s*$");
        readOnly = match.Groups["readonly"].Success;
        return match.Success;
    }

    private static bool ValidRuntimeAlias((FormKey Quest, short Alias)? identity, ScriptInstance[] frame)
    {
        if (identity is not { Alias: >= 0 } handle) return false;
        var quest = frame.FirstOrDefault(i => i.Quest.FormKey == handle.Quest)?.Quest;
        if (quest is null) return false;
        var aliases = quest.Aliases.Where(a => a.ID == handle.Alias).ToArray();
        return aliases.Length == 1 && aliases[0].Type == QuestAlias.TypeEnum.Reference;
    }
    private static bool StableRuntimeAliasArray(ScriptInstance instance, string name) => instance.Text is { } text
        && AutoPropertyType(text, name)?.Equals("ReferenceAlias[]", StringComparison.OrdinalIgnoreCase) == true
        && !Regex.IsMatch(TextSyntax(text).Masked, @"(?i)\b" + Regex.Escape(name) + @"\s*(?:[+\-*/]?=|\[[^\]\r\n]*\]\s*[+\-*/]?=)")
        && !instance.Functions.Any(f => HasLocalShadow(f, name));

    // Narrow non-escaping reference-alias operations. The public alias/array
    // exposure is still closed by the incoming frame; this is only a use proof.
    private static bool NativeAliasFrameUse(string line, ScriptInstance caller, ScriptInstance[] frame, Catalog catalog)
    {
        const string receiver = @"\w+(?:\s*\[\s*(?:\w+|[0-9]+)\s*\])?";
        bool Index(string name, string index, int length)
        {
            if (int.TryParse(index, out var literal)) return literal >= 0 && literal < length;
            var functions = caller.Functions.Where(f => TextSyntax(f.Body).Masked.Split('\n').Any(l => l.Trim().Equals(line.Trim(), StringComparison.Ordinal))).ToArray();
            return functions.Length > 0 && functions.All(f => DeclaredType(caller.Text!, f, index)?.Equals("Int", StringComparison.OrdinalIgnoreCase) == true)
                && !Regex.IsMatch(TextSyntax(caller.Text!).Masked, @"(?im)^\s*int\[\]\s+(?:property\s+)?" + Regex.Escape(index) + @"\b");
        }
        RuntimeObjectHandle[]? Resolve(string expression)
        {
            var parsed = Regex.Match(expression, @"(?i)^\s*(?<name>\w+)(?:\s*\[\s*(?<index>\w+|[0-9]+)\s*\])?\s*$");
            if (!parsed.Success) return null;
            var name = parsed.Groups["name"].Value;
            if (name.Equals("self", StringComparison.OrdinalIgnoreCase))
                return !parsed.Groups["index"].Success && caller.Alias && caller.AliasIdentity is { } self
                    && self.Quest == caller.Quest.FormKey && ValidRuntimeAlias(self, frame) ? [new(self.Quest, self.Alias)] : null;
            if (parsed.Groups["index"].Success)
            {
                if (!StableRuntimeAliasArray(caller, name) || !caller.ArrayBindings.TryGetValue(name, out var array) || array.Length == 0
                    || !AutoPropertyType(caller.Text!, name)!.Equals("ReferenceAlias[]", StringComparison.OrdinalIgnoreCase)
                    || array.Any(h => !ValidRuntimeAlias((h.Record, h.Alias), frame))
                    || !Index(name, parsed.Groups["index"].Value, array.Length)) return null;
                return array;
            }
            return StableAutoProperty(caller, name) && caller.AliasBindings.TryGetValue(name, out var alias)
                && AutoPropertyType(caller.Text!, name)!.Equals("ReferenceAlias", StringComparison.OrdinalIgnoreCase)
                && ValidRuntimeAlias(alias, frame) ? [new(alias.Quest, alias.Alias)] : null;
        }
        bool Contract(RuntimeObjectHandle[] handles, string method)
        {
            var dependencies = method.Equals("GetRef", StringComparison.OrdinalIgnoreCase) ? new[] { "GetRef", "GetReference" } : new[] { method };
            if (frame.Where(i => i.Alias && i.AliasIdentity is { } a && handles.Any(h => h.Record == a.Quest && h.Alias == a.Alias))
                .Any(i => dependencies.Any(d => HasScriptMethod(i.Text!, d)))) return false;
            if (method.Equals("GetRef", StringComparison.OrdinalIgnoreCase))
            {
                if (!NativeFrameContract(catalog, "ReferenceAlias", "GetReference", "ObjectReference", [], false)) return false;
                var wrappers = ParseFunctions(catalog.RuntimeBaseScripts.GetValueOrDefault("ReferenceAlias").Text ?? "")
                    .Where(f => f.Name.Equals("GetRef", StringComparison.OrdinalIgnoreCase)).ToArray();
                return wrappers.Length == 1 && wrappers[0].ReturnType.Equals("ObjectReference", StringComparison.OrdinalIgnoreCase)
                    && wrappers[0].Parameters.Trim().Length == 0
                    && Regex.IsMatch(wrappers[0].Body, @"(?i)^\s*return\s+self\s*\.\s*GetReference\s*\(\s*\)\s*$");
            }
            return method.ToLowerInvariant() switch
            {
                "getreference" => NativeFrameContract(catalog, "ReferenceAlias", "GetReference", "ObjectReference", [], false),
                "clear" => NativeFrameContract(catalog, "ReferenceAlias", "Clear", "", [], false),
                "forcerefto" => NativeFrameContract(catalog, "ReferenceAlias", "ForceRefTo", "", ["ObjectReference"], false),
                _ => false
            };
        }
        bool Getter(string expression)
        {
            var match = Regex.Match(expression, @"(?i)^\s*(?<receiver>" + receiver + @")\s*\.\s*(?<method>GetRef|GetReference)\s*\(\s*\)\s*$");
            return match.Success && Resolve(match.Groups["receiver"].Value) is { } handles && Contract(handles, match.Groups["method"].Value);
        }
        var clear = Regex.Match(line, @"(?i)^\s*(?<receiver>" + receiver + @")\s*\.\s*Clear\s*\(\s*\)\s*$");
        if (clear.Success) return Resolve(clear.Groups["receiver"].Value) is { } handles && Contract(handles, "Clear");
        var condition = Regex.Match(line, @"(?i)^\s*(?:if|elseif)\s+(.+?)\s*$");
        if (condition.Success && Getter(condition.Groups[1].Value)) return true;
        var force = Regex.Match(line, @"(?i)^\s*(?<receiver>" + receiver + @")\s*\.\s*ForceRefTo\s*\((?<argument>.*)\)\s*$");
        return force.Success && Resolve(force.Groups["receiver"].Value) is { } target && Contract(target, "ForceRefTo")
            && (force.Groups["argument"].Value.Trim().Equals("None", StringComparison.OrdinalIgnoreCase) || Getter(force.Groups["argument"].Value));
    }
}
