using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using SafePatch.Host;

namespace SafePatch.Authoring.Index;

/// <summary>One record as the scanner saw it: raw header fields and the EditorID, nothing decoded beyond that.</summary>
/// <param name="FormId">The FormID as stored: its top byte indexes the plugin's masters, or the plugin itself.</param>
/// <param name="Signature">The four-character record type, little-endian (<c>STAT</c>, <c>LVLI</c>...).</param>
/// <param name="Offset">Where the record (its header) starts in the file.</param>
public readonly record struct ScannedRecord(uint FormId, uint Signature, uint Flags, string? EditorId, int Offset)
{
    public bool IsDeleted => (Flags & 0x20) != 0;
    public bool IsCompressed => (Flags & 0x40000) != 0;
}

/// <summary>A plugin's header and every record in it, in file order.</summary>
/// <param name="HeaderVersion">The header's version (HEDR), which decides how some fields are read.</param>
/// <param name="FormVersion">The header record's form version, likewise.</param>
public sealed record ScannedPlugin(string FileName, uint HeaderFlags, IReadOnlyList<string> Masters, int HeaderRecordCount, ScannedRecord[] Records,
    float HeaderVersion = 0, ushort FormVersion = 0)
{
    public bool IsMaster => (HeaderFlags & 0x1) != 0;
    public bool IsLight => (HeaderFlags & 0x200) != 0;
}

/// <summary>
/// A fast, read-only walk over a Skyrim plugin's groups and record headers, after SARP's plugin reader: it reads
/// each record's header and EditorID and skips the rest, inflating compressed records only as far as their
/// EditorID. It never decodes fields; Mutagen does that for the records a query shows. Malformed framing (bad
/// lengths, groups that do not advance) is an error rather than a loop.
/// </summary>
public static class PluginScanner
{
    public const uint Grup = 0x50555247; // "GRUP"
    public const uint Tes4 = 0x34534554; // "TES4"
    private const uint Edid = 0x44494445; // "EDID"
    private const uint Xxxx = 0x58585858; // "XXXX"
    private const uint Hedr = 0x52444548; // "HEDR"
    private const uint Mast = 0x5453414D; // "MAST"
    private const int HeaderLength = 24;
    private const int EditorIdPrefix = 4 + 6 + 512;
    private const uint Compressed = 0x40000;
    private const int MaxInflatedRecord = 64 * 1024 * 1024;

    /// <summary>Scans a plugin file, opened so that a mod manager can still delete or rename it meanwhile.</summary>
    /// <param name="wanted">Only records of these signatures (top-level groups of other types are skipped whole); all when null.</param>
    public static ScannedPlugin Scan(string path, IReadOnlySet<uint>? wanted = null, bool editorIds = true) =>
        Scan(Path.GetFileName(path), ReadFile(path), wanted, editorIds);

    public static ScannedPlugin Scan(string fileName, byte[] file, IReadOnlySet<uint>? wanted = null, bool editorIds = true)
    {
        var layout = Layout(fileName, file, wanted);
        var records = PerGroup(fileName, file, layout, wanted, (offset, size, signature, flags, formId) =>
            new ScannedRecord(formId, signature, flags, editorIds ? EditorId(file, offset, size, (flags & Compressed) != 0) : null, offset - HeaderLength));
        return new ScannedPlugin(fileName, layout.HeaderFlags, layout.Masters, layout.RecordCount, records, layout.HeaderVersion, layout.FormVersion);
    }

    /// <summary>Tests a record's data, inflated if it was compressed.</summary>
    public delegate bool DataMatcher(ReadOnlySpan<byte> data);

    /// <summary>The stored FormIDs of the records whose data <paramref name="matches"/>. Compressed records are inflated to test them.</summary>
    public static IReadOnlyList<uint> RecordsMatching(string path, DataMatcher matches, IReadOnlySet<uint>? wanted = null)
    {
        var file = ReadFile(path);
        var fileName = Path.GetFileName(path);
        var found = PerGroup(fileName, file, Layout(fileName, file, wanted), wanted, (offset, size, _, flags, formId) =>
            matches((flags & Compressed) != 0 ? Inflate(file, offset, size) : file.AsSpan(offset, size)) ? formId : (uint?)null);
        return [.. found.OfType<uint>()];
    }

    /// <summary>Matches data holding <paramref name="value"/> as four little-endian bytes, e.g. a FormID as a plugin stores it.</summary>
    public static DataMatcher Containing(uint value)
    {
        var pattern = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(pattern, value);
        return data => data.IndexOf(pattern) >= 0;
    }

    /// <summary>Matches data holding <paramref name="text"/> in any letter case (ASCII), e.g. a file name in a model path.</summary>
    public static DataMatcher ContainingText(string text)
    {
        var pattern = Encoding.ASCII.GetBytes(text);
        var first = new[] { (byte)char.ToLowerInvariant((char)pattern[0]), (byte)char.ToUpperInvariant((char)pattern[0]) };
        return data =>
        {
            for (var start = 0; start <= data.Length - pattern.Length;)
            {
                var hit = data[start..].IndexOfAny(first);
                if (hit < 0 || start + hit > data.Length - pattern.Length) return false;
                if (System.Text.Ascii.EqualsIgnoreCase(data.Slice(start + hit, pattern.Length), pattern)) return true;
                start += hit + 1;
            }
            return false;
        };
    }

    private delegate T RecordVisitor<out T>(int dataOffset, int dataSize, uint signature, uint flags, uint formId);

    /// <summary>A plugin's header, and the byte ranges of its top-level groups (the wanted ones, when some are named).</summary>
    private sealed record FileLayout(uint HeaderFlags, IReadOnlyList<string> Masters, int RecordCount, IReadOnlyList<(int Start, int End)> Groups, float HeaderVersion, ushort FormVersion);

    /// <summary>Files at least this large have their top-level groups walked in parallel.</summary>
    private const int ParallelFileBytes = 8 * 1024 * 1024;

    private static FileLayout Layout(string fileName, byte[] file, IReadOnlySet<uint>? wanted)
    {
        ReadOnlySpan<byte> bytes = file;
        if (bytes.Length < HeaderLength || BinaryPrimitives.ReadUInt32LittleEndian(bytes) != Tes4)
            throw new SafePatchException($"{fileName} is not a Skyrim plugin: it does not start with a TES4 header.");
        var headerSize = (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes[4..]);
        var headerFlags = BinaryPrimitives.ReadUInt32LittleEndian(bytes[8..]);
        if (HeaderLength + (long)headerSize > bytes.Length) throw Malformed(fileName, 0);
        var formVersion = BinaryPrimitives.ReadUInt16LittleEndian(bytes[20..]);
        var (masters, recordCount, headerVersion) = ReadHeader(bytes.Slice(HeaderLength, headerSize), fileName);

        var groups = new List<(int, int)>();
        var position = HeaderLength + headerSize;
        while (position < bytes.Length)
        {
            if (bytes.Length - position < HeaderLength) throw Malformed(fileName, position);
            var header = bytes.Slice(position, HeaderLength);
            var size = BinaryPrimitives.ReadUInt32LittleEndian(header[4..]);
            var isGroup = BinaryPrimitives.ReadUInt32LittleEndian(header) == Grup;
            var length = isGroup ? size : HeaderLength + (long)size;
            if ((isGroup && size < HeaderLength) || position + length > bytes.Length) throw Malformed(fileName, position);
            // Top-level groups are labelled with their record type: skip whole ones the caller does not want, unless
            // they nest groups, which hold other types' records (cells' placed objects, a topic's responses).
            if (!isGroup || wanted is null || wanted.Contains(BinaryPrimitives.ReadUInt32LittleEndian(header[8..]))
                || NestsGroups(bytes, position + HeaderLength, (int)(position + length)))
                groups.Add((position, (int)(position + length)));
            position += (int)length;
        }
        return new FileLayout(headerFlags, masters, recordCount, groups, headerVersion, formVersion);
    }

    /// <summary>Whether a group's direct contents include a group. Only record headers are read, to skip over them.</summary>
    private static bool NestsGroups(ReadOnlySpan<byte> bytes, int start, int end)
    {
        for (var position = start; position + HeaderLength <= end;)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(bytes[position..]) == Grup) return true;
            position += HeaderLength + (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes[(position + 4)..]);
        }
        return false;
    }

    /// <summary>Visits every record of every group in the layout, a large file's groups in parallel, and returns the results in file order.</summary>
    private static T[] PerGroup<T>(string fileName, byte[] file, FileLayout layout, IReadOnlySet<uint>? wanted, RecordVisitor<T> visit)
    {
        // Walking the headers is cheap and sequential; what is done per record (inflating, searching) is not. So the
        // records are listed first and then visited in parallel chunks: a single top-level group (a worldspace with
        // its cells, land and navmeshes) spreads over every core instead of one.
        var records = new List<(int Offset, int Size, uint Signature, uint Flags, uint FormId)>();
        foreach (var (groupStart, groupEnd) in layout.Groups)
        {
            WalkRange(fileName, file, groupStart, groupEnd, wanted, (offset, size, signature, flags, formId) =>
            {
                records.Add((offset, size, signature, flags, formId));
                return true;
            });
        }
        var results = new T[records.Count];
        if (file.Length >= ParallelFileBytes && records.Count > RecordsPerChunk)
        {
            Parallel.ForEach(System.Collections.Concurrent.Partitioner.Create(0, records.Count, RecordsPerChunk), range =>
            {
                for (var i = range.Item1; i < range.Item2; i++)
                {
                    var r = records[i];
                    results[i] = visit(r.Offset, r.Size, r.Signature, r.Flags, r.FormId);
                }
            });
        }
        else
        {
            for (var i = 0; i < records.Count; i++)
            {
                var r = records[i];
                results[i] = visit(r.Offset, r.Size, r.Signature, r.Flags, r.FormId);
            }
        }
        return results;
    }

    /// <summary>Records visited per parallel work item in a large file.</summary>
    private const int RecordsPerChunk = 2048;

    /// <summary>
    /// Visits every record in a byte range: groups are entered, not recursed into, so nesting costs nothing.
    /// Framing is checked as it goes.
    /// </summary>
    private static void WalkRange(string fileName, byte[] file, int start, int end, IReadOnlySet<uint>? wanted, RecordVisitor<bool> visit)
    {
        ReadOnlySpan<byte> bytes = file;
        var position = start;
        while (position < end)
        {
            if (end - position < HeaderLength) throw Malformed(fileName, position);
            var header = bytes.Slice(position, HeaderLength);
            var signature = BinaryPrimitives.ReadUInt32LittleEndian(header);
            var size = BinaryPrimitives.ReadUInt32LittleEndian(header[4..]);
            if (signature == Grup)
            {
                if (size < HeaderLength || position + (long)size > end) throw Malformed(fileName, position);
                position += HeaderLength; // step into the group: its children follow its header
                continue;
            }

            if (position + HeaderLength + (long)size > end) throw Malformed(fileName, position);
            if (wanted is null || wanted.Contains(signature))
                visit(position + HeaderLength, (int)size, signature, BinaryPrimitives.ReadUInt32LittleEndian(header[8..]), BinaryPrimitives.ReadUInt32LittleEndian(header[12..]));
            position += HeaderLength + (int)size;
        }
    }

    private static byte[] ReadFile(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, bufferSize: 1, FileOptions.SequentialScan);
        var bytes = GC.AllocateUninitializedArray<byte>(checked((int)stream.Length));
        stream.ReadExactly(bytes);
        return bytes;
    }

    /// <summary>A compressed record's data, inflated; empty when it does not inflate.</summary>
    private static byte[] Inflate(byte[] file, int offset, int size)
    {
        if (size < 4) return [];
        var length = BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(offset));
        if (length > MaxInflatedRecord) return [];
        var output = new byte[length];
        try
        {
            using var input = new MemoryStream(file, offset + 4, size - 4, writable: false);
            using var zlib = new ZLibStream(input, CompressionMode.Decompress);
            zlib.ReadAtLeast(output, output.Length, throwOnEndOfStream: false);
            return output;
        }
        catch (InvalidDataException)
        {
            return [];
        }
    }

    /// <summary>The four-character name of a signature.</summary>
    public static string Name(uint signature)
    {
        Span<byte> chars = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(chars, signature);
        return Encoding.ASCII.GetString(chars);
    }

    public static uint Signature(string name) => BinaryPrimitives.ReadUInt32LittleEndian(Encoding.ASCII.GetBytes(name.PadRight(4)[..4]));

    private static (IReadOnlyList<string> Masters, int RecordCount, float Version) ReadHeader(ReadOnlySpan<byte> data, string fileName)
    {
        var masters = new List<string>();
        var recordCount = 0;
        var version = 0f;
        foreach (var (signature, payload) in Subrecords(data, fileName))
        {
            if (signature == Hedr && payload.Length >= 8)
            {
                version = BinaryPrimitives.ReadSingleLittleEndian(payload);
                recordCount = (int)BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(4));
            }
            else if (signature == Mast) masters.Add(ZString(payload));
        }
        return (masters, recordCount, version);
    }

    /// <summary>The record's EditorID: its first subrecord when that is EDID.</summary>
    private static string? EditorId(byte[] file, int offset, int size, bool compressed)
    {
        var data = file.AsSpan(offset, size);
        if (!compressed) return FirstEditorId(data);
        if (data.Length < 4) return null;
        var uncompressed = (int)Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(data), int.MaxValue);
        var zlib = data[4..];

        // Inflate only the first subrecord's header: most compressed records (landscape, navmeshes) have no EditorID,
        // and those that do need only as far as its end.
        Span<byte> prefix = stackalloc byte[EditorIdPrefix];
        var header = InflatePrefix.Inflate(zlib, prefix[..Math.Min(6, uncompressed)]);
        if (header < 0) return EditorIdByZLib(file, offset, size, uncompressed, prefix);
        if (header < 6 || BinaryPrimitives.ReadUInt32LittleEndian(prefix) != Edid) return null;
        var wanted = Math.Min(6 + BinaryPrimitives.ReadUInt16LittleEndian(prefix[4..]), Math.Min(uncompressed, prefix.Length));
        var read = InflatePrefix.Inflate(zlib, prefix[..wanted]);
        return read < 0 ? EditorIdByZLib(file, offset, size, uncompressed, prefix) : FirstEditorId(prefix[..read]);
    }

    /// <summary>The same through zlib, for a stream the prefix decoder does not accept.</summary>
    private static string? EditorIdByZLib(byte[] file, int offset, int size, int uncompressed, Span<byte> prefix)
    {
        var expected = Math.Min(uncompressed, prefix.Length);
        try
        {
            using var input = new MemoryStream(file, offset + 4, size - 4, writable: false);
            using var zlib = new ZLibStream(input, CompressionMode.Decompress);
            var read = 0;
            while (read < expected)
            {
                var n = zlib.Read(prefix[read..expected]);
                if (n == 0) break;
                read += n;
            }
            return FirstEditorId(prefix[..read]);
        }
        catch (InvalidDataException)
        {
            return null;
        }
    }

    private static string? FirstEditorId(ReadOnlySpan<byte> data)
    {
        if (data.Length < 6 || BinaryPrimitives.ReadUInt32LittleEndian(data) != Edid) return null;
        var size = BinaryPrimitives.ReadUInt16LittleEndian(data[4..]);
        return data.Length < 6 + size ? null : ZString(data.Slice(6, size));
    }

    private static List<(uint Signature, byte[] Payload)> Subrecords(ReadOnlySpan<byte> data, string fileName)
    {
        var result = new List<(uint, byte[])>();
        var position = 0;
        uint? bigSize = null;
        while (position + 6 <= data.Length)
        {
            var signature = BinaryPrimitives.ReadUInt32LittleEndian(data[position..]);
            int size = BinaryPrimitives.ReadUInt16LittleEndian(data[(position + 4)..]);
            position += 6;
            if (bigSize is { } big)
            {
                size = checked((int)big);
                bigSize = null;
            }
            if (position + size > data.Length) throw Malformed(fileName, position);
            if (signature == Xxxx && size == 4) bigSize = BinaryPrimitives.ReadUInt32LittleEndian(data[position..]);
            else result.Add((signature, data.Slice(position, size).ToArray()));
            position += size;
        }
        return result;
    }

    private static string ZString(ReadOnlySpan<byte> bytes)
    {
        var end = bytes.IndexOf((byte)0);
        return Encoding.Latin1.GetString(end < 0 ? bytes : bytes[..end]);
    }

    private static SafePatchException Malformed(string fileName, long position) =>
        new($"{fileName} is malformed at byte {position}: a record or group runs past its container.");
}
