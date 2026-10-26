using System.Globalization;
using System.Collections.Immutable;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using Noggog;

namespace XEnchantmentPatcher;

public static partial class PatcherEngine
{
    // Native startup inputs are an analytical overlay, not plugin overrides.
    // Keep the physical baseline and the append occurrences together so normal
    // output can leave those occurrences for SkyPatcher to apply exactly once.
    private sealed class SkyPatcherLists
    {
        public Dictionary<FormKey, ILeveledItemGetter> Physical { get; } = [];
        public Dictionary<FormKey, List<LeveledItemEntry>> Appended { get; } = [];
        public Dictionary<FormKey, int> AdditionalCapacity { get; } = [];
        public Dictionary<FormKey, int> MinimumDelta { get; } = [];
        public Dictionary<FormKey, string> Problems { get; } = [];
        public DiagnosticMessages Notes { get; } = [];
        // Capture-wide failure only. Individual excluded rules retain supported import.
        public bool ImportDisabled { get; private set; }
        public string DllSha256 { get; private init; } = "";
        public HashSet<string> ReferencedEditorIds { get; } = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> unavailablePlugins = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<FormKey, ILeveledItemGetter> effective = [];
        private sealed record PresenceCheck(FormKey Item, bool Present, int PriorAppends);
        private sealed record StagedDispatch(FormKey Target, ILeveledItemGetter Prior, LeveledItem? Changed,
            LeveledItemEntry[] Added, PresenceCheck[] Checks, int Replaced, int Removed, int Skipped);
        private readonly Dictionary<FormKey, List<PresenceCheck>> presenceChecks = [];
        private sealed record EditorIdFilter(string[] And, string[] Or, string[] Excluded)
        {
            public bool HasPositive => And.Length > 0 || Or.Length > 0;
            public bool Matches(string? editorId)
            {
                var folded = FoldEditorId(editorId ?? "");
                bool Contains(string term) => folded.Contains(term, StringComparison.Ordinal);
                return HasPositive && And.All(Contains) && (Or.Length == 0 || Or.Any(Contains)) && !Excluded.Any(Contains);
            }
        }
        private sealed record Replacement(FormKey From, FormKey To);
        private sealed record Restriction(string Relation, ushort Value)
        {
            public bool Matches(ushort value) => Relation switch
            { "<" => value < Value, ">" => value > Value, "<=" => value <= Value, ">=" => value >= Value, _ => value == Value };
        }
        private sealed record Removal(FormKey Item, Restriction? Level, Restriction? Count)
        {
            public bool Matches(ProjectedStaticEntry entry) => entry.Reference.GeneratedOccurrence is null && entry.Reference.Existing == Item
                && (Level is null && Count is null || Level?.Matches(unchecked((ushort)(entry.Level ?? 0))) == true || Count?.Matches(unchecked((ushort)entry.Count)) == true);
        }
        // List controls are assignments: the rule's last flag key wins, a global is
        // set or cleared, and Chance None is replaced. Count multipliers run last.
        private sealed record ListControls(LeveledItem.Flag? Flags, bool ClearGlobal, FormKey Global, Percent? ChanceNone)
        {
            public bool Any => Flags is not null || ClearGlobal || !Global.IsNull || ChanceNone is not null;
            public void Apply(LeveledItem list)
            {
                if (Flags is { } flags) list.Flags = flags;
                if (ClearGlobal) list.Global.Clear();
                else if (!Global.IsNull) list.Global.SetTo(Global);
                if (ChanceNone is { } chance) list.ChanceNone = chance;
            }
            // Compare only assigned fields; unassigned controls remain the caller's.
            public bool Preserves(ILeveledItemGetter list, LeveledItem.Flag? flags = null)
                => (Flags is null || Flags == (flags ?? list.Flags))
                    && (ClearGlobal ? list.Global.IsNull : Global.IsNull || Global == list.Global.FormKey)
                    && (ChanceNone is not { } chance || chance.Equals(list.ChanceNone));

            // Native rules assign in order. Only the final assignment of each field survives.
            public ListControls Then(ListControls next)
            {
                var global = next.ClearGlobal || !next.Global.IsNull;
                return new(next.Flags ?? Flags, global ? next.ClearGlobal : ClearGlobal,
                    global ? next.Global : Global, next.ChanceNone ?? ChanceNone);
            }
        }
        private sealed record CountMultiplier(FormKey Item, float Factor);
        private sealed record NativeRule(string Location, (LeveledItemEntry Entry, bool Once)[] Payloads, Replacement[] Replacements,
            Removal[] Removals, IReadOnlySet<FormKey> KeywordItems, bool HasKeywordRemoval, bool Clear, EditorIdFilter? Filter = null)
        {
            public ListControls Controls { get; init; } = new(null, false, default, null);
            public CountMultiplier[] Multipliers { get; init; } = [];
            public bool Erases => Removals.Length > 0 || HasKeywordRemoval || Clear;
            public bool Mutates => Replacements.Length > 0 || Erases || Multipliers.Length > 0;
        }
        // Only assignments from dispatches that actually executed constrain a written header.
        private readonly Dictionary<FormKey, ListControls> appliedControls = [];
        public bool PreservesControls(FormKey list, ILeveledItemGetter proposed, LeveledItem.Flag? flags = null)
            => !appliedControls.TryGetValue(list, out var controls) || controls.Preserves(proposed, flags);
        private readonly List<NativeRule> broadRules = [];
        private readonly Dictionary<FormKey, List<NativeRule>> dispatches = [];
        private readonly Dictionary<FormKey, HashSet<(FormKey Source, FormKey Predecessor)>> replacementPeers = [];
        public bool HasReplacementPeer(FormKey list, FormKey source, FormKey predecessor)
            => replacementPeers.TryGetValue(list, out var peers) && peers.Contains((source, predecessor));
        public bool HasBroadRules => broadRules.Count > 0;
        public bool HasEditorIdRules => broadRules.Any(r => r.Filter is not null);
        private readonly HashSet<FormKey> originalLists = [];

        // Generated lists participate in every broad dispatch. Existing item leaves
        // may change selection shares, but cannot add an edge to an XEP variant.
        // New list edges, level cutoffs and overflow need separate route proofs.
        public (ImmutableArray<ProjectedStaticEntry> Effective, ImmutableArray<ProjectedStaticEntry> Physical)
            ProjectGenerated(ILeveledItemGetter list, ImmutableArray<ProjectedStaticEntry> desired)
            => ProjectGeneratedWithName(list, desired, list.EditorID);

        public (ImmutableArray<ProjectedStaticEntry> Effective, ImmutableArray<ProjectedStaticEntry> Physical)
            ProjectGeneratedWithName(ILeveledItemGetter list, ImmutableArray<ProjectedStaticEntry> desired, string? editorId)
            => ProjectGeneratedCore(list, desired, editorId, false);

        // The shared compositor proves the actual split nodes after LLOS inversion.
        public (ImmutableArray<ProjectedStaticEntry> Effective, ImmutableArray<ProjectedStaticEntry> Physical)
            ProjectGeneratedForProjection(ILeveledItemGetter list, ImmutableArray<ProjectedStaticEntry> desired, string? editorId)
            => ProjectGeneratedCore(list, desired, editorId, true);

        private (ImmutableArray<ProjectedStaticEntry> Effective, ImmutableArray<ProjectedStaticEntry> Physical)
            ProjectGeneratedCore(ILeveledItemGetter list, ImmutableArray<ProjectedStaticEntry> desired, string? editorId, bool deferOverflowProof)
        {
            InvalidDataException Refuse(string why) => new($"SkyPatcher generated list {list.EditorID} [{list.FormKey}]: {why}. No output was installed.");
            if (broadRules.Count == 0) return (desired, desired);
            if (desired.Length > 255 && !deferOverflowProof) throw Refuse("overflow children require a broad-selector composition proof");
            if (desired.Length > 120) return (desired, desired); // Every native dispatch skips this list.
            var matching = broadRules.Where(r => r.Filter is null || r.Filter.Matches(editorId)).ToArray();
            var physical = desired.ToList();
            foreach (var (entry, once) in matching.SelectMany(r => r.Payloads))
            {
                if (once) continue;
                var index = physical.FindIndex(e => SameEntry(e, entry));
                if (index >= 0) physical.RemoveAt(index);
            }
            var effective = physical.ToList();
            var levels = desired.Select(e => e.Level).ToHashSet();
            ListControls? controls = null;
            string? controlLocation = null;
            foreach (var rule in matching)
            {
                if (effective.Count > 120) continue;
                if (Replace(effective, rule.Replacements) > 0)
                    throw Refuse($"{rule.Location} replaces generated-list content whose eligibility and consumers are unproved");
                Erase(effective, rule);
                var present = effective.Where(e => e.Reference.GeneratedOccurrence is null).Select(e => e.Reference.Existing).ToHashSet();
                foreach (var (entry, once) in rule.Payloads)
                {
                    var item = entry.Data!.Reference.FormKey;
                    if (once && present.Contains(item)) continue;
                    if (originalLists.Contains(item)) throw Refuse($"{rule.Location} adds a list edge whose cycles and consumers are unproved");
                    if (((int)list.Flags & 1) == 0 && !levels.Contains(entry.Data.Level))
                        throw Refuse($"{rule.Location} introduces a highest-eligible level cutoff");
                    effective.Add(new(entry, new(item))); present.Add(item);
                }
                if (rule.Controls.Any)
                {
                    controls = controls?.Then(rule.Controls) ?? rule.Controls;
                    controlLocation = rule.Location;
                }
                Multiply(effective, rule.Multipliers);
                if (effective.Count > 255) throw Refuse($"{rule.Location} exceeds native list capacity");
            }
            if (controls is not null && !controls.Preserves(list))
                throw Refuse($"{controlLocation} reassigns generated-list controls whose availability is unproved");
            // Removing a baked append is valid only if native replay restores every
            // planned occurrence. Metadata-bearing entries are never subtracted.
            var available = effective.GroupBy(e => (e.Reference, e.Level, e.Count, e.HasExtraData)).ToDictionary(g => g.Key, g => g.Count());
            foreach (var entry in desired)
            {
                var key = (entry.Reference, entry.Level, entry.Count, entry.HasExtraData);
                if (available.GetValueOrDefault(key) == 0) throw Refuse("native replay would lose a planned entry");
                available[key]--;
            }
            return (effective.ToImmutableArray(), physical.ToImmutableArray());
        }

        // Native lowercases bytes: fold ASCII, leaving non-ASCII text unchanged.
        private static string FoldEditorId(string text) => string.Create(text.Length, text, static (result, source) =>
        {
            for (var i = 0; i < source.Length; i++) result[i] = source[i] is >= 'A' and <= 'Z' ? (char)(source[i] + 32) : source[i];
        });

        public string SelectorIdentity(string? editorId) => string.Join(',', broadRules.Select((rule, index) => (rule, index))
            .Where(p => p.rule.Filter is null || p.rule.Filter.Matches(editorId)).Select(p => p.index));

        private static bool SameEntry(ProjectedStaticEntry projected, LeveledItemEntry entry)
            => projected.Reference.GeneratedOccurrence is null && projected.Reference.Existing == entry.Data!.Reference.FormKey
                && projected.Level == entry.Data.Level && projected.Count == entry.Data.Count && !projected.HasExtraData;

        private static List<ProjectedStaticEntry> Project(IEnumerable<ILeveledItemEntryGetter> entries) => entries
            .Select(e => new ProjectedStaticEntry(e, new(e.Data?.Reference.FormKey ?? FormKey.Null))).ToList();

        // Replacement changes only the reference. Keep the complete entry payload.
        private static int Replace(List<ProjectedStaticEntry> entries, IEnumerable<Replacement> replacements,
            Dictionary<ProjectedStaticEntry, ProjectedStaticEntry>? origins = null)
        {
            var changes = 0;
            foreach (var replacement in replacements)
            for (var i = 0; i < entries.Count; i++)
            {
                var entry = entries[i];
                if (replacement.From == replacement.To || entry.Reference.GeneratedOccurrence is not null || entry.Reference.Existing != replacement.From) continue;
                var payload = entry.Payload;
                payload.Data!.Reference.SetTo(replacement.To);
                entries[i] = new(payload, new(replacement.To));
                if (origins is not null && origins.Remove(entry, out var origin)) origins[entries[i]] = origin;
                changes++;
            }
            return changes;
        }

        public static bool SameProjected(ProjectedStaticEntry a, ProjectedStaticEntry b)
            => a.Reference == b.Reference && a.Payload.Equals(b.Payload);

        private static int Erase(List<ProjectedStaticEntry> entries, NativeRule rule)
        {
            if (rule.HasKeywordRemoval && entries.Any(e => e.Reference.GeneratedOccurrence is not null))
                throw new InvalidDataException($"SkyPatcher {rule.Location}: keywords of newly generated entries are unproved for keyword removal. No output was installed.");
            var before = entries.Count;
            if (rule.Clear) entries.Clear();
            else entries.RemoveAll(e => rule.Removals.Any(r => r.Matches(e)) || rule.KeywordItems.Contains(e.Reference.Existing));
            return before - entries.Count;
        }

        // Native multiplies matching counts in single precision and rounds up.
        private static int MultipliedCount(int count, float factor) => (int)MathF.Ceiling(count * factor);

        private static void Multiply(List<ProjectedStaticEntry> entries, IEnumerable<CountMultiplier> multipliers,
            Dictionary<ProjectedStaticEntry, ProjectedStaticEntry>? origins = null)
        {
            if (entries.Count == 0) return;
            foreach (var multiplier in multipliers)
            for (var i = 0; i < entries.Count; i++)
            {
                var entry = entries[i];
                if (entry.Reference.GeneratedOccurrence is not null || entry.Reference.Existing != multiplier.Item || !entry.HasData) continue;
                var count = MultipliedCount(entry.Count, multiplier.Factor);
                if (count > short.MaxValue)
                    throw new InvalidDataException($"SkyPatcher count multiplier for {multiplier.Item} exceeds the supported count range. No output was installed.");
                var payload = entry.Payload;
                payload.Data!.Count = (short)count;
                entries[i] = new(payload, entry.Reference);
                if (origins is not null && origins.Remove(entry, out var origin)) origins[entries[i]] = origin;
            }
        }

        private static void Replay(List<ProjectedStaticEntry> entries, IEnumerable<NativeRule> rules,
            Dictionary<ProjectedStaticEntry, ProjectedStaticEntry>? origins = null)
        {
            foreach (var rule in rules)
            {
                if (entries.Count > 120) continue;
                Replace(entries, rule.Replacements, origins);
                Erase(entries, rule);
                var present = entries.Where(e => e.Reference.GeneratedOccurrence is null).Select(e => e.Reference.Existing).ToHashSet();
                foreach (var (entry, once) in rule.Payloads)
                {
                    var item = entry.Data!.Reference.FormKey;
                    if (once && present.Contains(item)) continue;
                    entries.Add(new(entry, new(item))); present.Add(item);
                }
                Multiply(entries, rule.Multipliers, origins);
            }
        }

        private void CaptureReplacementPeers()
        {
            foreach (var (list, final) in effective)
            {
                if (!dispatches[list].Any(r => r.Replacements.Length > 0)) continue;
                var original = Project(Physical[list].Entries ?? []);
                var replay = original.ToList();
                var origins = original.ToDictionary(e => e, e => e);
                Replay(replay, dispatches[list], origins);
                // Only surviving original instances carry predecessor evidence.
                // Native re-additions are new instances, even with identical data.
                foreach (var after in replay)
                {
                    if (!origins.TryGetValue(after, out var before)) continue;
                    if (before.Count != 1 || before.HasExtraData || !before.HasData || before.Reference == after.Reference) continue;
                    var payload = after.Payload;
                    if (!(final.Entries ?? []).Any(e => payload.Equals(e))) continue;
                    if (!replacementPeers.TryGetValue(list, out var peers)) replacementPeers[list] = peers = [];
                    peers.Add((after.Reference.Existing, before.Reference.Existing));
                }
            }
        }

        private ImmutableArray<ProjectedStaticEntry> WriteMutations(FormKey target, ImmutableArray<ProjectedStaticEntry> desired)
        {
            InvalidDataException Refuse(string reason) => new($"SkyPatcher target {target}: {reason}. No output was installed.");
            // Preserve physical authored entries, adding only the XEP delta. This
            // avoids trying to invert many-to-one replacements or ordered chains.
            var additions = desired.ToList();
            foreach (var entry in Project(effective[target].Entries ?? []))
            {
                var index = additions.FindIndex(e => SameProjected(e, entry));
                if (index < 0) throw Refuse("rewritten mutation baseline has no proved native output composition");
                additions.RemoveAt(index);
            }
            var physical = Project(Physical[target].Entries ?? []);
            physical.AddRange(additions);
            if (physical.Count > 255) throw Refuse("replacement output requires unsupported overflow restructuring");
            var replay = physical.ToList();
            Replay(replay, dispatches[target]);
            foreach (var entry in desired)
            {
                var index = replay.FindIndex(e => SameProjected(e, entry));
                if (index < 0) throw Refuse("native replay would change planned output");
                replay.RemoveAt(index);
            }
            if (replay.Count != 0) throw Refuse("native replay would restore displaced content");
            return physical.ToImmutableArray();
        }

        public bool HasInputs => Physical.Count > 0 || broadRules.Count > 0;

        // Planning asks the writer's exact composition question. A refusal names
        // why this list cannot carry the planned entries; it is never a reason to
        // abort an otherwise valid run.
        public string? WritingProblem(FormKey target, ImmutableArray<ProjectedStaticEntry> desired, ILeveledItemGetter? written = null)
        {
            try { ForWriting(target, desired, written); return null; }
            catch (InvalidDataException ex) { return ex.Message.Replace(" No output was installed.", "", StringComparison.Ordinal); }
        }

        public string? GeneratedProblem(ILeveledItemGetter list, ImmutableArray<ProjectedStaticEntry> desired, string? editorId)
        {
            try { ProjectGeneratedWithName(list, desired, editorId); return null; }
            catch (InvalidDataException ex) { return ex.Message.Replace(" No output was installed.", "", StringComparison.Ordinal); }
        }

        public void ValidateOutputPlugins(IEnumerable<string> outputPaths)
        {
            foreach (var path in outputPaths)
                if (unavailablePlugins.Contains(Path.GetFileName(path)))
                    throw new InvalidDataException($"SkyPatcher references absent plugin {Path.GetFileName(path)}, which XEP would create. Its startup rules would change after generation; this output name is unsupported. No output was installed.");
        }

        public ILeveledItemGetter BaselineFor(ILeveledItemGetter original)
            => effective.GetValueOrDefault(original.FormKey) ?? original;

        public int EntryCeiling(FormKey target)
            => AdditionalCapacity.TryGetValue(target, out var remaining)
                ? Math.Min(255, (effective[target].Entries?.Count ?? 0) + remaining) : 255;

        public ImmutableArray<ProjectedStaticEntry> ForWriting(FormKey target, ImmutableArray<ProjectedStaticEntry> desired,
            ILeveledItemGetter? written = null)
            => ForWritingCore(target, desired, written, false);

        public ImmutableArray<ProjectedStaticEntry> ForProjection(FormKey target, ImmutableArray<ProjectedStaticEntry> desired,
            ILeveledItemGetter written)
            => ForWritingCore(target, desired, written, true);

        private ImmutableArray<ProjectedStaticEntry> ForWritingCore(FormKey target, ImmutableArray<ProjectedStaticEntry> desired,
            ILeveledItemGetter? written, bool deferOverflowProof)
        {
            if (Problems.ContainsKey(target))
                throw new InvalidDataException($"SkyPatcher target {target}: unsupported input prevents proving output composition. No output was installed.");
            // Native control assignments run again at startup. A written header that
            // differs from the effective one would be silently overwritten.
            if (written is not null && !PreservesControls(target, written))
                throw new InvalidDataException($"SkyPatcher target {target}: XEP list controls would be reassigned by native startup rules. No output was installed.");
            if (HasEditorIdRules && desired.Length > 255 && !deferOverflowProof)
                throw new InvalidDataException($"SkyPatcher target {target}: overflow children require an EditorID-selector composition proof. No output was installed.");
            if (!Physical.TryGetValue(target, out var baseline)) return desired;
            var imported = Appended.GetValueOrDefault(target) ?? [];
            var effectiveBaseline = effective[target].Entries?.Count ?? 0;
            var delta = desired.Length - effectiveBaseline;
            if (delta > AdditionalCapacity.GetValueOrDefault(target, int.MaxValue) || delta < MinimumDelta.GetValueOrDefault(target, int.MinValue))
                throw new InvalidDataException($"SkyPatcher target {target}: XEP additions would change native capacity dispatch. No output was installed.");
            if (dispatches[target].Any(r => r.Mutates)) return WriteMutations(target, desired);
            var physical = desired.ToList();
            foreach (var entry in imported)
            {
                var index = physical.FindIndex(e => SameEntry(e, entry));
                if (index < 0)
                    throw new InvalidDataException($"SkyPatcher target {target}: a rewritten imported entry cannot be preserved by normal output. No output was installed.");
                physical.RemoveAt(index);
            }
            var present = physical.Where(e => e.Reference.GeneratedOccurrence is null).Select(e => e.Reference.Existing).ToHashSet();
            var applied = 0;
            foreach (var check in presenceChecks.GetValueOrDefault(target) ?? [])
            {
                while (applied < check.PriorAppends) present.Add(imported[applied++].Data!.Reference.FormKey);
                if (present.Contains(check.Item) != check.Present)
                    throw new InvalidDataException($"SkyPatcher target {target}: XEP output would change an addOnceToLLs presence test. No output was installed.");
            }
            if (physical.Count > 255 && !deferOverflowProof)
                throw new InvalidDataException($"SkyPatcher target {target}: overflow restructuring requires a separate native dispatch proof. No output was installed.");
            return physical.ToImmutableArray();
        }

        public void ValidateOverflow(FormKey root, string? rootName, StaticOverflowNode tree,
            ImmutableArray<string> childNames, bool beforeLlos)
        {
            var index = 0;
            void Check(StaticOverflowNode node, string? name, bool originalRoot)
            {
                // Native leveledlist.cpp:244,277,306,356: every dispatch skips
                // >120 entries. Unknown earlier LLOS changes cannot use that cap.
                if (beforeLlos && node.EntryCount > 120) return;
                if (originalRoot && dispatches.ContainsKey(root)
                    || broadRules.Any(r => r.Filter is null || r.Filter.Matches(name)))
                    throw new InvalidDataException($"SkyPatcher list {rootName} [{root}]: overflow children or root match a native dispatch at {name}; its eligibility and consumers are unproved. No output was installed.");
            }
            void Visit(StaticOverflowNode node)
            {
                foreach (var branch in node.Branches)
                {
                    Visit(branch.Child);
                    Check(branch.Child, childNames[index++], false);
                }
            }
            Visit(tree);
            Check(tree, rootName, true);
        }

        public static SkyPatcherLists Read(SkyPatcherInputReport? inputs, Dictionary<FormKey, ILeveledItemGetter> lists)
        {
            using var timing = PhaseTiming.Measure("skypatcher.read");
            var result = new SkyPatcherLists { DllSha256 = inputs?.DllSha256 ?? "" };
            result.originalLists.UnionWith(lists.Keys);
            if (inputs is null || inputs.Status is "absent" or "disabled" or "missing-dll") return result;
            foreach (var reference in inputs.Files.Where(f => f.Status == "active").SelectMany(f => f.Rules)
                .Where(r => r.Status != "ignored-no-op").SelectMany(r => r.References))
            {
                var separator = reference.Identifier.IndexOf('|');
                if (separator < 0) result.ReferencedEditorIds.Add(reference.Identifier);
                else if (reference.Status == "missing-plugin") result.unavailablePlugins.Add(reference.Identifier[..separator].Trim());
            }
            foreach (var file in inputs.Files.Where(f => f.Status == "missing-plugin"))
            {
                var category = file.Path.Split('/').ElementAtOrDefault(1) ?? "";
                var name = Path.GetFileName(file.Path);
                var ini = name.IndexOf(".ini", StringComparison.Ordinal);
                if (ini >= 0 && (category.Equals("leveledList", StringComparison.OrdinalIgnoreCase) || CategoryMayRun(inputs, category)))
                    result.unavailablePlugins.Add(name[..ini]);
            }
            if (inputs.Status is not ("input-contract-7.0" or "unsupported-editorid-dependency" or "incomplete-capture"))
                return result.WithoutImport([$"SkyPatcher input contract is {inputs.Status}."]);
            var exclusions = new List<string>();
            void Exclude(string location, string text, string reason)
                => exclusions.Add($"SkyPatcher {location}: excluded line: {reason} Raw input: {text}");
            // These categories can change equipment, templates, script handles or
            // consumer edges used by the shared route proofs. Their semantics are
            // outside this list adapter; exclude their lines under the user's
            // partial-analysis policy. Disabled and missing-plugin files do not count.
            foreach (var file in inputs.Files.Where(f => f.Status == "other-category-unanalysed" && f.Rules.Count > 0))
            {
                var category = file.Path.Split('/').ElementAtOrDefault(1) ?? "";
                if (!new[] { "weapon", "armor", "magicEffect", "enchantment", "npc", "race", "raceHook", "outfit",
                    "container", "faction", "formList", "reference", "cell", "location", "encounterzone" }
                    .Contains(category, StringComparer.OrdinalIgnoreCase)
                    && !inputs.Files.Any(f => f.Status == "active" && f.Rules.Any(r => r.Fields.Contains("removeObjectsByKeyword", StringComparer.OrdinalIgnoreCase)))
                    || !CategoryMayRun(inputs, category)) continue;
                if (inputs.IrrelevantFurnitureFiles.Contains(file.Path)) continue;
                if (file.Rules.All(rule => HasIrrelevantWrites(category, rule.Text)))
                {
                    if (category.Equals("cell", StringComparison.OrdinalIgnoreCase))
                    {
                        result.Notes.AddInformation($"SkyPatcher {file.Path}: ignored irrelevant cell rules; captured input is retained in the report.");
                        foreach (var rule in file.Rules.Where(r => IsCellSelectorNoOp(r.Text)))
                            result.Notes.AddInformation($"SkyPatcher {file.Path}:{rule.Line}: ignored selector-only native no-op: {rule.Text}");
                    }
                    continue;
                }
                foreach (var rule in file.Rules.Where(r => !HasIrrelevantWrites(category, r.Text)))
                    Exclude($"{file.Path}:{rule.Line}", rule.Text, $"enabled {category} operation is not analysed");
            }
            foreach (var file in inputs.Files.Where(f => f.Status == "active"))
            foreach (var rule in file.Rules)
            {
                if (rule.Status == "ignored-no-op") continue;
                var location = $"{file.Path}:{rule.Line}";
                if ((inputs.Status == "unsupported-editorid-dependency"
                        || inputs.Status == "incomplete-capture" && !inputs.EditorIdCacheAvailable)
                    && (rule.References.Any(r => !r.Identifier.Contains('|'))
                        || rule.Fields.Any(f => f.StartsWith("filterByEditorId", StringComparison.OrdinalIgnoreCase))))
                {
                    Exclude(location, rule.Text, "EditorID cache is unavailable");
                    continue;
                }
                var clauses = rule.Text.Split(':').Select(c => c.Split('=', 2)).Where(c => c.Length == 2).ToArray();
                var broad = clauses.Any(c => c[0].Trim().Equals("noFilterLL", StringComparison.OrdinalIgnoreCase)
                    && c[1].Trim().Equals("true", StringComparison.OrdinalIgnoreCase));
                string[] Terms(string field) => clauses.FirstOrDefault(c => c[0].Trim().Equals(field, StringComparison.OrdinalIgnoreCase))?[1]
                    .Split(',', StringSplitOptions.RemoveEmptyEntries).Select(t => t.Trim()).Where(t => t != "none")
                    .Select(FoldEditorId).ToArray() ?? [];
                var filter = new EditorIdFilter(Terms("filterByEditorIdContains"), Terms("filterByEditorIdContainsOr"), Terms("filterByEditorIdContainsExcluded"));
                if (rule.Status == "npc-selector-out-of-scope" && !rule.Fields.Any(f =>
                    f.Equals("filterByLLs", StringComparison.OrdinalIgnoreCase) || f.Equals("noFilterLL", StringComparison.OrdinalIgnoreCase)
                    || f.StartsWith("filterByEditorId", StringComparison.OrdinalIgnoreCase))) continue;
                var targets = rule.References.Where(r => r.Field.Equals("filterByLLs", StringComparison.OrdinalIgnoreCase)
                    && r.Status == "resolved").Select(r => FormKey.Factory(r.FormKey!)).Where(lists.ContainsKey).ToArray();
                // Native runs the explicit pass first, then the all-item-list pass.
                // Keep overlap: it is a second dispatch with a fresh capacity check.
                if (broad) targets = targets.Concat(lists.Where(p => !p.Value.IsDeleted).Select(p => p.Key)).ToArray();
                if (filter.HasPositive) targets = targets.Concat(lists.Where(p => !p.Value.IsDeleted && filter.Matches(inputs.NativeListEditorIds.GetValueOrDefault(p.Key)))
                    .Select(p => p.Key)).ToArray();
                var uncertainTargets = rule.References.Where(r => r.Field.Equals("filterByLLs", StringComparison.OrdinalIgnoreCase)
                    && r.Status is "ambiguous-editorid" or "malformed-identifier").ToArray();
                if (uncertainTargets.Length > 0)
                {
                    // Historical aliases need not match any winning EditorID.
                    // Without a unique binding their footprint is unbounded.
                    Exclude(location, rule.Text, "ambiguous or malformed target");
                    continue;
                }
                var supported = rule.Status == "recognized" && rule.Fields.Count > 0 &&
                    rule.Fields.All(f => f.Equals("filterByLLs", StringComparison.OrdinalIgnoreCase) || f.Equals("addToLLs", StringComparison.OrdinalIgnoreCase)
                        || f.Equals("addOnceToLLs", StringComparison.OrdinalIgnoreCase) || f.Equals("noFilterLL", StringComparison.OrdinalIgnoreCase)
                        || f.Equals("filterByEditorIdContains", StringComparison.OrdinalIgnoreCase) || f.Equals("filterByEditorIdContainsOr", StringComparison.OrdinalIgnoreCase)
                        || f.Equals("filterByEditorIdContainsExcluded", StringComparison.OrdinalIgnoreCase) || f.Equals("formsToReplace", StringComparison.OrdinalIgnoreCase)
                        || f.Equals("removeFromLLs", StringComparison.OrdinalIgnoreCase) || f.Equals("removeObjectsByKeyword", StringComparison.OrdinalIgnoreCase)
                        || f.Equals("clear", StringComparison.OrdinalIgnoreCase) || ControlKeys.Contains(f, StringComparer.OrdinalIgnoreCase)
                        || f.Equals("objectMultCount", StringComparison.OrdinalIgnoreCase));
                if (!supported)
                {
                    var unsupported = $"SkyPatcher {location}: rule is not yet supported for distribution import ({string.Join(", ", rule.Fields)}).";
                    Exclude(location, rule.Text, unsupported);
                    continue;
                }
                var payloads = new List<(LeveledItemEntry Entry, bool Once)>();
                var replacements = new List<Replacement>();
                var removals = new List<Removal>();
                var keywordItems = new HashSet<FormKey>();
                var hasKeywordRemoval = false;
                // Native reads clear and list controls through its one case-sensitive
                // extractor. Another spelling is a no-op only by source evidence; do not guess.
                string? Exact(string key) => clauses.FirstOrDefault(c => c[0].Trim().Equals(key, StringComparison.Ordinal))?[1].Trim();
                var clear = Exact("clear") is "yes" or "true";
                string? problem = clauses.Select(c => c[0].Trim()).FirstOrDefault(key => CaseSensitiveKeys.Any(k =>
                    key.Equals(k, StringComparison.OrdinalIgnoreCase) && !key.Equals(k, StringComparison.Ordinal))) is { } spelled
                    ? $"Unverified SkyPatcher key spelling '{spelled}' at {location}; the supported release matches it case-sensitively." : null;
                var controls = ReadControls(Exact, rule, location, ref problem);
                var multipliers = ReadMultipliers(clauses, rule, location, ref problem);
                var removalClause = clauses.FirstOrDefault(c => c[0].Trim().Equals("removeFromLLs", StringComparison.OrdinalIgnoreCase));
                if (removalClause is not null)
                {
                    var references = rule.References.Where(r => r.Field.Equals("removeFromLLs", StringComparison.OrdinalIgnoreCase))
                        .GroupBy(r => r.Identifier, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
                    foreach (var text in removalClause[1].Split(','))
                    {
                        var parts = text.Split('~', StringSplitOptions.TrimEntries);
                        var reference = references.GetValueOrDefault(parts[0]);
                        if (reference?.Status is "missing-plugin" or "missing-record" or "deleted-record") continue;
                        if (reference?.Status != "resolved" || !TryRestriction(parts, 1, out var level) || !TryRestriction(parts, 2, out var count))
                        { problem = $"Unresolved or malformed SkyPatcher removal at {location}: {text}."; break; }
                        removals.Add(new(FormKey.Factory(reference.FormKey!), level, count));
                    }
                }
                foreach (var reference in rule.References.Where(r => r.Field.Equals("removeObjectsByKeyword", StringComparison.OrdinalIgnoreCase)))
                {
                    if (reference.Status is "missing-plugin" or "missing-record" or "deleted-record" or "wrong-type") continue;
                    if (reference.Status != "resolved" || !inputs.KeywordMembers.TryGetValue(FormKey.Factory(reference.FormKey!), out var members))
                    { problem = $"Unverified SkyPatcher keyword-removal evidence at {location}: {reference.Identifier}."; break; }
                    hasKeywordRemoval = true;
                    keywordItems.UnionWith(members);
                }
                var replacementClause = clauses.FirstOrDefault(c => c[0].Trim().Equals("formsToReplace", StringComparison.OrdinalIgnoreCase));
                if (replacementClause is not null)
                {
                    var references = rule.References.Where(r => r.Field.Equals("formsToReplace", StringComparison.OrdinalIgnoreCase))
                        .GroupBy(r => r.Identifier, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
                    foreach (var text in replacementClause[1].Split(','))
                    {
                        var pair = text.Split('~', StringSplitOptions.TrimEntries);
                        if (pair.Length != 2) { problem = $"Malformed SkyPatcher replacement at {location}: {text}."; break; }
                        var from = references.GetValueOrDefault(pair[0]); var to = references.GetValueOrDefault(pair[1]);
                        // User policy: an item absent from the load order is not part
                        // of the analysis, so its pair is dropped rather than blocking.
                        if (from?.Status is "missing-plugin" or "missing-record" or "deleted-record"
                            || to?.Status is "missing-plugin" or "missing-record" or "deleted-record") continue;
                        if (from?.Status != "resolved" || to?.Status != "resolved")
                        { problem = $"Unresolved SkyPatcher replacement at {location}: {text}."; break; }
                        replacements.Add(new(FormKey.Factory(from.FormKey!), FormKey.Factory(to.FormKey!)));
                    }
                }
                // Native patch() executes addToLLs before addOnceToLLs, regardless
                // of their textual order; capacity dispatch happens once per target.
                foreach (var operation in new[] { "addToLLs", "addOnceToLLs" })
                {
                    var addition = clauses.FirstOrDefault(c => c[0].Trim().Equals(operation, StringComparison.OrdinalIgnoreCase));
                    if (addition is null) continue;
                    var references = rule.References.Where(r => r.Field.Equals(operation, StringComparison.OrdinalIgnoreCase))
                        .GroupBy(r => r.Identifier, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
                    foreach (var text in addition[1].Split(','))
                    {
                        var parts = text.Split('~', StringSplitOptions.TrimEntries);
                        var reference = references.GetValueOrDefault(parts[0]);
                        if (parts.Length > 3 || !Number(parts, 1, out var level) || !Number(parts, 2, out var count) || count < 1)
                        { problem = $"Malformed or unsupported SkyPatcher payload at {location}: {text}."; break; }
                        if (reference?.Status is "missing-plugin" or "missing-record" or "deleted-record") continue;
                        if (reference?.Status != "resolved")
                        { problem = $"Unresolved SkyPatcher payload at {location}: {text}."; break; }
                        payloads.Add((new() { Data = new() { Reference = new FormLink<IItemGetter>(FormKey.Factory(reference.FormKey!)), Level = level, Count = count } }, operation == "addOnceToLLs"));
                    }
                    if (problem is not null) break;
                }
                if (problem is not null)
                {
                    Exclude(location, rule.Text, problem);
                    continue;
                }
                if (payloads.Count == 0 && replacements.Count == 0 && removals.Count == 0 && !hasKeywordRemoval && !clear
                    && !controls.Any && multipliers.Length == 0) continue;
                var program = new NativeRule(location, payloads.ToArray(), replacements.ToArray(), removals.ToArray(), keywordItems, hasKeywordRemoval, clear)
                    { Controls = controls, Multipliers = multipliers };
                // Stage the complete line before publishing any evidence or replay.
                // Repeated targets still see earlier dispatches of this same line.
                var staged = new List<StagedDispatch>();
                var stagedLists = new Dictionary<FormKey, ILeveledItemGetter>();
                var stagedAppends = new Dictionary<FormKey, int>();
                foreach (var target in targets)
                {
                    var prior = stagedLists.GetValueOrDefault(target) ?? lists[target];
                    var before = prior.Entries?.Count ?? 0;
                    if (before > 120)
                    {
                        staged.Add(new(target, prior, null, [], [], 0, 0, 0));
                        continue;
                    }
                    if (prior.IsDeleted || (prior.Entries?.Any(e => e.Data is null) ?? false))
                    { problem = $"Unsupported SkyPatcher list structure/capacity at {location}."; break; }
                    var changed = prior.DeepCopy();
                    changed.Entries ??= [];
                    var replaced = 0;
                    var removed = 0;
                    if (program.Erases || program.Replacements.Length > 0)
                    {
                        var changedEntries = Project(changed.Entries);
                        replaced = Replace(changedEntries, replacements);
                        removed = Erase(changedEntries, program);
                        if (replaced > 0 || removed > 0) changed.Entries = new(changedEntries.Select(e => e.Payload));
                    }
                    if (program.Controls.Flags is { } flags) changed.Flags = flags;
                    var entries = new List<LeveledItemEntry>();
                    var checks = new List<PresenceCheck>();
                    var priorAppends = (result.Appended.GetValueOrDefault(target)?.Count ?? 0) + stagedAppends.GetValueOrDefault(target);
                    var present = changed.Entries.Select(e => e.Data!.Reference.FormKey).ToHashSet();
                    var skipped = 0;
                    foreach (var (payload, once) in payloads)
                    {
                        var item = payload.Data!.Reference.FormKey;
                        if (once)
                        {
                            checks.Add(new(item, present.Contains(item), priorAppends + entries.Count));
                            if (present.Contains(item)) { skipped++; continue; }
                        }
                        changed.Entries.Add(payload.DeepCopy()); entries.Add(payload); present.Add(item);
                    }
                    if (changed.Entries.Count > 255)
                    { problem = $"Unsupported SkyPatcher list capacity at {location}."; break; }
                    // Global and Chance None follow the additions; count multipliers run last.
                    (program.Controls with { Flags = null }).Apply(changed);
                    if (program.Multipliers.Length > 0)
                    {
                        var counted = Project(changed.Entries);
                        try { Multiply(counted, program.Multipliers); }
                        catch (InvalidDataException) { problem = $"Unsupported SkyPatcher count multiplier at {location}."; break; }
                        changed.Entries = new(counted.Select(e => e.Payload));
                    }
                    changed.Entries = new(changed.Entries.OrderBy(e => e.Data!.Level));
                    stagedLists[target] = changed;
                    stagedAppends[target] = stagedAppends.GetValueOrDefault(target) + entries.Count;
                    staged.Add(new(target, prior, changed, entries.ToArray(), checks.ToArray(), replaced, removed, skipped));
                }
                if (problem is not null)
                {
                    Exclude(location, rule.Text, problem);
                    continue;
                }
                if (broad) result.broadRules.Add(program);
                if (filter.HasPositive) result.broadRules.Add(program with { Filter = filter });
                foreach (var dispatch in staged)
                {
                    var target = dispatch.Target;
                    var before = dispatch.Prior.Entries?.Count ?? 0;
                    result.Physical.TryAdd(target, dispatch.Prior);
                    if (!result.dispatches.TryGetValue(target, out var programs)) result.dispatches[target] = programs = [];
                    programs.Add(program);
                    if (dispatch.Changed is null)
                    {
                        result.MinimumDelta[target] = Math.Max(result.MinimumDelta.GetValueOrDefault(target, int.MinValue), 121 - before);
                        result.Notes.Add($"SkyPatcher {location}: {target} skipped at {before} entries (native limit 120).");
                        continue;
                    }
                    result.AdditionalCapacity[target] = Math.Min(result.AdditionalCapacity.GetValueOrDefault(target, 255), 120 - before);
                    if (!result.Appended.TryGetValue(target, out var entries)) result.Appended[target] = entries = [];
                    entries.AddRange(dispatch.Added);
                    if (dispatch.Checks.Length > 0)
                    {
                        if (!result.presenceChecks.TryGetValue(target, out var checks)) result.presenceChecks[target] = checks = [];
                        checks.AddRange(dispatch.Checks);
                    }
                    lists[target] = dispatch.Changed;
                    if (program.Controls.Any)
                        result.appliedControls[target] = result.appliedControls.TryGetValue(target, out var priorControls)
                            ? priorControls.Then(program.Controls) : program.Controls;
                    result.Notes.AddInformation($"SkyPatcher {location}: evaluated {dispatch.Replaced} replacements, {dispatch.Removed} removals and {dispatch.Added.Length} additions for {target}; skipped {dispatch.Skipped} addOnceToLLs payloads already present by FormKey.");
                }
            }
            if (exclusions.Count > 0 || inputs.Status == "incomplete-capture")
            {
                foreach (var exclusion in exclusions) result.Notes.AddInformation(exclusion);
                result.Notes.Add($"SkyPatcher analysis is partial: {exclusions.Count} line(s) excluded because XEP cannot prove them. Supported lines remain included. "
                    + (inputs.Status == "incomplete-capture" ? "Input capture is incomplete; uncaptured rules are also omitted. " : "")
                    + "Excluded lines may still change these results in game. See the report for every excluded line and reason.");
            }
            foreach (var key in result.Physical.Keys.Where(k => !result.Problems.ContainsKey(k))) result.effective[key] = lists[key];
            result.CaptureReplacementPeers();
            result.Notes.AddInformation($"SkyPatcher effective input: {result.effective.Count} lists interpreted; {result.Problems.Count} lists withheld because of unsupported dependencies.");
            return result;
        }

        // Capture-wide inability to interpret lines. Preserve resolution guards.
        private SkyPatcherLists WithoutImport(IReadOnlyList<string> reasons)
        {
            var result = new SkyPatcherLists { ImportDisabled = true };
            result.originalLists.UnionWith(originalLists);
            result.unavailablePlugins.UnionWith(unavailablePlugins);
            result.ReferencedEditorIds.UnionWith(ReferencedEditorIds);
            result.Notes.Add("SkyPatcher import unavailable: " + string.Join(" ", reasons)
                + " No lines can be interpreted under this capture contract; distribution uses plugin records.");
            return result;
        }

        private static readonly string[] ControlKeys = ["calcForLevel", "calcEachItem", "calcLevelAndEachItem", "calcUseAll", "clearFlags", "chanceGlobal", "chanceNone"];
        private static readonly string[] CaseSensitiveKeys = ["clear", .. ControlKeys];

        private static ListControls ReadControls(Func<string, string?> exact, SkyPatcherRule rule, string location, ref string? problem)
        {
            // Native assigns in this order; each later key replaces the flags.
            LeveledItem.Flag? flags = null;
            if (exact("calcForLevel") is "yes" or "true") flags = (LeveledItem.Flag)1;
            if (exact("calcEachItem") is "yes" or "true") flags = (LeveledItem.Flag)2;
            if (exact("calcLevelAndEachItem") is "yes" or "true") flags = (LeveledItem.Flag)3;
            // Use All and clearFlags act on any value other than none.
            if (exact("calcUseAll") is { } useAll && useAll != "none") flags = (LeveledItem.Flag)4;
            if (exact("clearFlags") is { } clearFlags && clearFlags != "none") flags = 0;
            var clearGlobal = false; var global = FormKey.Null;
            if (exact("chanceGlobal") is { } globalText && globalText != "none")
            {
                if (globalText.Equals("null", StringComparison.OrdinalIgnoreCase)) clearGlobal = true;
                else
                {
                    var reference = rule.References.FirstOrDefault(r => r.Field.Equals("chanceGlobal", StringComparison.Ordinal));
                    // A missing or non-global form leaves the native global unchanged.
                    if (reference?.Status == "resolved") global = FormKey.Factory(reference.FormKey!);
                    else if (reference?.Status is not ("missing-plugin" or "missing-record" or "deleted-record" or "wrong-type"))
                        problem ??= $"Unresolved SkyPatcher chanceGlobal at {location}: {globalText}.";
                }
            }
            Percent? chanceNone = null;
            if (exact("chanceNone") is { } chanceText && chanceText != "none")
            {
                // stoi reads a leading decimal prefix; native stores it as int8.
                var digits = Regex.Match(chanceText, @"^[+-]?[0-9]+").Value;
                if (!int.TryParse(digits, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var chance) || chance is < 0 or > 100)
                    problem ??= $"Unsupported SkyPatcher chanceNone at {location}: {chanceText}.";
                else chanceNone = new Percent(chance / 100.0);
            }
            return new(flags, clearGlobal, global, chanceNone);
        }

        private static CountMultiplier[] ReadMultipliers(string[][] clauses, SkyPatcherRule rule, string location, ref string? problem)
        {
            var clause = clauses.FirstOrDefault(c => c[0].Trim().Equals("objectMultCount", StringComparison.OrdinalIgnoreCase));
            if (clause is null) return [];
            var references = rule.References.Where(r => r.Field.Equals("objectMultCount", StringComparison.OrdinalIgnoreCase))
                .GroupBy(r => r.Identifier, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
            var result = new List<CountMultiplier>();
            foreach (var text in clause[1].Split(','))
            {
                var parts = text.Split('~', StringSplitOptions.TrimEntries);
                var reference = references.GetValueOrDefault(parts[0]);
                if (parts.Length != 2
                    || !float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var factor)
                    || !float.IsFinite(factor) || factor < 0)
                { problem ??= $"Malformed SkyPatcher count multiplier at {location}: {text}."; break; }
                // Native skips a multiplier whose form is not loaded.
                if (reference?.Status is "missing-plugin" or "missing-record" or "deleted-record") continue;
                if (reference?.Status != "resolved") { problem ??= $"Unresolved SkyPatcher count multiplier at {location}: {text}."; break; }
                result.Add(new(FormKey.Factory(reference.FormKey!), factor));
            }
            return result.ToArray();
        }

        private static bool Number(string[] parts, int index, out short value)
        {
            value = 1;
            return parts.Length <= index || short.TryParse(parts[index], NumberStyles.None, CultureInfo.InvariantCulture, out value);
        }

        private static bool TryRestriction(string[] parts, int index, out Restriction? restriction)
        {
            restriction = null;
            if (parts.Length <= index || !Regex.IsMatch(parts[index], "[0-9]")) return true;
            // Match splitRelationNumber and stoi's accepted decimal prefix. Native
            // stores the resulting int in uint16_t before comparing list values.
            var relation = Regex.Match(parts[index], @"([<>]=?)\s*([0-9]+)");
            var number = relation.Success ? relation.Groups[2].Value : Regex.Match(parts[index], @"^[+-]?[0-9]+").Value;
            if (!int.TryParse(number, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value)) return false;
            restriction = new(relation.Success ? relation.Groups[1].Value : "", unchecked((ushort)value));
            return true;
        }

        private static bool HasIrrelevantWrites(string category, string text)
        {
            // Source contract 876662e5: race.cpp 46-55/543-800 (attack data),
            // cell.cpp 775-800 (audio), faction.cpp 210-320 (individual crime
            // bits), npc.cpp 673-683 (aggression). None writes facts consumed
            // by XEP. Keep the captured rules; do not interpret target success.
            category = category.ToLowerInvariant();
            if (category == "cell" && IsCellSelectorNoOp(text)) return true;
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var clause in text.Split(':'))
            {
                var pair = clause.Split('=', 2);
                if (pair.Length != 2) return false;
                var field = pair[0].Trim().ToLowerInvariant();
                var value = pair[1].Trim();
                if (value.Length == 0 || !seen.Add(field)) return false;
                // Native examines every '='. Only the attack-only category
                // permits nested assignments (its documented attack syntax).
                if (category != "racehook" && value.Contains('=')) return false;
                var irrelevant = category switch
                {
                    "racehook" => field is "filterbyraces" or "filterbyracesexcluded" or "filterbykeywords"
                        or "filterbykeywordsor" or "filterbykeywordsexcluded" or "attackdatatoadd" or "attackdatatochange" or "attackdatatoremove",
                    "cell" => field is "filterbycells" or "acousticspace",
                    "faction" => field is "filterbyfactions" or "filterbyfactionsexcluded" or "filterbymodnames"
                        || field is "setflags" or "removeflags" && value.Split(',').All(flag => flag.Trim().ToLowerInvariant() is
                            "ignorescrimes_murder" or "ignorescrimes_assult" or "ignorescrimes_stealing" or "ignorescrimes_trespass"
                            or "ingorescrimes_trespass" or "ignorescrimes_pickpocket" or "ignorescrimes_werewolf"),
                    "npc" => field is "filterbynpcs" or "filterbynpcsexcluded" or "setaggression",
                    _ => false
                };
                if (!irrelevant) return false;
            }
            return seen.Count > 0;
        }

        private static bool IsCellSelectorNoOp(string text)
        {
            // General Information: ':' separates assignments; this trailing
            // qualified reference is not an operation. Do not repair it into a selector.
            const string form = @"[^,~=:|\s]+\.(?i:esm|esp|esl)\|[0-9a-fA-F]{1,8}";
            return Regex.IsMatch(text, @"^filterByCells=\s*" + form + @"\s*:\s*" + form + @"\s*$", RegexOptions.CultureInvariant);
        }

        public static bool CategoryMayRun(SkyPatcherInputReport inputs, string category)
        {
            if (!inputs.CapturedBytes.TryGetValue("SkyPatcher.ini", out var bytes)) return true;
            using var reader = new StreamReader(new MemoryStream(bytes.ToArray()), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            var inPatcher = false;
            while (reader.ReadLine() is { } raw)
            {
                var line = raw.Trim();
                if (line.StartsWith(';') || line.Length == 0) continue;
                if (line.StartsWith('[') && line.EndsWith(']')) { inPatcher = line[1..^1].Equals("Patcher", StringComparison.OrdinalIgnoreCase); continue; }
                var pair = line.Split('=', 2);
                if (inPatcher && pair.Length == 2 && pair[0].Trim().Equals($"iEnable{category}Patching", StringComparison.OrdinalIgnoreCase))
                    return !int.TryParse(pair[1].Trim(), out var enabled) || enabled != 0;
            }
            return false;
        }
    }

    // SkyPatcher runs after plugins load. A planned entry that a later native
    // operation would erase, rewrite or move across a dispatch boundary is a
    // refused placement, like any other unsupported destination; it must not
    // fail the whole run at write time. Withdrawal only removes additions.
    // Returns whether any plan, edge or rewrite was withdrawn.
    // A generated list has content when it holds an entry, a planned placement, or a
    // generated child (entry or fallback edge) that has content.
    // Only additions to an unrewritten native list can be removed as standalone
    // attachments; other edges may carry authored content or route structure.
    private static bool NativeAdditionAttachment(FallbackEdge edge, Catalog catalog) => catalog.LlosLists?.IsGenerated(edge.Parent.List) == true
        || catalog.WinningLists.ContainsKey(edge.Parent.List)
        && !catalog.SpecializedRewrites.ContainsKey(edge.Parent.List) && !catalog.Fallbacks.ContainsKey(edge.Parent.List)
        && !catalog.RebuiltFallbackRoots.Contains(edge.Parent.List);

    private static bool DetachEmptyBranches(PlanningAdditions<VariantPlan> plans, Catalog catalog, HashSet<FormKey> emptiedParents)
    {
        var planned = plans.SelectMany(p => p.Parents).Select(p => p.List).ToHashSet();
        var edges = catalog.FallbackEdges.ToLookup(e => e.Parent.List, e => e.Child);
        var memo = new Dictionary<FormKey, bool>();
        bool Content(FormKey key)
        {
            if (memo.TryGetValue(key, out var known)) return known;
            memo[key] = false; // cycle guard
            var result = planned.Contains(key)
                || (catalog.Lists.GetValueOrDefault(key)?.Entries ?? []).Any(e => e.Data is { } data
                    && (!catalog.Fallbacks.ContainsKey(data.Reference.FormKey) || Content(data.Reference.FormKey)))
                || edges[key].Any(child => !catalog.Fallbacks.ContainsKey(child) || Content(child));
            return memo[key] = result;
        }
        var changed = false;
        // Walk only from families that lost placements. Pre-existing empty
        // leaves may encode windows or switches and must not be deleted.
        var empty = emptiedParents.Where(k => catalog.Fallbacks.ContainsKey(k) && !Content(k)).ToHashSet();
        var pending = new Queue<FormKey>(empty);
        var incoming = catalog.FallbackEdges.ToLookup(e => e.Child);
        while (pending.TryDequeue(out var child))
        {
            foreach (var edge in incoming[child])
            {
                var parent = edge.Parent.List;
                if (!catalog.Fallbacks.ContainsKey(parent) && !NativeAdditionAttachment(edge, catalog)) continue;
                catalog.FallbackEdges.Remove(edge);
                catalog.DistributionNotes.Add($"{catalog.Lists[child].EditorID} [{child}]: empty added family detached after native withdrawal.");
                changed = true;
                // Removing a child with no content cannot change Content's memo.
                if (catalog.Fallbacks.ContainsKey(parent) && !Content(parent) && empty.Add(parent)) pending.Enqueue(parent);
            }
            foreach (var route in catalog.RuntimePlacements.Where(r => r.Child == child).ToArray())
            {
                catalog.RuntimePlacements.Remove(route);
                catalog.DistributionNotes.Add($"{route.Source}: empty runtime family withheld after native withdrawal.");
                changed = true;
            }
        }
        void Detach<TKey>(IDictionary<TKey, FormKey> roots, string consumer) where TKey : notnull
        {
            foreach (var (key, root) in roots.Where(p => catalog.Fallbacks.ContainsKey(p.Value) && !Content(p.Value)).ToArray())
            {
                catalog.DistributionNotes.Add($"{catalog.Lists[root].EditorID} [{root}]: {consumer} {key} keeps its original reference. "
                    + "Withdrawing natively refused placements left its isolated XEP branch empty.");
                roots.Remove(key);
                changed = true;
            }
        }
        var loot = catalog.LootRecipientRewrites.ToDictionary(p => p.Key, p => p.Value.Root);
        Detach(loot, "recipient");
        foreach (var key in catalog.LootRecipientRewrites.Keys.Where(k => !loot.ContainsKey(k)).ToArray()) catalog.LootRecipientRewrites.Remove(key);
        var inventory = catalog.MerchantInventoryRewrites.ToDictionary(p => p.Key, p => p.Value.Root);
        Detach(inventory, "merchant inventory");
        foreach (var key in catalog.MerchantInventoryRewrites.Keys.Where(k => !inventory.ContainsKey(k)).ToArray()) catalog.MerchantInventoryRewrites.Remove(key);
        Detach(catalog.MerchantContainerRoots, "merchant container");
        return changed;
    }

    private static bool WithdrawNativeConflicts(PlanningAdditions<VariantPlan> plans, Catalog catalog)
    {
        var withdrawn = new HashSet<FormKey>();
        var emptiedParents = new HashSet<FormKey>();
        var modified = false;
        while (EffectiveStaticEntries.NativeCompositionProblems(catalog, plans) is { Count: > 0 } problems)
        {
            var copies = catalog.SpecializedCloneOriginals.ToLookup(p => p.Value, p => p.Key);
            IEnumerable<FormKey> Targets(FormKey list)
            {
                var contents = MaterialContents(list, catalog);
                return InheritedCopyTargets(contents, copies, catalog).Prepend(contents);
            }
            string Name(FormKey key) => $"{catalog.Lists[key].EditorID} [{key}]";
            var changed = false;
            for (var i = 0; i < plans.Count; i++)
            {
                var plan = plans[i];
                var refused = plan.Parents.SelectMany(p => Targets(p.List)).Where(problems.ContainsKey).Distinct().ToArray();
                if (refused.Length == 0) continue;
                var parents = plan.Parents.Where(p => !Targets(p.List).Any(problems.ContainsKey)).ToArray();
                foreach (var key in refused)
                    catalog.DistributionNotes.Add($"{plan.Source.FormKey}: placement into {Name(key)} withheld. {problems[key]}");
                plans[i] = plan with { Parents = parents, PlacementReason = parents.Length == 0
                    ? "Native startup changes (SkyPatcher or LLOS) would erase or rewrite this placement" : plan.PlacementReason };
                foreach (var parent in plan.Parents.Except(parents))
                    foreach (var key in Targets(parent.List).Prepend(parent.List))
                        if (catalog.Fallbacks.ContainsKey(key)) emptiedParents.Add(key);
                withdrawn.Add(plan.Source.FormKey);
                changed = true;
            }
            bool Refused(FallbackEdge edge) => problems.ContainsKey(edge.Child) || problems.ContainsKey(edge.Parent.List)
                || InheritableEdge(edge, catalog) && InheritedCopyTargets(edge.Parent.List, copies, catalog).Any(problems.ContainsKey);
            foreach (var edge in catalog.FallbackEdges.Where(e => NativeAdditionAttachment(e, catalog) && Refused(e)).ToArray())
            {
                var key = problems.ContainsKey(edge.Child) ? edge.Child : edge.Parent.List;
                catalog.DistributionNotes.Add($"{Name(edge.Child)}: attachment to {Name(edge.Parent.List)} withheld. "
                    + (problems.GetValueOrDefault(key) ?? "Native startup changes (SkyPatcher or LLOS) would erase or rewrite an inherited copy."));
                catalog.FallbackEdges.Remove(edge);
                changed = true;
            }
            // A generated copy may be a consumer's rewritten root. Restoring that
            // consumer's original reference removes only XEP's isolated branch.
            // That is the last resort: withdrawing the refused placements and
            // attachments usually clears the problem, so a branch is detached only
            // in a round where nothing narrower changed (problems are recomputed).
            if (!changed && problems.Keys.Any(catalog.Fallbacks.ContainsKey))
            {
                // A root is erased when it, or a generated list it reaches through generated
                // children, has a problem. Computed once per pass, backwards from the
                // problems, instead of a forward search (with an edge scan) per root.
                HashSet<FormKey>? erased = null;
                bool Erased(FormKey root)
                {
                    if (erased is null)
                    {
                        var parents = new Dictionary<FormKey, List<FormKey>>();
                        void Link(FormKey parent, FormKey child)
                        {
                            if (!catalog.Fallbacks.ContainsKey(child)) return;
                            if (!parents.TryGetValue(child, out var known)) parents[child] = known = [];
                            known.Add(parent);
                        }
                        var origins = catalog.Fallbacks.Keys.Concat(catalog.LootRecipientRewrites.Values.Select(r => r.Root))
                            .Concat(catalog.MerchantInventoryRewrites.Values.Select(r => r.Root)).ToHashSet();
                        foreach (var key in origins)
                            foreach (var entry in catalog.Lists.GetValueOrDefault(key)?.Entries ?? [])
                                if (entry.Data is { } data) Link(key, data.Reference.FormKey);
                        foreach (var edge in catalog.FallbackEdges) Link(edge.Parent.List, edge.Child);
                        erased = problems.Keys.ToHashSet();
                        var todo = new Queue<FormKey>(erased);
                        while (todo.TryDequeue(out var key))
                            foreach (var parent in parents.GetValueOrDefault(key, []))
                                if (erased.Add(parent)) todo.Enqueue(parent);
                    }
                    return erased.Contains(root);
                }
                void Detach<TKey>(Dictionary<TKey, FormKey> roots, string consumer) where TKey : notnull
                {
                    foreach (var (key, root) in roots.Where(p => Erased(p.Value)).ToArray())
                    {
                        catalog.DistributionNotes.Add($"{Name(root)}: {consumer} {key} keeps its original reference. "
                            + "Native startup changes (SkyPatcher or LLOS) would erase or rewrite its isolated XEP branch.");
                        roots.Remove(key);
                        changed = true;
                    }
                }
                var loot = catalog.LootRecipientRewrites.ToDictionary(p => p.Key, p => p.Value.Root);
                Detach(loot, "recipient");
                foreach (var key in catalog.LootRecipientRewrites.Keys.Where(k => !loot.ContainsKey(k)).ToArray()) catalog.LootRecipientRewrites.Remove(key);
                var inventory = catalog.MerchantInventoryRewrites.ToDictionary(p => p.Key, p => p.Value.Root);
                Detach(inventory, "merchant inventory");
                foreach (var key in catalog.MerchantInventoryRewrites.Keys.Where(k => !inventory.ContainsKey(k)).ToArray()) catalog.MerchantInventoryRewrites.Remove(key);
                Detach(catalog.MerchantContainerRoots, "merchant container");
            }
            // Anything left cannot be withdrawn without removing authored content;
            // the writer's own refusal remains the boundary for it.
            if (!changed) break;
            modified = true;
            PruneUnreachableFallbacks(plans, catalog, []);
        }
        // Withdrawing placements can leave an isolated consumer branch with nothing in
        // it; such a consumer keeps its original reference instead of an empty copy.
        if (withdrawn.Count > 0 && DetachEmptyBranches(plans, catalog, emptiedParents)) PruneUnreachableFallbacks(plans, catalog, []);
        foreach (var source in withdrawn.Where(s => !plans.Any(p => p.Source.FormKey == s && p.Parents.Count > 0)))
            foreach (var plan in plans.Where(p => p.Source.FormKey == source))
                plan.Candidate.Reason = "Native startup changes (SkyPatcher or LLOS) would erase or rewrite every planned placement; variants withheld";
        return modified;
    }
}
