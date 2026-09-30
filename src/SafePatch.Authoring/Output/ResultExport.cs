using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using SafePatch.Host;

namespace SafePatch.Authoring.Output;

public enum ExportFormat
{
    /// <summary>One JSON object per row, keyed by column.</summary>
    Jsonl,
    Csv,
    /// <summary>The rendered result, unbudgeted.</summary>
    Text,
}

/// <summary>A whole result written to a file, for agents that analyse it with their own tools.</summary>
public static class ResultExport
{
    private static readonly JsonSerializerOptions Json = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>Writes <paramref name="set"/> to a new file; refuses to replace one.</summary>
    /// <returns>The file's full path and size in bytes.</returns>
    public static (string Path, long Bytes) Write(ResultSet set, string path, ExportFormat format)
    {
        var full = Path.GetFullPath(path);
        if (File.Exists(full) || Directory.Exists(full)) throw new SafePatchException($"{full} already exists; export to a new file.");
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);

        using (var writer = new StreamWriter(full, append: false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
        {
            switch (format)
            {
                case ExportFormat.Jsonl:
                    foreach (var row in set.Rows)
                        writer.WriteLine(JsonSerializer.Serialize(set.Columns.Select((c, i) => (c, row[i])).ToDictionary(p => p.c, p => p.Item2), Json));
                    break;
                case ExportFormat.Csv:
                    writer.WriteLine(string.Join(',', set.Columns.Select(Csv)));
                    foreach (var row in set.Rows) writer.WriteLine(string.Join(',', row.Select(Csv)));
                    break;
                default:
                    writer.WriteLine(Budgeted.Render(set, budget: int.MaxValue));
                    break;
            }
        }
        return (full, new FileInfo(full).Length);
    }

    private static string Csv(string? value) =>
        value is null ? "" : value.IndexOfAny([',', '"', '\n', '\r']) < 0 ? value : "\"" + value.Replace("\"", "\"\"") + "\"";
}
