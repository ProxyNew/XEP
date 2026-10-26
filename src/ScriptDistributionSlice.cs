using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;

namespace XEnchantmentPatcher;

// Follow script distribution proves only its distribution slice (ADR 0009), bounded
// to the source mod (ADR 0008): scripts attached to records that mod defines or wins,
// with their inheritance chains. Other mods' scripts are not part of the proof. Only
// mods being patched are analysed, each within its own scope, and only their changes
// to a shared list are combined with the source's. The shared frame owners take a
// RuntimeSliceScope; without one they keep whole-frame proofs.
public static partial class PatcherEngine
{
    private sealed class RuntimeSliceScope(ModKey source)
    {
        public ModKey Source { get; } = source;
        public List<string> Warnings { get; } = [];
    }
    // The script whose unproved operation can reach a list. Property names a VMAD
    // binding whose slice proof decides the taint; Receiver marks a list-receiver proof.
    // Flow marks a taint the slice flow re-proves from the operation itself.
    private sealed record RuntimeListTaint(string Instance, string Reason, string? Property, bool Receiver, bool Flow = false);

    private static ModKey SliceSourceMod(ScriptInstance instance) => instance.Quest.FormKey.ModKey;
    private static bool InSliceScope(FormKey record, ModKey source, Catalog catalog) => record.ModKey == source
        || RuntimeRecordWinners(catalog).TryGetValue(record, out var winner) && winner.Plugin == source;

    // Records whose handle can reach a slice value in this mod's analysis: a leveled
    // list, a FormList that carries one (authored or added by the mod's own scripts),
    // or one of the mod's own quests whose scripts bind them. Other mods' quests are
    // never slice handles here; their own analysis covers them (ADR 0008).
    private static bool SliceRecord(FormKey key, ModKey mod, Catalog catalog)
    {
        if (key.IsNull) return false;
        // Concurrent table: read without the slice lock; equal values race, the first is kept.
        if (catalog.RuntimeSliceRecords.TryGetValue((key, mod), out var known)) return known;
        return catalog.RuntimeSliceRecords.GetOrAdd((key, mod), SliceRecordCore(key, mod, catalog));
    }

    private static bool SliceRecordCore(FormKey key, ModKey mod, Catalog catalog)
    {
        if (catalog.Lists.ContainsKey(key)) return true;
        if (SliceQuests(mod, catalog).Contains(key)) return true;
        return SliceCarriesList(key, mod, catalog);
    }

    private static bool SliceCarriesList(FormKey key, ModKey mod, Catalog catalog)
    {
        lock (catalog.SliceLock)
        {
            catalog.RuntimeFormLists ??= catalog.Mods.SelectMany(m => m.FormLists).GroupBy(l => l.FormKey).ToDictionary(g => g.Key, g => g.Last());
            var pending = new Queue<(FormKey Key, int Depth)>(); pending.Enqueue((key, 0)); var seen = new HashSet<FormKey>();
            while (pending.TryDequeue(out var current))
            {
                if (catalog.Lists.ContainsKey(current.Key) || current.Depth > 16 || seen.Count >= 8192) return true;
                if (!seen.Add(current.Key) || !catalog.RuntimeFormLists.TryGetValue(current.Key, out var collection)) continue;
                if (SliceFormListCarriers(mod, catalog).Values.Any(c => c.Contains(current.Key))) return true;
                foreach (var item in collection.Items) pending.Enqueue((item.FormKey, current.Depth + 1));
            }
            return false;
        }
    }

    // The mod's quests through which a slice list's bound property can be read or
    // rebound: their scripts bind a list, a FormList carrying one, or another such quest.
    private static HashSet<FormKey> SliceQuests(ModKey mod, Catalog catalog)
    {
        lock (catalog.SliceLock)
        {
            if (catalog.RuntimeSliceQuests.TryGetValue(mod, out var built)) return built;
            catalog.RuntimeFormLists ??= catalog.Mods.SelectMany(m => m.FormLists).GroupBy(l => l.FormKey).ToDictionary(g => g.Key, g => g.Last());
            var bound = SliceScopeInstances(mod, catalog).GroupBy(i => i.Quest.FormKey)
                .ToDictionary(g => g.Key, g => g.SelectMany(i => i.Objects.Values.Concat(i.Arrays.Values.SelectMany(k => k))).Distinct().ToArray());
            var result = bound.Where(p => p.Value.Any(k => catalog.Lists.ContainsKey(k) || catalog.RuntimeFormLists.ContainsKey(k) && SliceCarriesList(k, mod, catalog)))
                .Select(p => p.Key).ToHashSet();
            for (var changed = true; changed;)
            {
                changed = false;
                foreach (var (quest, keys) in bound)
                    if (!result.Contains(quest) && keys.Any(result.Contains)) changed = result.Add(quest) | changed;
            }
            return catalog.RuntimeSliceQuests[mod] = result;
        }
    }

    // Every quest and alias script of the mod's scope, loaded, not only those the list
    // analysis touched: any of them may obtain a slice value or write the guard.
    private static IReadOnlyList<ScriptInstance> SliceScopeInstances(ModKey mod, Catalog catalog) =>
        catalog.RuntimeScopeInstances?.Invoke(mod) ?? catalog.RuntimeScriptInstances.Values.Where(i => InSliceScope(i.Quest.FormKey, mod, catalog)).ToArray();

    private static ScriptInstance? SliceInstance(string identity, Catalog catalog) =>
        catalog.RuntimeScriptInstances.GetValueOrDefault(identity) ?? catalog.RuntimeSliceInstances.GetValueOrDefault(identity);

    // The patched mods whose analyses are combined with this source mod's (ADR 0008):
    // the source itself, plus every mod being patched.
    private static IEnumerable<ModKey> SliceCombinedMods(ModKey source, Catalog catalog) =>
        new[] { source }.Concat((IEnumerable<ModKey>?)catalog.PatchedMods?.OrderBy(m => m.FileName.String, StringComparer.OrdinalIgnoreCase) ?? []).Distinct();

    // Whether the record belongs to a mod whose own analysis is combined with this source's.
    private static bool SliceCombined(FormKey record, ModKey source, Catalog catalog) =>
        SliceCombinedMods(source, catalog).Any(mod => InSliceScope(record, mod, catalog));

    private static bool SlicePrimitive(string? type) => type is not null
        && new[] { "Int", "Float", "Bool", "String" }.Contains(type.TrimEnd('[', ']'), StringComparer.OrdinalIgnoreCase);

    // `.Name(args)` at the start of masked text, with balanced parentheses.
    private static (string Method, string Args, int Length)? BalancedMemberCall(string text)
    {
        var head = Regex.Match(text, @"^\s*\.\s*(\w+)\s*\(");
        if (!head.Success) return null;
        var depth = 1;
        for (var i = head.Length; i < text.Length; i++)
        {
            if (text[i] == '(') depth++;
            else if (text[i] == ')' && --depth == 0) return (head.Groups[1].Value, text[head.Length..i], i + 1);
        }
        return null;
    }

    // E2: an attached script whose parents reach Quest (or ReferenceAlias) through
    // custom scripts is analysed as one body. Parents' headers are blanked so the
    // attached script's declaration stays the only one; dispatch stays in the chain.
    private static (ScriptInstance? View, string? Problem) SliceChainView(ScriptInstance caller, Catalog catalog)
    {
        lock (catalog.SliceLock)
        {
            var canonical = caller.Alias ? "ReferenceAlias" : "Quest";
            if (caller.Text is null || ScriptParent(caller.Text).Equals(canonical, StringComparison.OrdinalIgnoreCase)) return (caller, null);
            if (catalog.RuntimeSliceChainViews.TryGetValue(caller.Identity, out var cached)) return cached;
            var names = new List<string> { caller.Name }; var texts = new List<string> { caller.Text }; var evidence = new List<string>();
            var parent = ScriptParent(caller.Text); string? problem = null;
            while (problem is null && !parent.Equals(canonical, StringComparison.OrdinalIgnoreCase))
            {
                if (parent.Length == 0) problem = "custom inheritance does not reach " + canonical;
                else if (names.Contains(parent, StringComparer.OrdinalIgnoreCase)) problem = "custom inheritance is cyclic at " + parent;
                else if (names.Count > 16) problem = "custom inheritance exceeds 16 scripts";
                else
                {
                    var asset = catalog.RuntimeHolderAssetResolver?.Invoke(parent);
                    var text = asset?.Text;
                    if (text is null || text.Length > 1_000_000 || Regex.Matches(MaskScriptStrings(text), @"(?im)^\s*scriptname\b").Count != 1
                        || !Regex.IsMatch(MaskScriptStrings(text), @"(?im)^\s*scriptname\s+" + Regex.Escape(parent) + @"\b"))
                        problem = $"custom parent {parent} has no exact winning script asset";
                    else { names.Add(parent); texts.Add(text); evidence.Add(asset!.Evidence); parent = ScriptParent(text); }
                }
            }
            if (problem is not null) return catalog.RuntimeSliceChainViews[caller.Identity] = (null, problem);
            var combined = texts[0] + string.Concat(texts.Skip(1).Select(t => "\n" + Regex.Replace(t, @"(?im)^[^\S\n]*scriptname\b[^\n]*", m => new string(' ', m.Length))));
            if (combined.Length > 1_000_000) return catalog.RuntimeSliceChainViews[caller.Identity] = (null, "custom inheritance chain exceeds the text bound");
            var view = new ScriptInstance { Quest = caller.Quest, Name = caller.Name, Alias = caller.Alias, ScriptSlot = caller.ScriptSlot,
                AliasSlot = caller.AliasSlot, AliasIdentity = caller.AliasIdentity, BindingEntry = caller.BindingEntry,
                WinningQuestPlugin = caller.WinningQuestPlugin, BindingMetadataProblem = caller.BindingMetadataProblem, Chain = names.ToArray(),
                Text = combined, Evidence = caller.Evidence + "; custom chain " + string.Join(" > ", names) + "; " + string.Join("; ", evidence),
                DiagnosticAssetEvidence = caller.DiagnosticAssetEvidence };
            view.VmadHandles.AddRange(caller.VmadHandles);
            foreach (var (k, v) in caller.Objects) view.Objects[k] = v;
            foreach (var (k, v) in caller.Arrays) view.Arrays[k] = v;
            foreach (var (k, v) in caller.ArrayBindings) view.ArrayBindings[k] = v;
            foreach (var (k, v) in caller.AliasBindings) view.AliasBindings[k] = v;
            view.Functions.AddRange(ParseFunctions(combined));
            if (view.Functions.Count > 512) return catalog.RuntimeSliceChainViews[caller.Identity] = (null, "custom inheritance chain exceeds 512 functions");
            return catalog.RuntimeSliceChainViews[caller.Identity] = (view, null);
        }
    }

    private static Dictionary<string, FormKey[]> SliceScriptAttachments(Catalog catalog)
    {
        lock (catalog.SliceLock)
        {
            if (catalog.RuntimeSliceScriptAttachments is { } built) return built;
            var result = new Dictionary<string, HashSet<FormKey>>(StringComparer.OrdinalIgnoreCase);
            foreach (var (record, _) in RuntimeRecordWinners(catalog).Values)
            {
                if (record.IsDeleted || record is not IHaveVirtualMachineAdapterGetter { VirtualMachineAdapter: { } adapter }) continue;
                var names = adapter.Scripts.Select(s => s.Name);
                if (adapter is IQuestAdapterGetter quest) names = names.Concat(quest.Aliases.SelectMany(a => a.Scripts.Select(s => s.Name)));
                foreach (var name in names)
                {
                    if (!result.TryGetValue(name, out var rows)) result[name] = rows = [];
                    rows.Add(record.FormKey);
                }
            }
            return catalog.RuntimeSliceScriptAttachments = result.ToDictionary(p => p.Key, p => p.Value.ToArray(), StringComparer.OrdinalIgnoreCase);
        }
    }

    // E3: L added at runtime to FormList C by the mod's own scripts. Lists reached
    // through C as if C held them.
    private static Dictionary<FormKey, FormKey[]> SliceFormListCarriers(ModKey mod, Catalog catalog)
    {
        if (catalog.RuntimeSliceFormListCarriers.TryGetValue(mod, out var built)) return built;
        var result = new Dictionary<FormKey, HashSet<FormKey>>();
        lock (catalog.SliceLock) catalog.RuntimeFormLists ??= catalog.Mods.SelectMany(m => m.FormLists).GroupBy(l => l.FormKey).ToDictionary(g => g.Key, g => g.Last());
        foreach (var instance in SliceScopeInstances(mod, catalog).Where(i => i.Text is not null))
            foreach (var line in MaskScriptStrings(instance.Text!).Split('\n'))
            {
                var add = Regex.Match(line, @"(?i)^\s*(\w+)\s*\.\s*AddForm\s*\(\s*(\w+)(?:\s+as\s+form)?\s*\)\s*$");
                if (!add.Success || !instance.Objects.TryGetValue(add.Groups[1].Value, out var carrier) || !catalog.RuntimeFormLists.ContainsKey(carrier)
                    || !instance.Objects.TryGetValue(add.Groups[2].Value, out var list) || !catalog.Lists.ContainsKey(list)) continue;
                if (!result.TryGetValue(list, out var rows)) result[list] = rows = [];
                rows.Add(carrier);
            }
        return catalog.RuntimeSliceFormListCarriers.GetOrAdd(mod, result.ToDictionary(p => p.Key, p => p.Value.Order().ToArray()));
    }

    // E3 rule 1: a carrier is a stable plain Auto FormList property bound to a FormList
    // the source mod defines or wins. Its uses are checked by the slice flow.
    private static string? SliceCarrierProblem(FormKey carrier, ModKey source, Catalog catalog)
    {
        if (catalog.RuntimeSliceCarrierProblems.TryGetValue((carrier, source), out var cached)) return cached;
        string? Check()
        {
            if (!RuntimeRecordWinners(catalog).TryGetValue(carrier, out var winner) || winner.Record.IsDeleted || winner.Record is not IFormListGetter)
                return $"carrier {carrier} is not a winning FormList";
            if (!InSliceScope(carrier, source, catalog)) return $"carrier {carrier} is outside the source mod";
            foreach (var instance in SliceScopeInstances(source, catalog))
                foreach (var property in instance.Objects.Where(p => p.Value == carrier).Select(p => p.Key))
                    if (instance.Text is null || !StableAutoProperty(instance, property)
                        || !AutoPropertyType(instance.Text, property)!.Equals("FormList", StringComparison.OrdinalIgnoreCase))
                        return $"carrier {instance.Label}/{property} is not a stable plain Auto FormList property";
            return null;
        }
        return catalog.RuntimeSliceCarrierProblems[(carrier, source)] = Check();
    }

    // GetFormFromFile(<literal>, "<plugin>"): the loaded record it can return, or
    // null when it returns None. Native local IDs never reach another plugin's records.
    private static FormKey? SliceLiteralLookup(string id, string plugin, Catalog catalog)
    {
        // The engine keeps only the local bits, so high bits never make the result None.
        if (!(id.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? uint.TryParse(id[2..], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var value)
            : uint.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out value))) return null;
        var mod = catalog.Mods.FirstOrDefault(m => m.ModKey.FileName.String.Equals(plugin, StringComparison.OrdinalIgnoreCase));
        if (mod is null) return null;
        var width = mod.ModHeader.Flags.HasFlag(SkyrimModHeader.HeaderFlag.Small) ? 0xFFFu : 0xFFFFFFu;
        if ((value & width) == 0) return null;
        var key = new FormKey(mod.ModKey, value & width);
        return RuntimeRecordWinners(catalog).ContainsKey(key) ? key : null;
    }

    // GetForm(<literal>): a runtime FormID. Load-order index 00 is always the first
    // master, Skyrim.esm; any other index depends on the load order and stays dynamic.
    // Null when the literal names no loaded record (the call returns None).
    private static FormKey? SliceLiteralForm(string id, Catalog catalog, out bool dynamic)
    {
        dynamic = true;
        if (!(id.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? uint.TryParse(id[2..], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var value)
            : uint.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out value)) || value >> 24 != 0) return null;
        if (catalog.Mods.FirstOrDefault()?.ModKey is not { } first || !first.FileName.String.Equals("Skyrim.esm", StringComparison.OrdinalIgnoreCase)) return null;
        dynamic = false;
        var key = new FormKey(first, value);
        return RuntimeRecordWinners(catalog).ContainsKey(key) ? key : null;
    }

    private static string SliceEvidence(IEnumerable<string> parts) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('|', parts.Order(StringComparer.Ordinal)))));
}
