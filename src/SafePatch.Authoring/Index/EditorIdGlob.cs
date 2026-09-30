namespace SafePatch.Authoring.Index;

/// <summary>
/// An EditorID pattern (<c>*</c> any run, <c>?</c> any one character, case ignored), matched against the index's
/// EditorID bytes without decoding them to strings: a glob over every record in a load order tests over a million.
/// </summary>
internal sealed class EditorIdGlob
{
    private const byte AnyRun = (byte)'*';
    private const byte AnyOne = (byte)'?';

    /// <summary>Each Latin-1 byte's upper case, where that is Latin-1 too. EditorIDs are Latin-1, as the index reads them.</summary>
    private static readonly byte[] Fold = [.. Enumerable.Range(0, 256).Select(b => char.ToUpperInvariant((char)b) is var upper && upper < 256 ? (byte)upper : (byte)b)];

    private readonly byte[] _pattern;

    public EditorIdGlob(string glob)
    {
        // A character outside Latin-1 can be in no EditorID the index holds.
        _pattern = [.. glob.Select(c => c < 256 ? Fold[c] : (byte)0)];
        Impossible = glob.Any(c => c >= 256 || c == 0);
    }

    /// <summary>True when no EditorID can match (the pattern has a character no EditorID has).</summary>
    private bool Impossible { get; }

    /// <summary>
    /// Whether <paramref name="text"/> matches: the usual greedy wildcard match, linear in practice and at worst the
    /// pattern's length times the text's. A <c>*</c> first matches nothing, and on a later mismatch the match resumes
    /// one character further on from the last <c>*</c>. Only the last <c>*</c> needs remembering, because any earlier
    /// one could absorb whatever a retry of it would.
    /// </summary>
    public bool IsMatch(ReadOnlySpan<byte> text)
    {
        if (Impossible) return false;
        var pattern = _pattern.AsSpan();
        int p = 0, t = 0, star = -1, resume = 0;
        while (t < text.Length)
        {
            if (p < pattern.Length && pattern[p] == AnyRun)
            {
                star = p++;
                resume = t;
            }
            else if (p < pattern.Length && (pattern[p] == AnyOne || pattern[p] == Fold[text[t]]))
            {
                p++;
                t++;
            }
            else if (star >= 0)
            {
                p = star + 1;
                t = ++resume;
            }
            else
            {
                return false;
            }
        }
        while (p < pattern.Length && pattern[p] == AnyRun) p++;
        return p == pattern.Length;
    }
}
