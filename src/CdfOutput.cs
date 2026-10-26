using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;

namespace XEnchantmentPatcher;

public static partial class PatcherEngine
{
    private sealed record CdfOutput(string RelativePath, string Json, IReadOnlySet<FormKey> Roots);
    private static string CdfOutputPath(string requestedOutput) => "SKSE/Plugins/ContainerDistributionFramework/"
        + Path.GetFileNameWithoutExtension(requestedOutput) + ".CDF.json";

    private static CdfOutput? BuildCdfOutput(Catalog catalog, IReadOnlyDictionary<FormKey, FormKey> remapped,
        string requestedOutput, IReadOnlyList<string> outputPaths)
    {
        if (catalog.CdfOutputRoots.Count == 0) return null;
        using var timing = PhaseTiming.Measure("cdf.output");
        var rules = new JsonArray(); var roots = new HashSet<FormKey>();
        string Identifier(FormKey key) => $"0x{key.ID:X6}|{key.ModKey.FileName}";
        foreach (var output in catalog.CdfOutputRoots.GroupBy(o => o.Root))
        {
            // Recipients sharing a root share one authored change, so one rule lists them
            // all; never broaden beyond the admitted recipients.
            var use = output.First().Source;
            var root = remapped[output.Key]; roots.Add(root);
            var conditions = JsonNode.Parse(use.Conditions, documentOptions: new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true })!.AsObject();
            conditions[use.Reference ? "references" : "containers"] = new JsonArray(output.Select(o => o.Source.Consumer).Distinct().Order()
                .Select(consumer => (JsonNode?)JsonValue.Create(Identifier(consumer))).ToArray());
            var plugins = (conditions["plugins"] as JsonArray)?.Select(n => n!.GetValue<string>()) ?? [];
            conditions["plugins"] = new JsonArray(plugins.Concat(outputPaths.Select(Path.GetFileName)!)
                .Distinct(StringComparer.OrdinalIgnoreCase).Select(p => (JsonNode?)JsonValue.Create(p)).ToArray());
            rules.Add(new JsonObject { ["friendlyName"] = "XEP " + use.Provenance, ["conditions"] = conditions,
                ["changes"] = new JsonArray(new JsonObject { ["add"] = new JsonArray(Identifier(root)), ["count"] = use.Count }) });
        }
        return new(CdfOutputPath(requestedOutput), new JsonObject { ["rules"] = rules }.ToJsonString(), roots);
    }

    private static string? OwnedCdfFile(string requestedOutput)
    {
        var manifestPath = Path.ChangeExtension(requestedOutput, ".manifest.json");
        if (!File.Exists(manifestPath)) return null;
        var manifest = JsonSerializer.Deserialize(AtomicFile.ReadText(manifestPath), JsonContext.Default.OutputManifest);
        var relative = CdfOutputPath(requestedOutput);
        // Only this exact reserved path is accepted, even from a modified manifest.
        // Manifests written before CDF support have no CdfFiles entry at all.
        return manifest?.CdfFiles?.Contains(relative, StringComparer.Ordinal) == true
            ? Path.Combine(Path.GetDirectoryName(requestedOutput)!, relative) : null;
    }

    private static void StageCdfOutput(CdfOutput? output, string requestedOutput, Func<string, string> stageSupport, IReadOnlyList<string> stagedPlugins)
    {
        if (output is null) return;
        var target = Path.Combine(Path.GetDirectoryName(requestedOutput)!, output.RelativePath);
        if (File.Exists(target) && OwnedCdfFile(requestedOutput) != target)
            throw new InvalidDataException("CDF output path is occupied by an unowned file; no authored configuration was overwritten: " + target);
        var present = new HashSet<FormKey>();
        foreach (var path in stagedPlugins)
        {
            using var mod = SkyrimMod.CreateFromBinaryOverlay(path, SkyrimRelease.SkyrimSE);
            present.UnionWith(mod.LeveledItems.Select(l => l.FormKey));
        }
        if (!output.Roots.IsSubsetOf(present)) throw new InvalidDataException("CDF generated payload did not survive plugin serialization.");
        File.WriteAllText(stageSupport(output.RelativePath), output.Json);
    }
}
