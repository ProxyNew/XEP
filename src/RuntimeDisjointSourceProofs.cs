using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;

namespace XEnchantmentPatcher;

public static partial class PatcherEngine
{
    private sealed record RuntimeAddPayload(FormKey Payload, string Evidence);
    private sealed record RuntimePropertyBinding(string? Problem, bool MayRetarget)
    {
        public string Evidence { get; init; } = "";
        public RuntimeFrameFailure? Failure { get; init; }
        public string[] CoveredInstanceIdentities { get; init; } = [];
        // Slice proofs only: changes to a shared list by another patched mod that its own analysis cannot resolve.
        public string[] Warnings { get; init; } = [];
    }
    private sealed record RuntimeFrameFailure(string CallerIdentity, string Property, string Function, string Line);
    private sealed record RuntimeBindingCheck(string OwnerIdentity, string Property, bool Receiver, RuntimePropertyBinding Result);
    private static readonly HashSet<string> FootprintListMethods = new(StringComparer.OrdinalIgnoreCase)
    {
        "AddForm", "Revert", "RemoveAddedForm", "SetChanceNone", "SetChanceGlobal", "SetNthLevel", "SetNthCount",
        "GetChanceNone", "GetNumForms", "GetNthLevel", "GetNthCount"
    };

    // A mutable EMPTY competitor may remain empty for this source union. Prove
    // all authored and all possible added outcomes, irrespective of invocation
    // timing/repetition. Never promote a runtime edge into source availability.
    private static string? RuntimeDisjointSourceBranch(FormKey root, IReadOnlySet<FormKey> sources, Catalog catalog,
        out string evidence)
    {
        evidence = "";
        var author = SpecializedGraph(catalog).Lists;
        var items = StaticSourceItems(catalog);
        var globals = catalog.Mods.SelectMany(m => m.Globals).GroupBy(g => g.FormKey)
            .Where(g => !g.Last().IsDeleted).Select(g => g.Key).ToHashSet();
        var done = new HashSet<FormKey>(); var active = new HashSet<FormKey>();
        var nodes = new List<string>(); var ignored = new List<string>();
        var holderCertificates = new List<string>();
        string? Visit(FormKey key, int depth)
        {
            if (sources.Contains(key)) return $"possible runtime/authored outcome reaches selected source {key}";
            if (done.Contains(key)) return null;
            if (depth > 16 || done.Count + active.Count >= 8192) return "runtime disjoint proof exceeds 16 edges or 8192 states";
            if (!author.TryGetValue(key, out var list))
            {
                if (!items.Contains(key)) return $"unresolved or non-item outcome {key}";
                done.Add(key); return null;
            }
            if (!active.Add(key)) return $"cyclic authored/runtime payload graph at {key}";
            if (list.IsDeleted || ((int)list.Flags & ~15) != 0 || (list.Entries?.Count ?? 0) > 255)
                return $"invalid list flags/deletion/capacity at {key}";
            if (!list.Global.IsNull && !globals.Contains(list.Global.FormKey)) return $"unresolved chance global at {key}";
            if (RuntimeHolderFrameProblem([], catalog, key, out var holderEvidence) is { } holderProblem) return $"{key}: {holderProblem}";
            if (holderEvidence.Length > 0) holderCertificates.Add(holderEvidence);
            if (catalog.RuntimeControlledLists.ContainsKey(key) || catalog.OtherRuntimeMutations.Contains(key)
                || catalog.UnresolvedScriptPayloads.Contains(key) || catalog.UnsafeAvailability.ContainsKey(key)
                || catalog.ScriptUnlocks.ContainsKey(key)) return $"other or unresolved runtime mutation at {key}";
            if (catalog.RuntimeAddPayloadProblems.TryGetValue(key, out var bindingProblem)) return $"{key}: {bindingProblem}";
            nodes.Add(SourceNodeIdentity(list));
            foreach (var entry in list.Entries ?? [])
            {
                if (entry.Data is not { Count: > 0 } d || d.Level < 0 || entry.ExtraData is not null)
                    return $"invalid count/level/entry extra-data at {key}";
                var problem = Visit(d.Reference.FormKey, depth + 1);
                if (problem is not null) return problem;
            }
            if (catalog.ScriptAddTargets.Contains(key))
            {
                if (!catalog.RuntimeAddPayloads.TryGetValue(key, out var payloads) || payloads.Count == 0)
                    return $"complete fixed AddForm payload footprint unavailable at {key}";
                foreach (var payload in payloads)
                {
                    var problem = Visit(payload.Payload, depth + 1);
                    if (problem is not null) return problem;
                    ignored.Add($"{key} may add {payload.Payload} ({payload.Evidence})");
                }
            }
            active.Remove(key); done.Add(key); return null;
        }
        var failure = Visit(root, 0);
        if (failure is not null) return failure;
        var signature = "runtime-source-disjoint-v1|" + string.Join(';', sources.Order()) + "|" + string.Join('|', nodes.Order(StringComparer.Ordinal))
            + "|" + string.Join('|', ignored.Distinct().Order(StringComparer.Ordinal));
        if (holderCertificates.Count > 0)
        {
            var holders = string.Join('|', holderCertificates.Distinct().Order(StringComparer.Ordinal));
            signature += "|" + RuntimeHolderEvidenceDomain(holders) + "|" + holders;
        }
        var identity = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(signature)));
        evidence = $"Runtime source-disjoint proof {identity}: [{string.Join(", ", ignored.Distinct().Order(StringComparer.Ordinal))}]; fixed payloads cannot emit selected sources [{string.Join(", ", sources.Order())}]. Execution timing is unproved and is not used as availability evidence";
        return null;
    }
}
