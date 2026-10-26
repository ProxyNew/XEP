using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Mutagen.Bethesda.Skyrim;

namespace XEnchantmentPatcher;

public static partial class PatcherEngine
{
    // Evidence export never supplies an acceptance or execution proof. Keep
    // duplicate VMAD names and mixed alias arrays exactly as observed.
    private static void WriteRuntimeScriptEvidence(Utf8JsonWriter json, Catalog catalog)
    {
        json.WriteString("RuntimeScriptEvidenceScope", "Loaded analysis instances only; failed bounded incoming closures can leave other winning instances unexamined. VMAD handle metadata and text association are diagnostics, not complete lifecycle/receiver proofs. Non-handle property values are not exported.");
        json.WriteStartArray("RuntimeScriptEvidence");
        foreach (var instance in catalog.RuntimeScriptInstances.Values.OrderBy(i => i.Identity, StringComparer.Ordinal))
        {
            json.WriteStartObject();
            json.WriteString("Identity", instance.Identity);
            json.WriteString("Quest", instance.Quest.FormKey.ToString());
            json.WriteString("QuestEditorId", instance.Quest.EditorID);
            json.WriteString("WinningQuestPlugin", instance.WinningQuestPlugin);
            json.WriteString("Script", instance.Name);
            json.WriteNumber("ScriptSlot", instance.ScriptSlot);
            json.WriteNumber("AliasSlot", instance.AliasSlot);
            json.WriteString("AttachedAliasQuest", instance.AliasIdentity?.Quest.ToString());
            if (instance.AliasIdentity is { } alias) json.WriteNumber("AttachedAliasId", alias.Alias);
            else json.WriteNull("AttachedAliasId");
            json.WriteString("ProofEvidence", instance.Evidence);
            json.WriteString("AssetEvidence", instance.DiagnosticAssetEvidence);
            json.WriteString("PreparedTextSha256", instance.Text is null ? null
                : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(instance.Text))));
            json.WriteString("BindingMetadataProblem", instance.BindingMetadataProblem);
            json.WriteStartArray("WinningQuestAliases");
            foreach (var questAlias in instance.Quest.Aliases)
            {
                json.WriteStartObject(); json.WriteNumber("Id", questAlias.ID);
                json.WriteString("Type", questAlias.Type.ToString()); json.WriteEndObject();
            }
            json.WriteEndArray();
            json.WriteStartArray("VmadProperties");
            foreach (var property in instance.BindingEntry?.Properties ?? [])
            {
                json.WriteStartObject(); json.WriteString("Name", property.Name);
                json.WriteString("Kind", property.GetType().Name);
                json.WriteNumber("Flags", (int)property.Flags);
                json.WriteStartArray("Handles");
                if (property is IScriptObjectPropertyGetter handle)
                    Handle(handle.Object.FormKey.ToString(), handle.Alias, -1);
                else if (property is IScriptObjectListPropertyGetter handles)
                    foreach (var (value, index) in handles.Objects.Select((v, i) => (v, i)))
                        Handle(value.Object.FormKey.ToString(), value.Alias, index);
                json.WriteEndArray(); json.WriteEndObject();
            }
            json.WriteEndArray(); json.WriteEndObject();
        }
        json.WriteEndArray();
        json.WriteStartArray("RuntimeBindingChecks");
        foreach (var check in catalog.RuntimeBindingChecks.OrderBy(c => c.OwnerIdentity, StringComparer.Ordinal)
            .ThenBy(c => c.Property, StringComparer.Ordinal).ThenBy(c => c.Receiver))
        {
            json.WriteStartObject(); json.WriteString("OwnerIdentity", check.OwnerIdentity);
            json.WriteString("Property", check.Property);
            json.WriteString("Role", check.Receiver ? "Receiver" : "Payload");
            json.WriteString("Problem", check.Result.Problem);
            json.WriteBoolean("MayRetarget", check.Result.MayRetarget);
            json.WriteString("Evidence", check.Result.Evidence);
            json.WritePropertyName("CoveredInstanceIdentities"); JsonSerializer.Serialize(json, check.Result.CoveredInstanceIdentities);
            json.WritePropertyName("Failure");
            JsonSerializer.Serialize(json, check.Result.Failure);
            json.WriteEndObject();
        }
        json.WriteEndArray();
        json.WritePropertyName("RuntimePayloadExpressions");
        JsonSerializer.Serialize(json, catalog.RuntimePayloadExpressions);
        json.WriteString("RuntimeLocalArrayIdentityScope", "Conditional identity domains for unescaped fresh function-local Form arrays only. Unknown identities remain explicit on refusal. Observed native list effects and captured VMAD targets are retained; these identity diagnostics alone do not prove target bindings. Possible non-scripted item/list payloads may enter the separate ordinary receiver/frame/holder proof path; no availability, invocation/quantity/loop count, execution or reset/reconfiguration lifecycle proof. None-only consumption, live FormList and persistent/VMAD arrays are excluded from possible-payload admission.");
        json.WritePropertyName("RuntimeLocalArrayIdentities"); JsonSerializer.Serialize(json, catalog.RuntimeLocalArrayIdentities);
        json.WriteString("RuntimeHolderCheckScope", "Known winning VMAD holder and fragment obligations for a target; passing is not native/global/effective-asset/event/saved-attachment writer or lifecycle completeness.");
        json.WriteStartArray("RuntimeHolderChecks");
        foreach (var check in catalog.RuntimeHolderChecks.OrderBy(c => c.OwnerIdentity, StringComparer.Ordinal).ThenBy(c => c.Target))
        {
            json.WriteStartObject(); json.WriteString("OwnerIdentity", check.OwnerIdentity); json.WriteString("Target", check.Target.ToString());
            json.WriteString("Problem", check.Problem); json.WriteEndObject();
        }
        json.WriteEndArray();
        json.WriteString("RuntimeHolderViewScope", "Effect-free scalar Quest views over completed bounded winning VMAD carrier components. ActiveMagicEffect attachments are universal template invariants, not runtime self identities. Placed views require a resolved winning base with no extra VMAD; composition beyond that is refused. No global/effective-asset/native/saved-attachment/event/writer/execution/lifecycle completeness.");
        json.WriteStartArray("RuntimeHolderAttachments");
        foreach (var row in catalog.RuntimeHolderAttachments.Values.OrderBy(r => r.Identity, StringComparer.Ordinal))
        {
            json.WriteStartObject(); json.WriteString("Identity", row.Identity); json.WriteString("Owner", row.Owner.ToString());
            json.WriteString("WinningPlugin", row.WinningPlugin); json.WriteString("Domain", row.Domain);
            json.WriteNumber("ScriptSlot", row.ScriptSlot); json.WriteString("Script", row.Entry.Name); json.WriteString("Base", row.Base?.ToString());
            json.WriteString("ProofEvidence", row.Asset.Evidence); json.WriteString("AssetEvidence", row.Asset.DiagnosticEvidence);
            json.WriteString("PreparedTextSha256", row.Asset.Text is null ? null : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(row.Asset.Text))));
            json.WriteEndObject();
        }
        json.WriteEndArray();
        json.WriteStartArray("RuntimeHolderViewChecks");
        foreach (var row in catalog.RuntimeHolderViewChecks.OrderBy(r => r.Target))
        {
            json.WriteStartObject(); json.WriteString("Target", row.Target.ToString()); json.WriteString("Problem", row.Problem);
            json.WriteString("Evidence", row.Evidence); json.WritePropertyName("CoveredAttachments"); JsonSerializer.Serialize(json, row.CoveredAttachments);
            json.WriteString("FailureIdentity", row.FailureIdentity); json.WriteString("Function", row.Function); json.WriteString("Line", row.Line); json.WriteEndObject();
        }
        json.WriteEndArray();
        json.WriteStartArray("RuntimeHolderAssetEvidence");
        foreach (var (name, asset) in catalog.RuntimeHolderAssets.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase))
        {
            json.WriteStartObject(); json.WriteString("Name", name); json.WriteString("ProofEvidence", asset.Evidence);
            json.WriteString("AssetEvidence", asset.DiagnosticEvidence); json.WriteString("PreparedTextSha256", asset.Text is null ? null
                : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(asset.Text)))); json.WriteEndObject();
        }
        json.WriteEndArray();
        void Handle(string record, short alias, int index)
        {
            json.WriteStartObject(); json.WriteString("Record", record);
            json.WriteNumber("AliasId", alias); json.WriteNumber("ArrayIndex", index); json.WriteEndObject();
        }
    }

    private static RuntimePropertyBinding FrameFailure(string problem, ScriptInstance caller,
        string property, string line, string function = "") => new(problem, true)
        { Failure = new(caller.Identity, property, function, line.Trim()) };
}
