using SafePatch.Authoring.Output;
using SafePatch.Authoring.Query;
using SafePatch.Authoring.Sources;
using SafePatch.Host;

namespace SafePatch.Authoring;

/// <summary>
/// A long-lived authoring session, such as an MCP server's: the load order (which can be read again), the results
/// of recent queries by handle, and how results reach the agent: rendered within a budget, paged, or exported.
/// Programs can be validated and packaged without a load order.
/// </summary>
/// <param name="workerMemoryBytes">The worker's memory limit for test runs and queries, when not the sandbox's default.</param>
public sealed class AuthoringSession(LoadOrderSnapshot? snapshot, IWorkerLauncher? launcher = null, ulong? workerMemoryBytes = null) : IDisposable
{
    private readonly Lock _lock = new();
    private LoadOrderSnapshot? _snapshot = snapshot;

    public ResultStore Results { get; } = new();

    public LoadOrderSnapshot Snapshot
    {
        get
        {
            lock (_lock)
            {
                return _snapshot ?? throw new SafePatchException(
                    "No load order: start the server with --data <Data folder> --plugins <plugins.txt>, or --mo2 <MO2 instance folder> [--profile <name>].");
            }
        }
    }

    private QueryService? _queries;

    /// <summary>Queries over the current snapshot. One is kept per snapshot, with what it has worked out (such as
    /// conflict analyses, which drilling into a summary's groups reuses).</summary>
    public QueryService Queries
    {
        get
        {
            var snapshot = Snapshot;
            lock (_lock)
            {
                if (_queries?.Snapshot != snapshot) _queries = new QueryService(snapshot);
                return _queries;
            }
        }
    }

    public AuthoringService Service => new(Snapshot, launcher, workerMemoryBytes);

    /// <summary>A result as text within <paramref name="budget"/> characters, kept under a handle for paging and export.</summary>
    public string Present(ResultSet set, int? budget = null)
    {
        var handle = Results.Add(set);
        return Budgeted.Render(set, 0, Budgeted.Clamp(budget), handle, Continuation(handle));
    }

    /// <summary>More of a kept result, from a row (or line) offset.</summary>
    public string ReadResults(string handle, int offset, int? budget = null) =>
        Budgeted.Render(Results.Get(handle, Snapshot.Generation), offset, Budgeted.Clamp(budget), handle.Trim(), Continuation(handle.Trim()));

    /// <summary>Writes a kept result to a new file, never inside the load order or its mod manager's folders.</summary>
    public string Export(string handle, string path, ExportFormat format)
    {
        var set = Results.Get(handle, Snapshot.Generation);
        GuardOutput(path);
        var (full, bytes) = ResultExport.Write(set, path, format);
        return $"Wrote {set.Rows.Count:N0} {(set.IsLines ? "lines" : "rows")} of {handle.Trim()} ({set.Title}) to {full} ({bytes:N0} bytes, {format.ToString().ToLowerInvariant()}).";
    }

    /// <summary>Reads the load order again from its source; results from before are no longer served.</summary>
    public string Reload()
    {
        lock (_lock)
        {
            var old = _snapshot ?? throw new SafePatchException("No load order to reload: the server was started without one.");
            _snapshot = old.Reopen();
            old.Dispose();
            var warnings = _snapshot.Resolved.Warnings;
            return $"Reloaded {_snapshot.Mods.Count} plugins from {_snapshot.Resolved.Description}." + (warnings.Count == 0 ? "" : " " + string.Join(" ", warnings));
        }
    }

    /// <summary>Refuses paths inside the Data folder, any of its layers (mod folders) or the mod manager's instance.</summary>
    public void GuardOutput(string path)
    {
        LoadOrderSnapshot? current;
        lock (_lock) current = _snapshot;
        GuardOutput(current, path);
    }

    /// <inheritdoc cref="GuardOutput(string)"/>
    public static void GuardOutput(LoadOrderSnapshot? current, string path)
    {
        if (current is null) return;
        var full = Path.GetFullPath(path);
        if (current.View.Contains(full) || current.Resolved.ProtectedFolders.Any(folder => Inside(full, folder)))
            throw new SafePatchException($"{full} is inside the load order's folders; write it somewhere else.");
    }

    public void Dispose()
    {
        lock (_lock) _snapshot?.Dispose();
    }

    /// <summary>Parses an option name such as <c>overrides</c> or <c>itm</c>, case-insensitively.</summary>
    public static T Option<T>(string? text, T fallback) where T : struct, Enum =>
        string.IsNullOrWhiteSpace(text) ? fallback
        : Enum.TryParse<T>(text.Trim(), ignoreCase: true, out var value) ? value
        : throw new SafePatchException($"{text} is not one of: {string.Join(", ", Enum.GetNames<T>().Select(n => n.ToLowerInvariant()))}.");

    private static string Continuation(string handle) => $"read_results(handle: \"{handle}\", offset: {{0}})";

    private static bool Inside(string path, string folder)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
        return path.Equals(root, StringComparison.OrdinalIgnoreCase) || path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }
}
