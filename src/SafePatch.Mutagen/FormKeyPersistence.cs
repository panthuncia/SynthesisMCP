using Mutagen.Bethesda.Plugins;
using SafePatch.Host;

namespace SafePatch.Mutagen;

/// <summary>
/// Synthesis's shared FormKey allocation state (Mutagen's <c>TextFileSharedFormKeyAllocator</c>):
/// a folder with a marker file and one text file per patcher, listing EditorID and FormID lines.
/// It keeps new records' FormKeys stable across runs. The worker's allocator updates this
/// patcher's file inside the sandbox; the host checks that file against the records it accepted
/// and writes it itself.
/// </summary>
public sealed class FormKeyPersistence
{
    public const string MarkerFileName = "TextFileSharedFormKeyAllocator.marker";

    private readonly string _folder;
    private readonly string _patcherName;

    private FormKeyPersistence(string folder, string patcherName)
    {
        _folder = folder;
        _patcherName = patcherName;
    }

    /// <summary>The run's persistence, from Synthesis's arguments; null when the run does not use it (as Synthesis decides).</summary>
    public static FormKeyPersistence? FromArguments(string? persistencePath, string? patcherName)
    {
        if (persistencePath is null || patcherName is null || !File.Exists(Path.Combine(persistencePath, MarkerFileName))) return null;
        if (patcherName.Length == 0 || patcherName.IndexOfAny([.. Path.GetInvalidFileNameChars()]) >= 0 || patcherName is "." or "..")
            throw new SafePatchException($"Patcher name '{patcherName}' cannot name a persistence file.");
        return new FormKeyPersistence(persistencePath, patcherName);
    }

    /// <summary>The files to share with the worker so its allocator sees the same state.</summary>
    public static IEnumerable<string> Files(string persistencePath) =>
        File.Exists(Path.Combine(persistencePath, MarkerFileName))
            ? Directory.EnumerateFiles(persistencePath, "*.txt").Prepend(Path.Combine(persistencePath, MarkerFileName))
            : [];

    private string OwnFile => Path.Combine(_folder, _patcherName + ".txt");

    /// <summary>
    /// Checks the worker's updated file: every entry is either already this patcher's, unchanged,
    /// or names a new record the host accepted, by its EditorID and FormKey; and nothing collides
    /// with another patcher's entries.
    /// </summary>
    /// <returns>The entries to write.</returns>
    internal IReadOnlyList<(string EditorId, uint Id)> Validate(byte[] updated, IReadOnlyDictionary<FormKey, string?> acceptedNew, ModKey patchKey)
    {
        var entries = Parse(new MemoryStream(updated), "the worker's persistence file");
        var own = File.Exists(OwnFile) ? Parse(File.OpenRead(OwnFile), OwnFile).ToDictionary(e => e.EditorId, e => e.Id) : [];
        var others = Directory.EnumerateFiles(_folder, "*.txt")
            .Where(f => !string.Equals(f, OwnFile, StringComparison.OrdinalIgnoreCase))
            .SelectMany(f => Parse(File.OpenRead(f), f))
            .ToList();
        var otherIds = others.Select(e => e.Id).ToHashSet();
        var otherEditorIds = others.Select(e => e.EditorId).ToHashSet(StringComparer.Ordinal);

        var errors = new List<string>();
        foreach (var (editorId, id) in entries)
        {
            var key = new FormKey(patchKey, id);
            if (otherEditorIds.Contains(editorId) || otherIds.Contains(id))
                errors.Add($"persistence entry {editorId} = {key} collides with another patcher's.");
            else if (!(own.TryGetValue(editorId, out var ownId) && ownId == id)
                     && !(acceptedNew.TryGetValue(key, out var accepted) && accepted == editorId))
                errors.Add($"persistence entry {editorId} = {key} names no new record in this patch.");
        }
        foreach (var (editorId, id) in own.Where(o => !entries.Any(e => e.EditorId == o.Key)).Select(o => (o.Key, o.Value)))
            errors.Add($"persistence entry {editorId} = {new FormKey(patchKey, id)} was dropped.");
        if (errors.Count > 0)
            throw new PatchRejectedException($"FormKey persistence rejected with {errors.Count} error(s):{Environment.NewLine}{string.Join(Environment.NewLine, errors.Take(50))}");
        return entries;
    }

    /// <summary>Writes this patcher's file as Mutagen does: to a temporary file, then replacing the old one.</summary>
    internal void Write(IReadOnlyList<(string EditorId, uint Id)> entries)
    {
        var temp = Path.Combine(_folder, _patcherName + ".tmp");
        using (var writer = new StreamWriter(temp))
        {
            foreach (var (editorId, id) in entries)
            {
                writer.WriteLine(editorId);
                writer.WriteLine(id);
            }
        }
        if (File.Exists(OwnFile)) File.Replace(temp, OwnFile, null);
        else File.Move(temp, OwnFile);
    }

    private static List<(string EditorId, uint Id)> Parse(Stream stream, string name)
    {
        var entries = new List<(string, uint)>();
        var editorIds = new HashSet<string>(StringComparer.Ordinal);
        var ids = new HashSet<uint>();
        using var reader = new StreamReader(stream);
        while (reader.ReadLine() is { } editorId)
        {
            if (reader.ReadLine() is not { } idText || !uint.TryParse(idText, out var id) || id > 0xFFFFFF
                || editorId.Length == 0 || !editorIds.Add(editorId) || !ids.Add(id))
                throw new PatchRejectedException($"FormKey persistence file {name} is malformed near '{editorId}'.");
            entries.Add((editorId, id));
        }
        return entries;
    }
}
