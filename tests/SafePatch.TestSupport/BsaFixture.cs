using System.Text;

namespace SafePatch.TestSupport;

/// <summary>
/// Writes a minimal Skyrim SE archive (BSA version 105): folder and file names, no compression.
/// Name hashes are left at zero; Mutagen looks files up by name and does not read them.
/// </summary>
public static class BsaFixture
{
    private const int HeaderLength = 36;
    private const int FolderRecordLength = 24;
    private const int FileRecordLength = 16;

    /// <param name="files">Data-relative paths (e.g. <c>meshes\x\y.nif</c>) and their contents.</param>
    public static void Write(string path, IReadOnlyDictionary<string, byte[]> files)
    {
        var folders = files
            .Select(f => (Folder: Path.GetDirectoryName(f.Key.Replace('/', '\\'))!.ToLowerInvariant(), Name: Path.GetFileName(f.Key).ToLowerInvariant(), Data: f.Value))
            .GroupBy(f => f.Folder)
            .Select(g => (Name: g.Key, Files: g.ToList()))
            .ToList();
        var allFiles = folders.SelectMany(f => f.Files).ToList();
        var totalFolderNameLength = folders.Sum(f => f.Name.Length + 1);
        var totalFileNameLength = allFiles.Sum(f => f.Name.Length + 1);

        // Layout: header, folder records, per folder (name + file records), file names, file data.
        var fileRecordBlocks = HeaderLength + (FolderRecordLength * folders.Count);
        var dataStart = fileRecordBlocks + folders.Sum(f => 1 + f.Name.Length + 1 + (FileRecordLength * f.Files.Count)) + totalFileNameLength;

        using var output = new BinaryWriter(File.Create(path), Encoding.ASCII);
        output.Write("BSA\0"u8);
        output.Write(105u);
        output.Write((uint)HeaderLength);
        output.Write(0x1u | 0x2u); // folder names, file names
        output.Write((uint)folders.Count);
        output.Write((uint)allFiles.Count);
        output.Write((uint)totalFolderNameLength);
        output.Write((uint)totalFileNameLength);
        output.Write(0u); // file flags

        var blockOffset = fileRecordBlocks;
        foreach (var (name, folderFiles) in folders)
        {
            output.Write(0ul); // hash
            output.Write((uint)folderFiles.Count);
            output.Write(0u);
            output.Write((ulong)(blockOffset + totalFileNameLength));
            blockOffset += 1 + name.Length + 1 + (FileRecordLength * folderFiles.Count);
        }

        var dataOffset = dataStart;
        foreach (var (name, folderFiles) in folders)
        {
            output.Write((byte)(name.Length + 1));
            output.Write(Encoding.ASCII.GetBytes(name));
            output.Write((byte)0);
            foreach (var file in folderFiles)
            {
                output.Write(0ul); // hash
                output.Write((uint)file.Data.Length);
                output.Write((uint)dataOffset);
                dataOffset += file.Data.Length;
            }
        }

        foreach (var file in allFiles)
        {
            output.Write(Encoding.ASCII.GetBytes(file.Name));
            output.Write((byte)0);
        }
        foreach (var file in allFiles) output.Write(file.Data);
    }
}
