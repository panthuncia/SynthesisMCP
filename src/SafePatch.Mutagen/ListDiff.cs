using System.Collections;
using Loqui;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Strings;
using Noggog;

namespace SafePatch.Mutagen;

/// <summary>
/// Element-level differences between two versions of a list field, compared by value: record parts by their generated
/// equals masks (<see cref="RecordDiff.ValuesEqual"/>), links by FormKey, byte arrays by their bytes. Lists are
/// multisets: a duplicate entry added is an addition. Order is ignored.
/// </summary>
public static class ListDiff
{
    public sealed record Result(IReadOnlyList<object> Added, IReadOnlyList<object> Removed)
    {
        public bool IsEmpty => Added.Count == 0 && Removed.Count == 0;
    }

    /// <summary>Whether a field value is a list this compares (not text or raw bytes).</summary>
    public static bool IsList(object? value) =>
        value is IEnumerable and not string and not ITranslatedStringGetter && value.GetType() is var type
        && !type.GetInterfaces().Any(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IEnumerable<>) && i.GetGenericArguments()[0] == typeof(byte));

    /// <summary>What <paramref name="after"/> added to and removed from <paramref name="before"/>. A null list is empty.</summary>
    public static Result Compare(IEnumerable? before, IEnumerable? after)
    {
        var remaining = Counts(before);
        var added = new List<object>();
        foreach (var item in Items(after))
        {
            if (!Take(remaining, item)) added.Add(item);
        }
        var removed = new List<object>();
        foreach (var item in Items(before))
        {
            // Each unmatched occurrence of an item counts once, in the order the list has them.
            if (Take(remaining, item)) removed.Add(item);
        }
        return new Result(added, removed);
    }

    /// <summary>The items of <paramref name="items"/> that <paramref name="list"/> holds, counting duplicates.</summary>
    public static IReadOnlyList<object> Present(IEnumerable<object> items, IEnumerable? list)
    {
        var remaining = Counts(list);
        return [.. items.Where(item => Take(remaining, item))];
    }

    /// <summary>The items of <paramref name="items"/> that <paramref name="list"/> lacks, counting duplicates.</summary>
    public static IReadOnlyList<object> Absent(IEnumerable<object> items, IEnumerable? list)
    {
        var remaining = Counts(list);
        return [.. items.Where(item => !Take(remaining, item))];
    }

    /// <summary>
    /// How many of each item a list holds, keyed by value (<see cref="ValueComparer"/>), so matching two lists is
    /// linear rather than quadratic.
    /// </summary>
    private static Dictionary<object, int> Counts(IEnumerable? list)
    {
        var counts = new Dictionary<object, int>(ValueComparer.Instance);
        foreach (var item in Items(list)) counts[item] = counts.GetValueOrDefault(item) + 1;
        return counts;
    }

    /// <summary>Uses up one occurrence of an item, if any is left.</summary>
    private static bool Take(Dictionary<object, int> counts, object item)
    {
        if (!counts.TryGetValue(item, out var count) || count == 0) return false;
        counts[item] = count - 1;
        return true;
    }

    private static List<object> Items(IEnumerable? list) => list?.Cast<object?>().OfType<object>().ToList() ?? [];

    /// <summary>
    /// Value equality for list items. Mutagen 0.54.4's own <c>GetHashCode</c> hashes nested lists and byte arrays by
    /// reference, so equal parts read or built apart (a rank placement's padding, an effect's conditions) usually hash
    /// differently. A part's hash here is built only from what equal parts share: its type, the FormKeys it links
    /// to, and its byte arrays' contents.
    /// </summary>
    private sealed class ValueComparer : IEqualityComparer<object>
    {
        public static readonly ValueComparer Instance = new();

        public new bool Equals(object? x, object? y) => (x, y) switch
        {
            (ILoquiObject a, ILoquiObject b) => RecordDiff.ValuesEqual(a, b),
            _ when Bytes(x) is { } a && Bytes(y) is { } b => a.Span.SequenceEqual(b.Span),
            _ => object.Equals(x, y),
        };

        /// <summary>A byte array in any of the forms Mutagen holds them (slices for the full parse and overlay).</summary>
        private static ReadOnlyMemorySlice<byte>? Bytes(object? item) => item switch
        {
            ReadOnlyMemorySlice<byte> slice => (ReadOnlyMemorySlice<byte>?)slice,
            MemorySlice<byte> slice => (ReadOnlyMemorySlice<byte>)slice,
            byte[] array => new ReadOnlyMemorySlice<byte>(array),
            _ => null,
        };

        public int GetHashCode(object item)
        {
            switch (item)
            {
                case ILoquiObject part:
                    var hash = new HashCode();
                    hash.Add(part.Registration.ClassType);
                    if (part is IFormLinkContainerGetter links)
                    {
                        foreach (var link in links.EnumerateFormLinks()) hash.Add(link.FormKey);
                    }
                    return hash.ToHashCode();
                case var _ when Bytes(item) is { } bytes:
                    var content = new HashCode();
                    content.AddBytes(bytes.Span);
                    return content.ToHashCode();
                default:
                    return item.GetHashCode();
            }
        }
    }
}
