using System.Globalization;
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
    private sealed record RuntimePayloadExpressionDiagnostic(string OwnerIdentity, string Function,
        string Expression, string Kind, string[] PossibleIdentities, bool MayBeNone, string? Problem, string Evidence);

    private static Dictionary<FormKey, (ISkyrimMajorRecordGetter Record, ModKey Plugin)> RuntimeRecordWinners(Catalog catalog)
    {
        if (catalog.RuntimeWinningRecords is { } old) return old;
        using var timing = PhaseTiming.Measure("availability.record-winners");
        return catalog.RuntimeWinningRecords = LoadOrderWinners(catalog.Mods);
    }

    // One load-order winner index, shared by SkyPatcher input resolution and
    // availability analysis of the same loaded plugins.
    internal static Dictionary<FormKey, (ISkyrimMajorRecordGetter Record, ModKey Plugin)> LoadOrderWinners(IReadOnlyList<ISkyrimModGetter> loaded)
    {
        var result = new Dictionary<FormKey, (ISkyrimMajorRecordGetter, ModKey)>();
        var width = Math.Min(8, Math.Max(1, Environment.ProcessorCount));
        var options = new ParallelOptions { MaxDegreeOfParallelism = width };
        for (var start = 0; start < loaded.Count; start += width)
        {
            var mods = loaded.Skip(start).Take(width).ToArray();
            var records = new (FormKey Key, ISkyrimMajorRecordGetter Record)[mods.Length][];
            void Read(int i) => records[i] = mods[i].EnumerateMajorRecords().OfType<ISkyrimMajorRecordGetter>()
                .Select(record => (record.FormKey, record)).ToArray();
            // A worker owns one plugin's inventory. Bound temporary storage and
            // join every reader before merging or propagating an exception.
            if (mods.Length == 1 || mods.Distinct(ReferenceEqualityComparer.Instance).Count() != mods.Length)
                for (var i = 0; i < mods.Length; i++) Read(i);
            else Parallel.For(0, mods.Length, options, Read);
            for (var i = 0; i < mods.Length; i++)
                foreach (var record in records[i]) result[record.Key] = (record.Record, mods[i].ModKey);
        }
        // A later deletion or VMAD removal must remain the winner.
        return result;
    }

    // This contract models the canonical native local-ID lookup as returning
    // this record or None. It does not prove success, execution or persistence,
    // and deliberately does not infer high-byte/packed-ID normalization.
    private static FormKey? ConstantRuntimeLookup(string expression, ScriptInstance instance,
        ScriptFunction? function, Catalog catalog, out RuntimePropertyBinding binding)
    {
        binding = new("payload expression is outside the constant native lookup contract", true);
        var call = Regex.Match(expression, @"(?i)^Game\s*\.\s*GetFormFromFile\s*\((?<args>.*)\)$");
        if (!call.Success) return null;
        if (function is null || RuntimeLookupGameShadowed(instance, function))
            return RefuseRuntimeLookup("Game lookup dispatch is shadowed or has no bounded caller function", out binding);
        var inheritedScope = RuntimeLookupInheritedGameScope(instance, catalog, out var scopeEvidence);
        if (inheritedScope is not null) return RefuseRuntimeLookup(inheritedScope, out binding);
        if (!NativeFrameContract(catalog, "Game", "GetFormFromFile", "Form", ["Int", "String"], true)
            || !ScriptParent(catalog.RuntimeBaseScripts.GetValueOrDefault("Game").Text ?? "").Equals("", StringComparison.Ordinal))
            return RefuseRuntimeLookup("canonical global native Game.GetFormFromFile contract is unverified", out binding);
        var args = SplitArguments(call.Groups["args"].Value);
        if (args.Length != 2 || !Regex.IsMatch(args[0], @"^\+?\d+$")
            || !int.TryParse(args[0], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var id))
            return RefuseRuntimeLookup("lookup ID must be a nonnegative already-local Int literal", out binding);
        var filename = Regex.Match(args[1], "^\"(?<name>[^\"\\\\]+)\"$");
        if (!filename.Success) return RefuseRuntimeLookup("lookup filename must be an unescaped literal", out binding);
        // Exact loaded filename comparison; no trimming, paths or fuzzy match.
        var matches = catalog.Mods.Where(m => m.ModKey.FileName.String.Equals(filename.Groups["name"].Value, StringComparison.Ordinal)).ToArray();
        if (matches.Length != 1) return RefuseRuntimeLookup("lookup filename does not exactly identify one loaded plugin", out binding);
        var mod = matches[0];
        var maximum = mod.ModHeader.Flags.HasFlag(SkyrimModHeader.HeaderFlag.Small) ? 0xFFF : 0xFFFFFF;
        if (id == 0 || id > maximum) return RefuseRuntimeLookup("lookup ID is null, packed or outside the loaded plugin's local-ID width", out binding);
        var key = new FormKey(mod.ModKey, (uint)id);
        if (!RuntimeRecordWinners(catalog).TryGetValue(key, out var winner) || winner.Record.IsDeleted)
            return RefuseRuntimeLookup("lookup has no nondeleted winning record with this originating FormKey", out binding);
        binding = new(null, false) { Evidence = $"native-local-id-possible-form-or-none-v1 {key}; {catalog.RuntimeBaseScripts["Game"].Evidence}; winning record plugin {winner.Plugin}; {scopeEvidence}" };
        return key;
    }
    private static FormKey? RefuseRuntimeLookup(string problem, out RuntimePropertyBinding binding)
    {
        binding = new(problem, true);
        return null;
    }
    private static bool RuntimeLookupGameShadowed(ScriptInstance instance, ScriptFunction function)
    {
        if (instance.Objects.ContainsKey("Game") || instance.Arrays.ContainsKey("Game") || HasParameter(function, "Game")) return true;
        // A control-flow expression such as `if Game.GetFormFromFile(...)` or
        // `return Game.GetFormFromFile(...)` is not a variable declaration.
        // Other functions' locals/parameters do not shadow this caller.
        const string declaration = @"(?im)^\s*\w+(?:\[\])?\s+(?:property\s+)?Game\b(?=\s*(?:=|$|auto(?:readonly)?\b|hidden\b))";
        if (Regex.IsMatch(MaskScriptStrings(function.Body), declaration)) return true;
        var outside = MaskScriptStrings(instance.Text!).ToCharArray();
        foreach (var member in instance.Functions) Array.Fill(outside, ' ', member.Start, member.End - member.Start);
        return Regex.IsMatch(new string(outside), declaration);
    }

    private static string? RuntimeLookupInheritedGameScope(ScriptInstance instance, Catalog catalog, out string evidence)
    {
        evidence = "";
        var chain = ScriptParent(instance.Text ?? "").ToLowerInvariant() switch
        {
            "quest" => new[] { (Name: "Quest", Parent: "Form"), (Name: "Form", Parent: "") },
            "referencealias" => new[] { (Name: "ReferenceAlias", Parent: "Alias"), (Name: "Alias", Parent: "") },
            _ => []
        };
        if (chain.Length == 0) return "Game lookup inherited name scope has an unsupported caller parent";
        var inputs = new List<string>();
        foreach (var (name, parent) in chain)
        {
            var source = catalog.RuntimeBaseScripts.GetValueOrDefault(name);
            if (source.Text is not { Length: <= 1_000_000 } text
                || Regex.Matches(MaskScriptStrings(text), @"(?im)^\s*scriptname\b").Count != 1
                || !ScriptParent(text).Equals(parent, StringComparison.OrdinalIgnoreCase)
                || !Regex.IsMatch(text, @"(?im)^[ \t]*scriptname[ \t]+" + Regex.Escape(name)
                    + (parent.Length == 0 ? "" : @"[ \t]+extends[ \t]+" + Regex.Escape(parent)) + @"(?:[ \t]+hidden)?[ \t]*\r?$"))
                return "Game lookup inherited name scope is unproved for " + name;
            var outside = MaskScriptStrings(text).ToCharArray();
            foreach (var member in ParseFunctions(text)) Array.Fill(outside, ' ', member.Start, member.End - member.Start);
            if (HasScriptMethod(text, "Game") || Regex.IsMatch(new string(outside),
                @"(?im)^[ \t]*\w+(?:\[\])?[ \t]+(?:property[ \t]+)?Game\b(?=[ \t]*(?:=|\r?$|auto(?:readonly)?\b|hidden\b))"))
                return "Game lookup is shadowed by an inherited " + name + " member";
            inputs.Add(name + "/" + parent + "/" + source.Evidence + "/" + text + "/"
                + catalog.RuntimeBaseScriptDiagnosticEvidence.GetValueOrDefault(name));
        }
        evidence = "native-lookup-inherited-name-scope-v1 " + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('|', inputs))));
        return null;
    }
}
