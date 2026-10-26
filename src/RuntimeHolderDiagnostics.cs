using System.Text.Json;
using Mutagen.Bethesda.Skyrim;

namespace XEnchantmentPatcher;

public static partial class PatcherEngine
{
    // Static winning-record metadata, independent of TouchesLists and text
    // loading. This inventory never supplies writer/event/execution closure.
    private static void WriteWinningRuntimeHolderEvidence(Utf8JsonWriter json, Catalog catalog)
    {
        var winners = RuntimeRecordWinners(catalog).Values.OrderBy(w => w.Record.FormKey).ToArray();
        json.WriteString("WinningRuntimeHolderScope", "All nondeleted winning record VMAD entries and fragment roots, winning Quest alias fill metadata, and immediate winning FormList members. Winners are chosen before filtering deletion or removed VMAD. Static plugin metadata only; native/global helpers, saved/runtime attachments, effective script assets, event subscribers, writers, execution and lifecycle are not closed. No nested FormList flattening or scalar VMAD values.");
        json.WriteStartArray("WinningRuntimeHolders");
        foreach (var (record, plugin) in winners)
        {
            if (record.IsDeleted || record is not IHaveVirtualMachineAdapterGetter owner || owner.VirtualMachineAdapter is not { } adapter) continue;
            json.WriteStartObject();
            json.WriteString("Owner", record.FormKey.ToString()); json.WriteString("WinningPlugin", plugin.ToString());
            json.WriteString("RecordType", record.GetType().Name); json.WriteString("EditorId", record.EditorID);
            json.WriteStartArray("Scripts");
            foreach (var (script, slot) in adapter.Scripts.Select((s, i) => (s, i))) Entry(script, slot, -1, null);
            if (adapter is IQuestAdapterGetter questAdapter)
                foreach (var (alias, aliasSlot) in questAdapter.Aliases.Select((a, i) => (a, i)))
                    foreach (var (script, slot) in alias.Scripts.Select((s, i) => (s, i))) Entry(script, slot, aliasSlot, alias.Property);
            json.WriteEndArray();
            json.WriteStartArray("FragmentRoots");
            switch (adapter)
            {
                case IQuestAdapterGetter quest:
                    FragmentFile(quest.FileName);
                    foreach (var f in quest.Fragments) Fragment(f.ScriptName, f.FragmentName, "QuestStage", f.Stage, f.StageIndex);
                    break;
                case IDialogResponsesAdapterGetter dialog when dialog.ScriptFragments is { } f:
                    Common(f); break;
                case IPackageAdapterGetter package when package.ScriptFragments is { } f:
                    FragmentFile(f.FileName); Named(f.OnBegin, "OnBegin"); Named(f.OnEnd, "OnEnd"); Named(f.OnChange, "OnChange"); break;
                case ISceneAdapterGetter scene when scene.ScriptFragments is { } f:
                    Common(f);
                    foreach (var phase in f.PhaseFragments) Fragment(phase.ScriptName, phase.FragmentName, "ScenePhase", phase.Index, (int)phase.Flags);
                    break;
                case IPerkAdapterGetter perk when perk.ScriptFragments is { } f:
                    FragmentFile(f.FileName);
                    foreach (var indexed in f.Fragments) Fragment(indexed.ScriptName, indexed.FragmentName, "PerkIndexed", indexed.FragmentIndex, null);
                    break;
            }
            json.WriteEndArray(); json.WriteEndObject();
        }
        json.WriteEndArray();
        json.WriteStartArray("WinningQuestAliasFills");
        foreach (var (record, plugin) in winners)
        {
            if (record.IsDeleted || record is not IQuestGetter quest || quest.Aliases.Count == 0) continue;
            json.WriteStartObject(); json.WriteString("Quest", quest.FormKey.ToString()); json.WriteString("WinningPlugin", plugin.ToString());
            json.WriteStartArray("Aliases");
            foreach (var (alias, index) in quest.Aliases.Select((a, i) => (a, i)))
            {
                json.WriteStartObject(); json.WriteNumber("Index", index); json.WriteNumber("Id", alias.ID);
                json.WriteString("Name", alias.Name); json.WriteString("Type", alias.Type.ToString());
                json.WritePropertyName("Flags"); JsonSerializer.Serialize(json, alias.Flags is { } flags ? (uint?)flags : null);
                json.WritePropertyName("AliasIdToForceIntoWhenFilled"); JsonSerializer.Serialize(json, alias.AliasIDToForceIntoWhenFilled);
                json.WriteString("ForcedReference", alias.ForcedReference.FormKeyNullable?.ToString());
                json.WriteString("UniqueActor", alias.UniqueActor.FormKeyNullable?.ToString());
                json.WriteString("SpecificLocation", alias.SpecificLocation.FormKeyNullable?.ToString());
                json.WritePropertyName("Location"); JsonSerializer.Serialize(json, alias.Location is not { } location ? null : new {
                    location.AliasID, Keyword = location.Keyword.FormKeyNullable?.ToString(), RefType = location.RefType.FormKeyNullable?.ToString() });
                json.WritePropertyName("External"); JsonSerializer.Serialize(json, alias.External is not { } external ? null : new {
                    Quest = external.Quest.FormKeyNullable?.ToString(), external.AliasID });
                json.WritePropertyName("CreateReferenceToObject"); JsonSerializer.Serialize(json, alias.CreateReferenceToObject is not { } create ? null : new {
                    Object = create.Object.FormKey.ToString(), create.AliasID, Create = (int)create.Create, Level = (int)create.Level });
                json.WritePropertyName("FindMatchingRefNearAlias"); JsonSerializer.Serialize(json, alias.FindMatchingRefNearAlias is not { } near ? null : new {
                    near.AliasID, Type = near.Type is { } type ? (int?)type : null });
                json.WritePropertyName("FindMatchingRefFromEvent"); JsonSerializer.Serialize(json, alias.FindMatchingRefFromEvent is not { } fromEvent ? null : new {
                    FromEvent = fromEvent.FromEvent?.ToString(), EventData = fromEvent.EventData is { } data ? (int?)data : null });
                json.WriteNumber("ConditionCount", alias.Conditions.Count); json.WriteEndObject();
            }
            json.WriteEndArray(); json.WriteEndObject();
        }
        json.WriteEndArray();
        json.WriteStartArray("WinningFormListMembers");
        foreach (var (record, plugin) in winners)
        {
            if (record.IsDeleted || record is not IFormListGetter list) continue;
            json.WriteStartObject(); json.WriteString("FormList", list.FormKey.ToString()); json.WriteString("WinningPlugin", plugin.ToString());
            json.WriteStartArray("Members");
            foreach (var (member, index) in list.Items.Select((m, i) => (m, i)))
            {
                json.WriteStartObject(); json.WriteNumber("Index", index); json.WriteString("Record", member.FormKey.ToString());
                json.WriteBoolean("NondeletedWinner", RuntimeRecordWinners(catalog).TryGetValue(member.FormKey, out var winning) && !winning.Record.IsDeleted);
                json.WriteEndObject();
            }
            json.WriteEndArray(); json.WriteEndObject();
        }
        json.WriteEndArray();

        void Entry(IScriptEntryGetter script, int slot, int aliasSlot, IScriptObjectPropertyGetter? attached)
        {
            json.WriteStartObject(); json.WriteString("Script", script.Name); json.WriteNumber("ScriptSlot", slot); json.WriteNumber("AliasSlot", aliasSlot);
            json.WritePropertyName("AttachedAlias");
            if (attached is null) json.WriteNullValue(); else Handle(attached, -1);
            json.WriteStartArray("Properties");
            foreach (var (property, index) in script.Properties.Select((p, i) => (p, i)))
            {
                json.WriteStartObject(); json.WriteNumber("Index", index); json.WriteString("Name", property.Name);
                json.WriteString("Kind", property.GetType().Name); json.WriteNumber("Flags", (int)property.Flags);
                json.WriteStartArray("Handles");
                if (property is IScriptObjectPropertyGetter scalar) Handle(scalar, -1);
                else if (property is IScriptObjectListPropertyGetter array)
                    foreach (var (handle, element) in array.Objects.Select((h, i) => (h, i))) Handle(handle, element);
                json.WriteEndArray(); json.WriteEndObject();
            }
            json.WriteEndArray(); json.WriteEndObject();
        }
        void Handle(IScriptObjectPropertyGetter handle, int index)
        {
            json.WriteStartObject(); json.WriteString("Record", handle.Object.FormKey.ToString());
            json.WriteNumber("AliasId", handle.Alias); json.WriteNumber("ArrayIndex", index); json.WriteEndObject();
        }
        void Fragment(string script, string function, string kind, int? index, int? subindex)
        {
            json.WriteStartObject(); json.WriteString("Script", script); json.WriteString("Function", function); json.WriteString("Kind", kind);
            json.WritePropertyName("Index"); JsonSerializer.Serialize(json, index); json.WritePropertyName("SubIndex"); JsonSerializer.Serialize(json, subindex); json.WriteEndObject();
        }
        void FragmentFile(string file) => Fragment(file, "", "FragmentFile", null, null);
        void Named(IScriptFragmentGetter? f, string kind) { if (f is not null) Fragment(f.ScriptName, f.FragmentName, kind, null, f.ExtraBindDataVersion); }
        void Common(IScriptFragmentsGetter f) { FragmentFile(f.FileName); Named(f.OnBegin, "OnBegin"); Named(f.OnEnd, "OnEnd"); }
    }
}
