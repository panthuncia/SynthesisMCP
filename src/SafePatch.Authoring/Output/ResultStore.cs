using SafePatch.Host;

namespace SafePatch.Authoring.Output;

/// <summary>
/// The results of a session's latest queries, by handle (<c>r1</c>, <c>r2</c>, …), for paging and export. Kept in
/// memory, the least recently used dropped first. A result belongs to the load order it was computed from: once
/// that is reloaded, its handle is refused rather than quietly describing a load order that has changed.
/// </summary>
public sealed class ResultStore(int capacity = 32)
{
    private readonly Lock _lock = new();
    private readonly LinkedList<(string Handle, ResultSet Set)> _recent = new();
    private int _next;

    public int Capacity => capacity;

    public string Add(ResultSet set)
    {
        lock (_lock)
        {
            var handle = $"r{++_next}";
            _recent.AddFirst((handle, set));
            while (_recent.Count > capacity) _recent.RemoveLast();
            return handle;
        }
    }

    /// <param name="generation">The current snapshot's generation; results from another are refused.</param>
    public ResultSet Get(string handle, int generation)
    {
        lock (_lock)
        {
            for (var node = _recent.First; node is not null; node = node.Next)
            {
                if (!string.Equals(node.Value.Handle, handle.Trim(), StringComparison.OrdinalIgnoreCase)) continue;
                if (node.Value.Set.Generation != 0 && node.Value.Set.Generation != generation)
                    throw new SafePatchException($"Result {handle} is from before the load order was reloaded; run the query again.");
                _recent.Remove(node);
                _recent.AddFirst(node);
                return node.Value.Set;
            }
        }
        throw new SafePatchException($"No result {handle}: only the last {capacity} results are kept. Run the query again.");
    }
}
