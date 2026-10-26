using System.Text.Json;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;

namespace XEnchantmentPatcher;

public static partial class PatcherEngine
{
    private sealed partial record CdfConsumers
    {
        private sealed record CdfErasureScope((FormKey Consumer, FormKey Base, bool Reference)[] Recipients,
            bool EveryContainer = false, bool ReachesUnsafe = false)
        {
            public void Add(List<CdfRemoval> target, FormKey item, uint count, string provenance, string changeId = "")
            {
                if (EveryContainer) target.Add(new(item, default, default, false, count, provenance, changeId)
                    { EveryContainer = true, ReachesUnsafe = ReachesUnsafe });
                else foreach (var (consumer, basis, reference) in Recipients)
                    target.Add(new(item, consumer, basis, reference, count, provenance, changeId));
            }
        }

        private static bool KnownAbsentSelector(CdfReference reference)
            => reference.Status == "missing-plugin" || reference.Status == "missing-record" && reference.Identifier.Split('|').Length == 2;

        // Positive selectors alone can bound a destructive footprint. Unsupported
        // runtime/location/exclusion predicates cannot prove that an erasure is absent.
        // A malformed selector may disappear natively; use another sound selector or widen.
        private static CdfErasureScope ErasureScope(CdfRule rule, Catalog catalog, bool trustedGuards)
        {
            using var document = JsonDocument.Parse(rule.Json, new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            var raw = document.RootElement;
            var conditions = raw.ValueKind == JsonValueKind.Object && raw.TryGetProperty("conditions", out var value) ? value : NoConditions;
            FormKey[]? Selector(string field)
            {
                if (conditions.ValueKind != JsonValueKind.Object || !conditions.TryGetProperty(field, out var selector)
                    || selector.ValueKind == JsonValueKind.Null) return null;
                if (selector.ValueKind != JsonValueKind.Array || selector.EnumerateArray().Any(e => e.ValueKind != JsonValueKind.String)
                    || rule.Diagnostics.Any(d => d.StartsWith("Duplicate field conditions." + field, StringComparison.Ordinal))) return null;
                var entries = rule.References.Where(r => r.Field == "conditions." + field).ToArray();
                if (entries.Any(r => r.Status != "resolved" && !KnownAbsentSelector(r))) return null;
                return entries.Where(r => r.Status == "resolved").Select(r => FormKey.Factory(r.FormKey!)).Distinct().ToArray();
            }
            var bases = Selector("containers"); var references = Selector("references");
            var bypass = rule.Guards?.BypassUnsafeContainers ?? true;
            var trusted = trustedGuards && rule.Guards is not null;
            var recipients = new List<(FormKey Consumer, FormKey Base, bool Reference)>();
            if (bases is { Length: 0 }) return new([]);
            if (references is not null)
            {
                var winners = RuntimeRecordWinners(catalog);
                foreach (var key in references)
                    if (winners.TryGetValue(key, out var found) && found.Record is IPlacedObjectGetter placed
                        && catalog.ConsumerRecords.Containers.ContainsKey(placed.Base.FormKey)
                        && (bases is null || bases.Contains(placed.Base.FormKey))) recipients.Add((key, placed.Base.FormKey, true));
            }
            else if (bases is not null) recipients.AddRange(bases.Select(key => (key, key, false)));
            else return new([], true, bypass || !trusted);
            if (!bypass && trusted)
                recipients.RemoveAll(r => !catalog.ConsumerRecords.Containers.TryGetValue(r.Base, out var container)
                    || !container.Flags.HasFlag(Container.Flag.Respawns));
            return new(recipients.ToArray());
        }

        private sealed record CdfReplacementFacts(IReadOnlySet<FormKey> Predecessors, FormKey[] Erased, IReadOnlySet<FormKey> Restored);
        private static CdfReplacementFacts ReplacementFacts(CdfRule rule, CdfChange change, FormKey[] payloads, bool drawn,
            Catalog catalog, List<string> notes, ref CdfKeywordMembership? membership)
        {
            var refs = rule.References.Where(r => r.Field == $"changes[{change.Index}]."
                + (change.Operation == "replacement" ? "remove" : "removeByKeywords")).ToArray();
            var keys = refs.Select(r => FormKey.Factory(r.FormKey!)).Distinct().Order().ToArray();
            IReadOnlySet<FormKey> predecessors;
            FormKey[] erased;
            if (change.Operation == "replacement") { predecessors = keys.ToHashSet(); erased = keys; }
            else
            {
                membership ??= new(catalog, notes);
                predecessors = membership.Certain(keys); erased = membership.Match(keys);
            }
            // Only an unambiguous deterministic re-add restores the original form.
            return new(predecessors, erased, drawn ? new HashSet<FormKey>() : payloads.Where(predecessors.Contains).ToHashSet());
        }

        private static CdfConsumers DestructiveFallback(CdfInputReport report, Catalog catalog, bool trustedGuards, List<string> notes)
        {
            var removals = new List<CdfRemoval>(); var variants = new List<CdfRemoval>();
            CdfKeywordMembership? membership = null;
            void UnknownFile(string provenance) => new CdfErasureScope([], true, true).Add(removals, default, 0, provenance);
            if (report.Status is not ("source-matched-3.1.0" or "accepted-major-3" or "accepted-other-major")) UnknownFile("incomplete CDF capture");
            foreach (var file in report.Files)
            {
                if (file.Status is "malformed-json" or "unreadable") { UnknownFile(file.Path); continue; }
                foreach (var rule in file.Rules)
                {
                    if (rule.NativeDisposition.StartsWith("stops-file-", StringComparison.Ordinal)
                        || rule.NativeDisposition is "skipped-file-tail" or "skipped-empty-condition" or "no-changes") continue;
                    var provenance = $"{file.Path} rule {rule.Index} ({rule.FriendlyName})";
                    CdfErasureScope scope;
                    try { scope = ErasureScope(rule, catalog, trustedGuards); }
                    catch (JsonException) { UnknownFile(provenance); continue; }
                    if (rule.NativeDisposition == "native-error-risk"
                        || rule.Diagnostics.Any(d => d == "Duplicate field changes."))
                    { scope.Add(removals, default, 0, provenance); continue; }
                    foreach (var change in rule.Changes)
                    {
                        var prefix = $"changes[{change.Index}]";
                        var unknown = rule.Diagnostics.Any(d => d.StartsWith("Unknown field " + prefix, StringComparison.Ordinal)
                            || d.StartsWith("Duplicate field " + prefix, StringComparison.Ordinal));
                        if (!unknown && change.Disposition is "skipped-invalid-change" or "skipped-unresolved-form" or "no-operation") continue;
                        var refs = rule.References.Where(r => r.Field.StartsWith(prefix + ".", StringComparison.Ordinal)).ToArray();
                        if (unknown || change.Disposition != "unsupported-effect" || refs.Any(r => r.Status != "resolved"))
                        { scope.Add(removals, default, 0, provenance); continue; }
                        FormKey[] Keys(string field) => refs.Where(r => r.Field == prefix + "." + field).Select(r => FormKey.Factory(r.FormKey!)).ToArray();
                        var changeId = $"{file.Path}|{rule.Index}|{change.Index}";
                        if (change.Operation == "removal")
                            foreach (var item in Keys("remove")) scope.Add(removals, item, change.Count ?? 0, provenance, changeId);
                        else if (change.Operation == "keyword-removal")
                        {
                            membership ??= new(catalog, notes);
                            foreach (var item in membership.Match(Keys("removeByKeywords").Distinct().Order().ToArray()))
                                scope.Add(removals, item, 0, provenance, changeId);
                        }
                        else if (change.Operation is "replacement" or "keyword-replacement")
                        {
                            var payloads = Keys("add");
                            var drawn = rule.Guards?.RandomAdd != false && payloads.Distinct().Count() > 1;
                            var facts = ReplacementFacts(rule, change, payloads, drawn, catalog, notes, ref membership);
                            foreach (var item in facts.Erased)
                                if (!facts.Restored.Contains(item)) scope.Add(removals, item, 0, provenance, changeId);
                                else if (change.Operation == "keyword-replacement") scope.Add(variants, item, 0, provenance, changeId);
                        }
                        else if (change.Operation != "addition") scope.Add(removals, default, 0, provenance, changeId);
                    }
                }
            }
            return new([], [.. notes, "CDF destructive fallback retains negative constraints without additions or residual-quantity permission."])
                { Removals = new(removals, [], variants, RespawningContainers(catalog).ToHashSet()), KeywordMembership = membership };
        }
    }
}
