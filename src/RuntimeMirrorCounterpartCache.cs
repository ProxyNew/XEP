using System.Security.Cryptography;
using System.Text;
using Mutagen.Bethesda.Plugins;

namespace XEnchantmentPatcher;

public static partial class PatcherEngine
{
    // Only observed-receiver counterpart facts may enter this cache. The caller
    // must apply each source's eligibility, class/subtype and ancestry guards
    // before lookup; ordinary source availability assessments remain separate.
    private readonly record struct RuntimeMirrorCounterpartKey(
        FormKey Receiver, string DirectContext, EquipmentClass Class, string Subtype,
        bool OwnBase, bool ExpandMaterials, SpecializedEvidence GraphEpoch, string TemplateEpoch);

    private sealed class RuntimeMirrorCounterpartCache
    {
        private readonly Catalog catalog;
        private readonly SpecializedEvidence graph;
        private readonly HashSet<FormKey> enabledTemplates;
        private readonly HashSet<EnchantedTemplate> frozenTemplates;
        private readonly Dictionary<RuntimeMirrorCounterpartKey, SpecializedAssessment> entries = [];
        public string TemplateEpoch { get; }
        public IReadOnlySet<FormKey> EnabledTemplates => enabledTemplates;
        public int Count => entries.Count;
        public int Hits { get; private set; }
        public int Misses { get; private set; }

        public RuntimeMirrorCounterpartCache(Catalog catalog)
        {
            this.catalog = catalog;
            graph = SpecializedGraph(catalog);
            enabledTemplates = catalog.Templates.Select(t => t.Item.FormKey).ToHashSet();
            frozenTemplates = new(graph.AllTemplates, ReferenceEqualityComparer.Instance);
            // Capture once for this catalog/profile. The graph's reference also
            // distinguishes winning inputs and disabled-template safety facts.
            var signature = string.Join("\n", catalog.Templates
                .OrderBy(t => t.Item.FormKey).ThenBy(t => t.Enchantment)
                .Select(t => $"{t.Item.FormKey}|{t.BaseItem.FormKey}|{t.Enchantment}|{ClassKey(t.Class)}|"
                    + string.Join(";", t.Parents.OrderBy(p => p.List).ThenBy(p => p.Level).ThenBy(p => p.Count)
                        .Select(p => $"{p.List}:{p.Level}:{p.Count}"))));
            TemplateEpoch = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
                "observed-counterpart-templates-v1\n" + catalog.BroadUnmatchedDistribution + "\n" + signature)));
        }

        private TypedLootUse[] DirectUses(FormKey receiver) => graph.Up(receiver).SelectMany(k => graph.Uses.GetValueOrDefault(k, []))
            .Where(u => u.Known).Distinct().ToArray();

        private static string ContextIdentity(IEnumerable<TypedLootUse> uses)
        {
            // Length prefixes preserve exact field identities without relying on
            // delimiters that may themselves occur in context/family strings.
            var text = new StringBuilder();
            foreach (var use in uses.OrderBy(u => u.Consumer, FormKeyOrder.Instance).ThenBy(u => u.Role, StringComparer.Ordinal)
                .ThenBy(u => u.Context, StringComparer.Ordinal).ThenBy(u => u.Family, StringComparer.Ordinal))
            {
                foreach (var field in new[] { use.Consumer.ToString(), use.Role, use.Context, use.Family,
                             use.Known ? "1" : "0", use.ScopeOnly ? "1" : "0" })
                    text.Append(field.Length).Append(':').Append(field);
            }
            return text.ToString();
        }

        private RuntimeMirrorCounterpartKey Key(FormKey receiver, EquipmentClass cls, string subtype, bool expandMaterials)
        {
            if (!ReferenceEquals(catalog.SpecializedEvidence, graph))
                throw new InvalidOperationException("Runtime mirror counterpart cache belongs to a different frozen graph epoch.");
            if (receiver.IsNull || string.IsNullOrWhiteSpace(subtype))
                throw new InvalidOperationException("Runtime mirror counterpart cache requires a receiver and a resolved subtype.");
            return new(receiver, ContextIdentity(DirectUses(receiver)), cls, subtype,
                IsOwnBaseMaterial(cls.Material), expandMaterials, graph, TemplateEpoch);
        }

        public bool TryGet(FormKey receiver, EquipmentClass cls, string subtype, bool expandMaterials,
            out SpecializedAssessment assessment)
        {
            if (entries.TryGetValue(Key(receiver, cls, subtype, expandMaterials), out var stored))
            {
                Hits++;
                assessment = Copy(stored);
                assessment.MirrorEntry = stored;
                return true;
            }
            Misses++;
            assessment = null!;
            return false;
        }

        public SpecializedAssessment Store(FormKey receiver, EquipmentClass cls, string subtype, bool expandMaterials,
            SpecializedAssessment assessment)
        {
            var key = Key(receiver, cls, subtype, expandMaterials);
            var directUses = DirectUses(receiver).ToHashSet();
            // Routes share proof use arrays; compare each distinct array once.
            var usesEqual = new Dictionary<object, bool>(ReferenceEqualityComparer.Instance);
            bool UsesEqual(IEnumerable<TypedLootUse> uses) => usesEqual.TryGetValue(uses, out var known) ? known : usesEqual[uses] = directUses.SetEquals(uses);
            foreach (var route in assessment.Routes)
            {
                if ((!route.PeerPool.IsNull && !graph.Up(receiver).Contains(route.PeerPool)) || !route.ReplicatedSelector.IsNull
                    || route.MerchantIsolation is not null || route.CountedMerchantSource is not null
                    || !frozenTemplates.Contains(route.Template) || !enabledTemplates.Contains(route.Template.Item.FormKey)
                    || !graph.Lists.ContainsKey(route.Destination.List)
                    || route.IsolationPath?.Any(k => !graph.Lists.ContainsKey(k)) == true
                    || route.MaterialPath?.Any(k => !graph.Lists.ContainsKey(k)) == true)
                    throw new InvalidOperationException("Runtime mirror cache cannot retain a source-bound or non-frozen counterpart route.");
                if (route.SourceProof is { } proof && (proof.Availability.Controls.Length != 0
                    || proof.Availability.Structure is not null || proof.Unlock is not null || proof.Level != 1
                    || !proof.Global.IsNull || proof.Chance is not null || !UsesEqual(proof.Uses)))
                    throw new InvalidOperationException("Runtime mirror cache requires a receiver-only, unconditional context proof.");
            }
            // Validate the entire result before publishing it. Copies isolate the
            // mutable route/evidence/path collections from both the builder and hits.
            return entries[key] = Copy(assessment);
        }

        private static SpecializedAssessment Copy(SpecializedAssessment assessment)
        {
            // One copied proof per distinct source proof: routes keep the sharing the
            // builder gave them, while still owning nothing of the stored entry.
            var proofs = new Dictionary<MaterialSourceProof, MaterialSourceProof>(ReferenceEqualityComparer.Instance);
            MaterialSourceProof CopyProof(MaterialSourceProof proof)
                => proofs.TryGetValue(proof, out var copied) ? copied
                    : proofs[proof] = new MaterialSourceProof(new MaterialAvailabilityPath(), null, 1, proof.Uses.ToArray());
            return new(assessment.Routes.Select(route => route with
            {
                IsolationPath = route.IsolationPath?.ToArray(),
                MaterialPath = route.MaterialPath?.ToArray(),
                SourceProof = route.SourceProof is { } proof ? CopyProof(proof) : null
            }).ToList(), assessment.Evidence.ToList())
            {
                Stage = assessment.Stage,
                Refusals = assessment.Refusals.ToList()
            };
        }
    }
}
