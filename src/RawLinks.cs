using System.Buffers.Binary;
using System.Reflection;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using Noggog;

namespace XEnchantmentPatcher;

public static partial class PatcherEngine
{
    // Mutagen keeps a record's subrecord bytes, decompressed, in a field it does not publish.
    private static readonly FieldInfo? OverlayRecordData = typeof(FormKey).Assembly
        .GetType("Mutagen.Bethesda.Plugins.Binary.Overlay.PluginBinaryOverlay")?.GetField("_recordData", BindingFlags.NonPublic | BindingFlags.Instance);

    // For a record Mutagen cannot read: every key its stored bytes can name, read at each
    // offset through the masters of the plugin that holds it. A superset of its links, so a
    // pass that treats the record as linking to these keys and no others refuses no less than
    // the record requires. It throws when the bytes cannot be had: a Mutagen that moved the field.
    internal static HashSet<FormKey> RawLinks(ISkyrimMajorRecordGetter record, ModKey plugin, IReadOnlyList<ISkyrimModGetter> mods)
    {
        var data = (ReadOnlyMemorySlice<byte>)OverlayRecordData!.GetValue(record)!;
        var masters = mods.First(m => m.ModKey == plugin).ModHeader.MasterReferences;
        var keys = new HashSet<FormKey>();
        for (var span = data.Span; span.Length >= 4; span = span[1..])
        {
            var id = BinaryPrimitives.ReadUInt32LittleEndian(span);
            keys.Add(new FormKey(id >> 24 < masters.Count ? masters[(int)(id >> 24)].Master : plugin, id & 0xFFFFFF));
        }
        return keys;
    }

    // Each winning quest with the keys it links to. A quest Mutagen cannot read comes with the
    // keys its bytes can name, which may be more than it links to, and with the failure.
    internal static IEnumerable<(IQuestGetter Quest, IEnumerable<FormKey> Keys, Exception? Unreadable)> QuestLinks(IReadOnlyList<ISkyrimModGetter> mods)
    {
        foreach (var (quest, plugin) in mods.SelectMany(m => m.Quests.Select(q => (q, m.ModKey))).GroupBy(q => q.q.FormKey).Select(g => g.Last()))
        {
            IEnumerable<FormKey> keys; Exception? unreadable = null;
            try { keys = quest.EnumerateFormLinks().Select(l => l.FormKey).ToArray(); }
            catch (Exception ex) { keys = RawLinks(quest, plugin, mods); unreadable = ex; }
            yield return (quest, keys, unreadable);
        }
    }

    // For an operation that stopped inside one of Mutagen's record readers: the first record,
    // in load order, whose full read fails with the same message. Null for any other stop.
    // A cell, worldspace or topic is read without its children, which are records of their own.
    internal static string? UnreadableRecord(IReadOnlyList<ISkyrimModGetter> mods, Exception error)
    {
        var text = error.ToString();
        if (!text.Contains("BinaryOverlay")) return null;
        return mods.AsParallel().AsOrdered().Select(mod =>
        {
            foreach (var record in mod.EnumerateMajorRecords<ISkyrimMajorRecordGetter>())
                try
                {
                    if (record is ICellGetter or IWorldspaceGetter or IDialogTopicGetter) record.EnumerateFormLinks(iterateNestedRecords: false).Count();
                    else record.DeepCopy();
                }
                catch (Exception ex) when (text.Contains(ex.GetBaseException().Message)) { return $"{record.Registration.Name} {record.FormKey} in {mod.ModKey.FileName}"; }
                catch (Exception) { }
            return null;
        }).FirstOrDefault(found => found is not null);
    }
}
