using System.Buffers;
using System.IO;
using System.IO.Compression;
using System.Text.Json;

namespace XEnchantmentPatcher;

// The generation report is a bug-report artifact: users send it back so a setup can be
// reproduced. It is written gzip-compressed, and its three largest sections are stored in a
// compact layout: every string they use is stored once in "Strings" and rows refer to it by
// index. "Report" holds every other PatchReport field as ordinary JSON. ReadReport (and
// tools/xep_report.py) expand the file back to the full report; nothing is dropped.
//
//   Changes            [Source, SourceName, Generated, GeneratedName, Enchantment, [TargetList...]]
//                      one row per consecutive run of changes for the same variant
//   FallbackPlacements [Parent, ParentEditorId, Child, ChildEditorId, Level, Count]
//   ListSelections     [FormKey, Flags, Global, ChanceNonePercent, [[Reference, Level, Count]...]]
//
// String columns are indices into Strings; Level, Count, Flags and ChanceNonePercent are numbers.
public static partial class PatcherEngine
{
    internal const string ReportFormat = "xep-report-2";
    internal const CompressionLevel ReportCompression = CompressionLevel.Optimal;

    // The level parameter exists for measuring alternatives; Generate uses ReportCompression.
    private static void WriteReport(string path, PatchReport report, CompressionLevel level = ReportCompression)
    {
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20);
        using var gzip = new GZipStream(stream, level);
        using var writer = new Utf8JsonWriter(new ReportBufferWriter(gzip));
        var strings = new Dictionary<string, int>(StringComparer.Ordinal);
        var table = new List<string>();
        int Id(string value)
        {
            if (strings.TryGetValue(value, out var id)) return id;
            strings.Add(value, table.Count);
            table.Add(value);
            return table.Count - 1;
        }

        writer.WriteStartObject();
        writer.WriteString("Format", ReportFormat);
        writer.WritePropertyName("Report");
        JsonSerializer.Serialize(writer, report, JsonContext.Default.PatchReport);

        writer.WriteStartArray("Changes");
        for (var start = 0; start < report.Changes.Count;)
        {
            var first = report.Changes[start];
            var end = start + 1;
            while (end < report.Changes.Count && SameVariant(report.Changes[end], first)) end++;
            writer.WriteStartArray();
            writer.WriteNumberValue(Id(first.Source));
            writer.WriteNumberValue(Id(first.SourceName));
            writer.WriteNumberValue(Id(first.Generated));
            writer.WriteNumberValue(Id(first.GeneratedName));
            writer.WriteNumberValue(Id(first.Enchantment));
            writer.WriteStartArray();
            for (var index = start; index < end; index++) writer.WriteNumberValue(Id(report.Changes[index].TargetList));
            writer.WriteEndArray();
            writer.WriteEndArray();
            start = end;
        }
        writer.WriteEndArray();

        writer.WriteStartArray("FallbackPlacements");
        foreach (var edge in report.FallbackPlacements)
        {
            writer.WriteStartArray();
            writer.WriteNumberValue(Id(edge.Parent));
            writer.WriteNumberValue(Id(edge.ParentEditorId));
            writer.WriteNumberValue(Id(edge.Child));
            writer.WriteNumberValue(Id(edge.ChildEditorId));
            writer.WriteNumberValue(edge.Level);
            writer.WriteNumberValue(edge.Count);
            writer.WriteEndArray();
        }
        writer.WriteEndArray();

        writer.WriteStartArray("ListSelections");
        foreach (var selection in report.ListSelections)
        {
            writer.WriteStartArray();
            writer.WriteNumberValue(Id(selection.FormKey));
            writer.WriteNumberValue(selection.Flags);
            writer.WriteNumberValue(Id(selection.Global));
            writer.WriteNumberValue(selection.ChanceNonePercent);
            writer.WriteStartArray();
            foreach (var entry in selection.Entries)
            {
                writer.WriteStartArray();
                writer.WriteNumberValue(Id(entry.Reference));
                writer.WriteNumberValue(entry.Level);
                writer.WriteNumberValue(entry.Count);
                writer.WriteEndArray();
            }
            writer.WriteEndArray();
            writer.WriteEndArray();
        }
        writer.WriteEndArray();

        writer.WriteStartArray("Strings");
        foreach (var value in table) writer.WriteStringValue(value);
        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private static bool SameVariant(PatchChange left, PatchChange right)
        => left.Source == right.Source && left.SourceName == right.SourceName && left.Generated == right.Generated
            && left.GeneratedName == right.GeneratedName && left.Enchantment == right.Enchantment;

    // Expands a written report back to the full PatchReport (tests and diagnostics).
    internal static PatchReport ReadReport(string path)
    {
        using var file = File.OpenRead(path);
        using var gzip = new GZipStream(file, CompressionMode.Decompress);
        using var document = JsonDocument.Parse(gzip);
        var root = document.RootElement;
        if (root.GetProperty("Format").GetString() != ReportFormat)
            throw new InvalidDataException($"{path} is not a {ReportFormat} generation report.");
        var report = root.GetProperty("Report").Deserialize(JsonContext.Default.PatchReport)
            ?? throw new InvalidDataException($"{path} has no report.");
        var strings = root.GetProperty("Strings").EnumerateArray().Select(x => x.GetString()!).ToArray();
        string Text(JsonElement row, int column) => strings[row[column].GetInt32()];
        foreach (var row in root.GetProperty("Changes").EnumerateArray())
            foreach (var target in row[5].EnumerateArray())
                report.Changes.Add(new PatchChange(Text(row, 0), Text(row, 1), Text(row, 2), Text(row, 3), Text(row, 4),
                    strings[target.GetInt32()]));
        foreach (var row in root.GetProperty("FallbackPlacements").EnumerateArray())
            report.FallbackPlacements.Add(new FallbackPlacement(Text(row, 0), Text(row, 1), Text(row, 2), Text(row, 3),
                row[4].GetInt16(), row[5].GetInt16()));
        foreach (var row in root.GetProperty("ListSelections").EnumerateArray())
            report.ListSelections.Add(new ListSelection(Text(row, 0), row[1].GetInt32(), Text(row, 2), row[3].GetDouble(),
                row[4].EnumerateArray().Select(entry => new ListSelectionEntry(Text(entry, 0), entry[1].GetInt32(), entry[2].GetInt32())).ToList()));
        return report;
    }

    // .NET's Stream serializers can buffer an entire primitive list before
    // flushing. Drain each committed writer buffer instead, keeping memory
    // proportional to the largest individual token, not the report or list.
    private sealed class ReportBufferWriter(Stream stream) : IBufferWriter<byte>
    {
        private byte[] buffer = new byte[1024 * 1024];
        public void Advance(int count) => stream.Write(buffer.AsSpan(0, count));
        public Memory<byte> GetMemory(int sizeHint = 0)
        {
            if (sizeHint > buffer.Length) buffer = new byte[sizeHint];
            return buffer;
        }
        public Span<byte> GetSpan(int sizeHint = 0) => GetMemory(sizeHint).Span;
    }
}
