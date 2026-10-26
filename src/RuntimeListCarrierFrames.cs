using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;

namespace XEnchantmentPatcher;

public static partial class PatcherEngine
{
    private sealed record RuntimeListCarrier(FormKey? Parent, string Evidence, string? Instance = null);

    // Known possible handle exports are collected before binding/unlock proof.
    // Carrier obligations never establish execution or availability.
    private static void CollectRuntimeListCarriers(Catalog catalog)
    {
        if (catalog.RuntimePossibleListCarriers is not null) return;
        var result = new Dictionary<FormKey, List<RuntimeListCarrier>>();
        foreach (var instance in catalog.RuntimeScriptInstances.Values.ToArray())
        {
            if (instance.Text is null) continue;
            var offset = 0;
            foreach (var raw in instance.Text.Split('\n'))
            {
                var position = offset; offset += raw.Length + 1;
                var call = MutationCall.Match(MaskScriptStrings(raw));
                if (!call.Success || !call.Groups[1].Value.Equals("AddForm", StringComparison.OrdinalIgnoreCase)) continue;
                var direct = DirectMutation.Match(raw);
                if (direct.Success && UnfilledRuntimeListReceiver(instance, direct.Groups["recv"].Value)) continue;
                var last = raw.LastIndexOf(')');
                var args = direct.Success ? SplitArguments(direct.Groups["args"].Value)
                    : last >= call.Index + call.Length ? SplitArguments(raw[(call.Index + call.Length)..last]) : [];
                if (args.Length != 3) continue;
                var value = System.Text.RegularExpressions.Regex.Replace(args[0], @"(?i)\s+as\s+form\s*$", "").Trim();
                var function = instance.Functions.FirstOrDefault(f => position >= f.BodyStart && position < f.End);
                FormKey? payload = null; var dependency = "captured VMAD property " + value;
                if (instance.Objects.TryGetValue(value, out var property)) payload = property;
                else if (System.Text.RegularExpressions.Regex.IsMatch(value, @"^\w+\s*\["))
                {
                    var identity = FreshRuntimeArrayIdentity(value, instance, function, position, catalog);
                    if (identity.Problem is null && !identity.UnknownIdentityPossible && identity.PossibleIdentities.Length == 1)
                    { payload = FormKey.Factory(identity.PossibleIdentities[0]); dependency = identity.Evidence; }
                }
                else
                {
                    payload = ConstantRuntimeLookup(value, instance, function, catalog, out var lookup);
                    dependency = lookup.Evidence;
                }
                if (payload is not { } child || !RuntimeRecordWinners(catalog).TryGetValue(child, out var winner)
                    || winner.Record.IsDeleted || winner.Record is not ILeveledItemGetter) continue;
                FormKey? parent = instance.Objects.TryGetValue(direct.Groups["recv"].Value, out var target)
                    && RuntimeRecordWinners(catalog).TryGetValue(target, out var targetWinner) && !targetWinner.Record.IsDeleted
                    && targetWinner.Record is ILeveledItemGetter ? target : null;
                if (!result.TryGetValue(child, out var rows)) result[child] = rows = [];
                rows.Add(new(parent, $"possible-runtime-lvli-carrier-v1 {instance.Identity}/{position}/{raw}; {instance.Evidence}; {instance.DiagnosticAssetEvidence}; {dependency}", instance.Identity));
            }
        }
        catalog.RuntimePossibleListCarriers = result.ToDictionary(p => p.Key, p => p.Value.Distinct().OrderBy(e => e.Evidence, StringComparer.Ordinal).ToArray());
    }
}
