using System.Buffers.Binary;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins.Binary.Headers;
using Mutagen.Bethesda.Plugins.Meta;
using Noggog;
using SafePatch.Host;

namespace SafePatch.Mutagen;

/// <summary>
/// Checks that an untrusted plugin is soundly framed before Mutagen reads it. Mutagen trusts the
/// lengths it reads: a group whose length is zero makes its reader loop forever, in the host process.
/// So every group and record must be at least a header long, fit in its parent and end exactly where
/// the parent does; groups nest a bounded number of levels; and a compressed record may not claim to
/// inflate beyond a limit. Header layouts come from Mutagen's <see cref="GameConstants"/>.
/// </summary>
public static class PluginFraming
{
    /// <summary>Deeper than any real plugin (worldspace, block, sub-block, cell, children).</summary>
    public const int MaxGroupDepth = 8;

    /// <summary>Far larger than any real record's decompressed data.</summary>
    public const uint MaxDecompressedRecordBytes = 64 * 1024 * 1024;

    public static void Validate(byte[] plugin, GameRelease release)
    {
        var constants = GameConstants.Get(release);
        var header = Major(plugin, 0, plugin.Length, constants);
        Items(plugin, (int)header.TotalLength, plugin.Length, constants, depth: 0);
    }

    private static void Items(byte[] plugin, int position, int end, GameConstants constants, int depth)
    {
        while (position < end)
        {
            if (end - position < 4) throw Malformed(position, "a truncated header");
            if (IsGroup(plugin, position))
            {
                if (depth >= MaxGroupDepth) throw Malformed(position, $"groups nested more than {MaxGroupDepth} deep");
                if (end - position < constants.GroupConstants.HeaderLength) throw Malformed(position, "a truncated group header");
                var group = new GroupHeader(constants, new ReadOnlyMemorySlice<byte>(plugin, position, constants.GroupConstants.HeaderLength));
                if (group.TotalLength < group.HeaderLength || group.TotalLength > end - position)
                    throw Malformed(position, $"a group of length {group.TotalLength}");
                Items(plugin, position + group.HeaderLength, position + (int)group.TotalLength, constants, depth + 1);
                position += (int)group.TotalLength;
            }
            else
            {
                // Top-level items are groups; records live inside them.
                if (depth == 0) throw Malformed(position, "a record outside any group");
                position += (int)Major(plugin, position, end, constants).TotalLength;
            }
        }
    }

    private static MajorRecordHeader Major(byte[] plugin, int position, int end, GameConstants constants)
    {
        var headerLength = constants.MajorConstants.HeaderLength;
        if (end - position < headerLength) throw Malformed(position, "a truncated record header");
        var record = new MajorRecordHeader(constants, new ReadOnlyMemorySlice<byte>(plugin, position, headerLength));
        if (record.TotalLength > end - position) throw Malformed(position, $"a record of length {record.TotalLength}");
        if (record.IsCompressed)
        {
            if (record.ContentLength < 4) throw Malformed(position, "a compressed record without its size");
            if (BinaryPrimitives.ReadUInt32LittleEndian(plugin.AsSpan(position + headerLength)) > MaxDecompressedRecordBytes)
                throw Malformed(position, "a compressed record claiming more than the size limit");
        }
        return record;
    }

    private static bool IsGroup(byte[] plugin, int position) => plugin.AsSpan(position, 4).SequenceEqual("GRUP"u8);

    private static PatchRejectedException Malformed(int position, string what) =>
        new($"The worker's plugin is malformed: {what} at offset {position}.");
}
