using System.Text.Json;
using System.Text;
using System.Globalization;

namespace XEnchantmentPatcher;

internal static partial class CdfInputs
{
    private static CdfSettings DescribeSettings(byte[] bytes)
    {
        var general = false;
        var values = new List<string>();
        foreach (var raw in Encoding.UTF8.GetString(bytes).TrimStart('\uFEFF').Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith(';') || line.StartsWith('#') || line.Length == 0) continue;
            if (line.StartsWith('[')) { general = line.Equals("[General]", StringComparison.OrdinalIgnoreCase); continue; }
            var equals = line.IndexOf('=');
            if (general && equals >= 0 && line[..equals].Trim().Equals("fMaxRefLookupDistance", StringComparison.OrdinalIgnoreCase))
                values.Add(line[(equals + 1)..].Trim());
        }
        if (values.Count == 0) return new("default", null, 25000);
        var value = values[^1];
        // Do not emulate strtod extensions, multiline SimpleIni values or duplicate
        // settings. Preserve them as unsupported instead of inventing an effective value.
        if (values.Count != 1) return new("unsupported-syntax", value, null);
        if (value.Length == 0) return new("default-empty", value, 25000);
        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var distance) && double.IsFinite(distance))
            return new("captured-finite", value, Math.Clamp(distance, 10000, 150000));
        return new("unsupported-syntax", value, null);
    }

    // These are pinned-source diagnostics, never execution or placement evidence.
    // In particular, loaded-record resolution cannot reproduce runtime EditorID
    // caches, dynamic references, actor values or native numeric conversions.
    private static bool Present(JsonElement parent, string name, out JsonElement value)
    {
        value = default;
        return parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(name, out value)
            && value.ValueKind != JsonValueKind.Null;
    }

    private static (string Disposition, CdfGuards? Guards, IReadOnlyList<CdfChange> Changes) DescribeNative(
        JsonElement rule, Resolver resolver, List<CdfReference> references, List<string> notes, ref bool stopped)
    {
        if (stopped) return ("skipped-file-tail", null, []);
        if (rule.ValueKind is not (JsonValueKind.Object or JsonValueKind.Null))
        {
            notes.Add("Pinned JsonCpp object lookup can throw for this rule; remaining native execution is unresolved.");
            return ("native-error-risk", null, []);
        }
        if (!Present(rule, "friendlyName", out var name) || name.ValueKind != JsonValueKind.String
            || !Present(rule, "changes", out var changes) || changes.ValueKind != JsonValueKind.Array)
        {
            stopped = true;
            return ("stops-file-invalid-envelope", null, []);
        }
        var bypass = false; var vendors = false; var random = false;
        var disposition = "unsupported-effect";
        if (Present(rule, "conditions", out var conditions))
        {
            if (conditions.ValueKind != JsonValueKind.Object)
            {
                notes.Add("Non-null conditions must be an object: native object lookup can throw, not cleanly skip the rule.");
                return ("native-error-risk", null, []);
            }
            if (Present(conditions, "plugins", out var plugins))
            {
                if (plugins.ValueKind != JsonValueKind.Array || plugins.EnumerateArray().Any(p => p.ValueKind != JsonValueKind.String))
                { stopped = true; return ("stops-file-invalid-plugins", null, []); }
                if (plugins.EnumerateArray().Any(p => !resolver.HasPlugin(p.GetString()!)))
                { stopped = true; return ("stops-file-missing-plugin", null, []); }
            }
            foreach (var field in new[] { "bypassUnsafeContainers", "allowVendors", "onlyVendors", "randomAdd" })
            {
                if (!Present(conditions, field, out var value)) continue;
                if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                { stopped = true; return ("stops-file-invalid-guard", null, []); }
                if (field == "bypassUnsafeContainers") bypass = value.GetBoolean();
                if (field == "allowVendors") vendors = value.GetBoolean();
                if (field == "onlyVendors" && value.GetBoolean()) vendors = true;
                if (field == "randomAdd") random = value.GetBoolean();
            }
            // Source order matters: positive and negative fields share the same
            // condition vector. Only syntax-proven empty first predicates establish
            // a skip here; unresolved runtime lookups stay explicitly unsupported.
            foreach (var family in new[] { "containers", "locations", "worldspaces", "locationKeywords", "playerSkills", "globals" })
            {
                var prior = false;
                foreach (var field in new[] { family, "!" + family })
                {
                    if (!Present(conditions, field, out var value)) continue;
                    var invalid = value.ValueKind != JsonValueKind.Array || value.GetArrayLength() == 0
                        || value.EnumerateArray().Any(v => v.ValueKind != JsonValueKind.String);
                    if (invalid && !prior)
                    {
                        notes.Add($"Pinned source skips this rule when {field} produces no conditions.");
                        return ("skipped-empty-condition", new(bypass, vendors, false, random), []);
                    }
                    if (invalid) notes.Add($"{field} can disappear while an earlier {family} predicate survives; native CDF drops it. Location-family predicates are copied verbatim so outputs drop it identically; other families are not imported.");
                    prior = true;
                }
            }
            foreach (var field in new[] { "references", "!references" })
                if (Present(conditions, field, out var value))
                    notes.Add(value.ValueKind == JsonValueKind.Array && value.GetArrayLength() == 0
                        ? $"Pinned {field}: empty predicate is {(field[0] == '!' ? "true" : "false")}; it is not an absent condition."
                        : $"Pinned {field}: malformed or unresolved entries are dropped; a non-array value omits the whole predicate, so shared analysis refuses it.");
            if (conditions.EnumerateObject().Any(p => !Conditions.Contains(p.Name))) disposition = "unsupported-predicate";
        }
        var result = new List<CdfChange>();
        foreach (var change in changes.EnumerateArray())
        {
            var index = result.Count;
            if (change.ValueKind is not (JsonValueKind.Object or JsonValueKind.Null))
            { result.Add(new(index, "native-error-risk", "unknown", null)); continue; }
            var add = Present(change, "add", out var added);
            var remove = Present(change, "remove", out var removed);
            var keywords = Present(change, "removeByKeywords", out var removedKeywords);
            var counted = Present(change, "count", out var count);
            var validCount = !counted || NativeUInt(count, out _);
            var invalid = !validCount || remove && removed.ValueKind != JsonValueKind.String
                || add && !StringArray(added) || keywords && !StringArray(removedKeywords);
            var operation = add && keywords ? "keyword-replacement" : add && remove ? "replacement"
                : keywords ? "keyword-removal" : remove ? "removal" : add ? "addition" : "none";
            // Native validation skips a change whose add, remove or keyword form does not
            // resolve. Malformed FormID strings, unloaded plugins, missing records and
            // wrong record types are null in game too; ambiguous or deleted lookups are not proved.
            var unresolved = references.Where(r => r.Field.StartsWith($"changes[{index}].", StringComparison.Ordinal) && r.Status != "resolved").ToArray();
            var state = invalid ? "skipped-invalid-change" : operation == "none" ? "no-operation"
                : unresolved.Any(r => r.Status is "malformed-identifier" or "missing-plugin" or "missing-record" or "wrong-type") ? "skipped-unresolved-form"
                : unresolved.Length > 0 ? "unresolved-native-lookup" : "unsupported-effect";
            uint? effectiveCount = validCount && operation is "addition" or "removal"
                ? counted && NativeUInt(count, out var number) ? number : operation == "addition" ? 1u : 0u : null;
            result.Add(new(index, state, operation, effectiveCount));
        }
        return (result.Count == 0 ? "no-changes" : disposition, new(bypass, vendors, false, random), result.AsReadOnly());
    }

    private static bool StringArray(JsonElement value) => value.ValueKind == JsonValueKind.Array
        && value.EnumerateArray().All(v => v.ValueKind == JsonValueKind.String);

    internal static bool NativeUInt(JsonElement value, out uint result)
    {
        result = 0;
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out var number)
            || number < 0 || number > uint.MaxValue || number != Math.Truncate(number)) return false;
        result = (uint)number;
        return true;
    }
}
