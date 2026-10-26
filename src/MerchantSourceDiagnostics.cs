using System.IO;
using System.Text;
using System.Text.Json;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;

namespace XEnchantmentPatcher;

public static partial class PatcherEngine
{
    // Read-only diagnostic using the same winning catalog and script analysis
    // as production. Source acceptance is separate from destination planning.
    public static void WriteMerchantSourcePreflight(AppSettings settings, string path)
    {
        using var session = PhaseTiming.Begin("merchant-source-preflight");
        using var game = LoadOrderProvider.Load(settings, OutputFileName);
        File.WriteAllText(path, MerchantSourcePreflightLoaded(settings, game.Mods));
    }

    private static string MerchantSourcePreflightLoaded(AppSettings settings, IReadOnlyList<ISkyrimModGetter> mods)
    {
        var catalog = BuildCatalogForProfile(mods, settings.CurrentProfile);
        InitializeAvailability(settings, catalog);
        var graph = SpecializedGraph(catalog);
        var sources = settings.CurrentProfile.SpecializedLootOverrides
            .Where(p => p.Value == SpecializedLootMode.SpecializedEnchantedLists)
            .Select(p => FormKey.Factory(p.Key)).Where(k => catalog.Weapons.ContainsKey(k) || catalog.Armors.ContainsKey(k))
            .Where(k => settings.CurrentProfile.ItemOverrides.GetValueOrDefault(k.ToString(), true)).Order().ToHashSet();
        if (sources.Count > 1024) throw new InvalidOperationException("Merchant preflight exceeds 1024 explicitly selected sources.");
        using var stream = new MemoryStream();
        using var json = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });
        void Strings(string name, IEnumerable<string> values)
        {
            json.WriteStartArray(name);
            foreach (var value in values.Distinct().Take(1024)) json.WriteStringValue(value.Length > 4096 ? value[..4096] + " [truncated]" : value);
            json.WriteEndArray();
        }
        json.WriteStartObject(); json.WriteNumber("SchemaVersion", 2);
        json.WriteString("Scope", "Production source preflight; no destination selection, flavor allocation, plugin write or runtime execution. Potential closures from rejected paths are diagnostics only.");
        json.WriteBoolean("AvailabilityInitialized", catalog.AvailabilityInitialized);
        json.WriteStartArray("RuntimeBaseScriptEvidence");
        foreach (var (name, script) in catalog.RuntimeBaseScripts.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            json.WriteStartObject(); json.WriteString("Name", name); json.WriteBoolean("Available", script.Text is not null);
            json.WriteString("TextSha256", script.Text is null ? null : Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(script.Text))));
            var evidence = script.Evidence ?? "";
            json.WriteString("Evidence", evidence.Length > 4096 ? evidence[..4096] + " [truncated]" : evidence);
            json.WriteString("DiagnosticAssetEvidence", catalog.RuntimeBaseScriptDiagnosticEvidence.GetValueOrDefault(name));
            json.WriteEndObject();
        }
        json.WriteEndArray();
        WriteWinningRuntimeHolderEvidence(json, catalog);
        Strings("SelectedSourceUnion", sources.Order().Select(k => k.ToString()));
        var envelopes = new List<CountedSourceEnvelope>();
        json.WriteStartArray("Sources");
        foreach (var source in sources.Order())
        {
            json.WriteStartObject(); json.WriteString("Source", source.ToString());
            var sourceProblem = catalog.UnsafeAvailability.GetValueOrDefault(source)
                ?? (catalog.ScriptUnlocks.ContainsKey(source) ? "Source requires a runtime unlock; static counted stock is unsupported." : null);
            json.WriteString("AvailabilityProblem", sourceProblem);
            var evidence = new List<string>();
            var routes = sourceProblem is null ? MerchantSourceRoutes(source, catalog, evidence, true) : [];
            Strings("SourceEvidence", evidence);
            json.WriteStartArray("ProvedSourcePaths");
            foreach (var route in routes)
            {
                json.WriteStartObject(); json.WriteString("Container", route.Container.ToString());
                json.WriteNumber("InventoryIndex", route.InventoryIndex);
                if (route.Proof.Availability.Structure is { } envelope)
                {
                    envelopes.Add(envelope);
                    json.WriteString("KernelRoot", envelope.KernelRoot.ToString()); json.WriteString("Scope", envelope.ProjectionScope);
                    json.WriteString("SamplingIdentity", envelope.SamplingIdentity);
                    Strings("OmittedSourceBranches", envelope.OmittedSourceBranches);
                    Strings("OmittedRuntimeTargets", envelope.OmittedRuntimeTargets.Select(k => k.ToString()));
                    Strings("RuntimeDisjointEvidence", envelope.RuntimeDisjointEvidence);
                }
                json.WriteEndObject();
            }
            json.WriteEndArray(); json.WriteEndObject();
        }
        json.WriteEndArray();
        // Inspect possible counted children even when a source path failed
        // early. These are NOT promoted to accepted or retained dependencies.
        var potentialRoots = sources.SelectMany(graph.Up).Distinct().Where(graph.Lists.ContainsKey)
            .SelectMany(k => (graph.Lists[k].Entries ?? []).Where(e => e.Data is { Count: > 1 })
                .Select(e => e.Data!.Reference.FormKey)).Where(graph.Lists.ContainsKey).Distinct().Order().ToArray();
        var retainedRoots = envelopes.Select(e => e.KernelRoot).Distinct().ToHashSet();
        var visited = new Dictionary<FormKey, bool>(); var pending = new Queue<(FormKey Key, bool Retained, int Depth)>();
        foreach (var root in retainedRoots.Order().Union(potentialRoots)) pending.Enqueue((root, retainedRoots.Contains(root), 0));
        var boundProblem = "";
        while (pending.TryDequeue(out var current))
        {
            if (visited.TryGetValue(current.Key, out var retained) && (retained || !current.Retained)) continue;
            if (current.Depth > 16 || visited.Count >= 8192) { boundProblem = "Dependency closure exceeded 16 edges or 8192 states; incomplete diagnostic."; break; }
            if (!graph.Lists.TryGetValue(current.Key, out var list)) continue;
            visited[current.Key] = current.Retained || retained;
            foreach (var child in (list.Entries ?? []).Where(e => e.Data is not null).Select(e => e.Data!.Reference.FormKey)
                .Concat(catalog.RuntimeAddPayloads.GetValueOrDefault(current.Key, []).Select(p => p.Payload)))
                pending.Enqueue((child, current.Retained, current.Depth + 1));
        }
        json.WriteString("DependencyBoundProblem", boundProblem);
        json.WriteStartArray("ListDependencies");
        foreach (var (key, retained) in visited.OrderBy(p => p.Key))
        {
            var list = graph.Lists[key];
            json.WriteStartObject(); json.WriteString("List", key.ToString()); json.WriteString("EditorID", list.EditorID);
            json.WriteString("Role", retained ? "Retained authored or possible-runtime closure" : "Potential counted-child closure; source acceptance not established");
            var bearing = sources.Any(source => SourceKernelContains(key, source, catalog));
            json.WriteBoolean("AuthoredSourceBearing", bearing);
            json.WriteNumber("Flags", (int)list.Flags); json.WriteNumber("ChanceNone", list.ChanceNone.Value);
            json.WriteString("Global", list.Global.FormKey.ToString());
            json.WriteString("RuntimeControlProblem", catalog.RuntimeControlledLists.GetValueOrDefault(key));
            json.WriteString("RuntimeBindingProblem", catalog.RuntimeAddPayloadProblems.GetValueOrDefault(key));
            json.WriteStartArray("Entries");
            foreach (var (entry, index) in (list.Entries ?? []).Select((e, i) => (e, i)))
            {
                json.WriteStartObject(); json.WriteNumber("Index", index);
                if (entry.Data is { } data)
                { json.WriteString("Reference", data.Reference.FormKey.ToString()); json.WriteNumber("Level", data.Level); json.WriteNumber("Count", data.Count); }
                json.WriteBoolean("HasExtraData", entry.ExtraData is not null); json.WriteEndObject();
            }
            json.WriteEndArray();
            json.WriteStartArray("PossibleAddFormPayloads");
            foreach (var payload in catalog.RuntimeAddPayloads.GetValueOrDefault(key, []))
            { json.WriteStartObject(); json.WriteString("Payload", payload.Payload.ToString()); json.WriteString("Evidence", payload.Evidence); json.WriteEndObject(); }
            json.WriteEndArray();
            if (!bearing && SourceListHasRuntimeMutation(key, catalog))
            {
                var problem = RuntimeDisjointSourceBranch(key, sources, catalog, out var proof);
                json.WriteString("FullUnionDisjointProblem", problem); json.WriteString("FullUnionDisjointEvidence", proof);
            }
            json.WriteEndObject();
        }
        json.WriteEndArray();
        json.WriteStartArray("JointSourceEvents");
        foreach (var group in envelopes.GroupBy(e => e.SamplingIdentity).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var problem = TryJointCountedSourceEnvelope(group.DistinctBy(e => e.Source).ToArray(), catalog, out var joint);
            json.WriteStartObject(); json.WriteString("SamplingIdentity", group.Key);
            Strings("Sources", group.Select(e => e.Source.ToString())); json.WriteString("Problem", problem);
            if (joint is not null) { json.WriteString("Identity", joint.Identity); Strings("RuntimeDisjointEvidence", joint.RuntimeDisjointEvidence); }
            json.WriteEndObject();
        }
        json.WriteEndArray();
        // Source proofs can close exact property obligations on demand. Export
        // their supplemental checks after all source/joint traversals finish.
        WriteRuntimeScriptEvidence(json, catalog);
        Strings("RuntimeNativeFrameProblems", catalog.RuntimeNativeContractProblems.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => p.Key + ": " + p.Value));
        json.WriteStartArray("RuntimeFrameClosureProblems");
        foreach (var (quest, problem) in catalog.RuntimeFrameClosureProblems.OrderBy(p => p.Key))
        {
            json.WriteStartObject(); json.WriteString("Quest", quest.ToString());
            json.WriteString("Problem", problem.Length > 4096 ? problem[..4096] + " [truncated]" : problem); json.WriteEndObject();
        }
        json.WriteEndArray();
        json.WriteEndObject(); json.Flush();
        return Encoding.UTF8.GetString(stream.ToArray());
    }
}
