using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using Loqui;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using Noggog;

namespace XEnchantmentPatcher;

// Stands in for a record Mutagen cannot build, as a getter of its own record type. It answers
// its FormKey, its EditorID (its leading EDID subrecord) and that it is not deleted (the game
// loads it); any other read throws an UnreadableRecordException that names it. Its subrecord
// bytes serve RawLinks.
public class UnreadableStandIn : DispatchProxy
{
    private const int HeaderLength = 24;
    private const uint Compressed = 0x40000;

    internal FormKey Key;
    internal byte[] Data = [];
    internal string? EditorId;
    internal UnreadableRecordException Error = null!;

    // Throws when the bytes cannot be had (a Mutagen that moved the fields), so no stand-in
    // ever claims fewer links than its record holds.
    internal static ISkyrimMajorRecordGetter Create(IGroupGetter group, FormKey key, ModKey plugin, Exception error)
    {
        // Mutagen keeps a group's bytes and each record's offset in fields it does not publish,
        // behind wrappers and, for a group split in the plugin, a merge of its parts.
        static object? Field(object cache, string name) => cache.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(cache);
        static IEnumerable<object> Caches(object part) => Field(part, "_locs") is not null ? [part]
            : part.GetType().GetProperty("SubGroups")?.GetValue(part) is System.Collections.IEnumerable split ? split.Cast<object>().SelectMany(Caches)
            : part.GetType().GetProperty("RecordCache")?.GetValue(part) is { } inner && inner != part ? Caches(inner) : [];
        var cache = Caches(group).Last(part => ((IReadOnlyDictionary<FormKey, int>)Field(part, "_locs")!).ContainsKey(key));
        var bytes = ((ReadOnlyMemorySlice<byte>)Field(cache, "_data")!).Span[((IReadOnlyDictionary<FormKey, int>)Field(cache, "_locs")!)[key]..];
        var data = bytes.Slice(HeaderLength, (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes[4..])).ToArray();
        if ((BinaryPrimitives.ReadUInt32LittleEndian(bytes[8..]) & Compressed) != 0)
        {
            using var inflated = new MemoryStream();
            using (var zlib = new ZLibStream(new MemoryStream(data, 4, data.Length - 4), CompressionMode.Decompress)) zlib.CopyTo(inflated);
            data = inflated.ToArray();
        }
        var registration = LoquiRegistration.GetRegister(group.ContainedRecordType);
        var record = (UnreadableStandIn)DispatchProxy.Create(registration.GetterType, typeof(UnreadableStandIn));
        record.Key = key;
        record.Data = data;
        if (data.AsSpan().StartsWith("EDID"u8)) record.EditorId = System.Text.Encoding.Latin1.GetString(data, 6, BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(4))).TrimEnd('\0');
        record.Error = new($"{registration.Name} {key} in {plugin.FileName} cannot be read: {error.GetBaseException().Message}\n\n"
            + "Repair the record in xEdit, or report it to the author of that plugin.", error);
        return (ISkyrimMajorRecordGetter)(object)record;
    }

    protected override object? Invoke(MethodInfo? method, object?[]? args) => method?.Name switch
    {
        "get_FormKey" => Key,
        "get_EditorID" => EditorId,
        "get_IsDeleted" => false,
        _ => throw Error,
    };
}
