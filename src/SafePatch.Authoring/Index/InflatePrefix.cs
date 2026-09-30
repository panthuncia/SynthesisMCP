namespace SafePatch.Authoring.Index;

/// <summary>
/// Decompresses only the beginning of a zlib stream (RFC 1950/1951), stopping once the output buffer is full. A
/// compressed record's EditorID is its first subrecord, so a few dozen bytes answer the question, where a full
/// inflate (and setting up zlib for it) costs the whole record. Canonical Huffman decoding after Mark Adler's
/// <c>puff.c</c>, zlib's reference decoder: no tables beyond symbol counts, no window, nothing allocated.
/// </summary>
internal static class InflatePrefix
{
    private const int MaxBits = 15;
    private const int MaxLiteralCodes = 286;
    private const int MaxDistanceCodes = 30;
    private const int FixedLiteralCodes = 288;

    private static readonly short[] LengthBase = [3, 4, 5, 6, 7, 8, 9, 10, 11, 13, 15, 17, 19, 23, 27, 31, 35, 43, 51, 59, 67, 83, 99, 115, 131, 163, 195, 227, 258];
    private static readonly short[] LengthExtra = [0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 1, 1, 2, 2, 2, 2, 3, 3, 3, 3, 4, 4, 4, 4, 5, 5, 5, 5, 0];
    private static readonly short[] DistanceBase = [1, 2, 3, 4, 5, 7, 9, 13, 17, 25, 33, 49, 65, 97, 129, 193, 257, 385, 513, 769, 1025, 1537, 2049, 3073, 4097, 6145, 8193, 12289, 16385, 24577];
    private static readonly short[] DistanceExtra = [0, 0, 0, 0, 1, 1, 2, 2, 3, 3, 4, 4, 5, 5, 6, 6, 7, 7, 8, 8, 9, 9, 10, 10, 11, 11, 12, 12, 13, 13];
    private static readonly byte[] CodeLengthOrder = [16, 17, 18, 0, 8, 7, 9, 6, 10, 5, 11, 4, 12, 3, 13, 2, 14, 1, 15];

    private static readonly Huffman FixedLiterals;
    private static readonly Huffman FixedDistances;

    static InflatePrefix()
    {
        Span<short> lengths = stackalloc short[FixedLiteralCodes];
        for (var i = 0; i < 144; i++) lengths[i] = 8;
        for (var i = 144; i < 256; i++) lengths[i] = 9;
        for (var i = 256; i < 280; i++) lengths[i] = 7;
        for (var i = 280; i < FixedLiteralCodes; i++) lengths[i] = 8;
        FixedLiterals = Huffman.Build(lengths);
        lengths = lengths[..MaxDistanceCodes];
        lengths.Fill(5);
        FixedDistances = Huffman.Build(lengths);
    }

    /// <summary>Fills <paramref name="output"/> from the start of a zlib stream.</summary>
    /// <returns>Bytes written: all of <paramref name="output"/>, fewer if the stream ends first, or -1 if it is not valid.</returns>
    public static int Inflate(ReadOnlySpan<byte> zlib, Span<byte> output)
    {
        // zlib header: deflate method, a check value, and no preset dictionary.
        if (zlib.Length < 2 || (zlib[0] & 0x0F) != 8 || ((zlib[0] << 8) | zlib[1]) % 31 != 0 || (zlib[1] & 0x20) != 0) return -1;
        if (output.IsEmpty) return 0;
        var state = new State(zlib[2..], output);
        try
        {
            bool last;
            do
            {
                last = state.Bits(1) == 1;
                var done = state.Bits(2) switch
                {
                    0 => state.Stored(),
                    1 => state.Codes(FixedLiterals, FixedDistances),
                    2 => state.Dynamic(),
                    _ => throw new InvalidDataException("Invalid deflate block type."),
                };
                if (done) break;
            }
            while (!last);
            return state.Written;
        }
        catch (InvalidDataException)
        {
            return -1;
        }
    }

    /// <summary>A canonical Huffman code as puff keeps it: how many codes of each length, and the symbols in code order.</summary>
    private sealed class Huffman
    {
        public readonly short[] Counts = new short[MaxBits + 1];
        public readonly short[] Symbols;

        private Huffman(int symbols) => Symbols = new short[symbols];

        public static Huffman Build(ReadOnlySpan<short> lengths)
        {
            var huffman = new Huffman(lengths.Length);
            huffman.Fill(lengths);
            return huffman;
        }

        /// <summary>Returns false if the lengths over-subscribe the code (an invalid stream).</summary>
        public bool Fill(ReadOnlySpan<short> lengths)
        {
            Array.Clear(Counts);
            foreach (var length in lengths) Counts[length]++;
            if (Counts[0] == lengths.Length) return true;
            var left = 1;
            for (var length = 1; length <= MaxBits; length++)
            {
                left = (left << 1) - Counts[length];
                if (left < 0) return false;
            }
            Span<short> offsets = stackalloc short[MaxBits + 1];
            for (var length = 1; length < MaxBits; length++) offsets[length + 1] = (short)(offsets[length] + Counts[length]);
            for (short symbol = 0; symbol < lengths.Length; symbol++)
            {
                if (lengths[symbol] != 0) Symbols[offsets[lengths[symbol]]++] = symbol;
            }
            return true;
        }
    }

    private ref struct State(ReadOnlySpan<byte> input, Span<byte> output)
    {
        private readonly ReadOnlySpan<byte> _input = input;
        private readonly Span<byte> _output = output;
        private int _position;
        private int _bitBuffer;
        private int _bitCount;

        public int Written { get; private set; }

        public int Bits(int need)
        {
            var value = _bitBuffer;
            while (_bitCount < need)
            {
                if (_position >= _input.Length) throw new InvalidDataException("Deflate stream ended early.");
                value |= _input[_position++] << _bitCount;
                _bitCount += 8;
            }
            _bitBuffer = value >> need;
            _bitCount -= need;
            return value & ((1 << need) - 1);
        }

        /// <summary>A stored block: copied as is. Returns true once the output is full.</summary>
        public bool Stored()
        {
            _bitBuffer = 0;
            _bitCount = 0;
            if (_position + 4 > _input.Length) throw new InvalidDataException("Deflate stream ended early.");
            var length = _input[_position] | (_input[_position + 1] << 8);
            if ((_input[_position + 2] | (_input[_position + 3] << 8)) != (~length & 0xFFFF)) throw new InvalidDataException("Stored block length check failed.");
            _position += 4;
            if (_position + length > _input.Length) throw new InvalidDataException("Deflate stream ended early.");
            var take = Math.Min(length, _output.Length - Written);
            _input.Slice(_position, take).CopyTo(_output[Written..]);
            _position += length;
            Written += take;
            return Written == _output.Length;
        }

        /// <summary>A block with its own code, described by the code lengths that follow.</summary>
        public bool Dynamic()
        {
            var literalCount = Bits(5) + 257;
            var distanceCount = Bits(5) + 1;
            var codeLengthCount = Bits(4) + 4;
            if (literalCount > MaxLiteralCodes || distanceCount > MaxDistanceCodes) throw new InvalidDataException("Too many deflate codes.");

            Span<short> lengths = stackalloc short[MaxLiteralCodes + MaxDistanceCodes];
            lengths.Clear();
            for (var i = 0; i < codeLengthCount; i++) lengths[CodeLengthOrder[i]] = (short)Bits(3);
            var codeLengths = Huffman.Build(lengths[..19]);
            if (codeLengths.Counts[0] == 19) throw new InvalidDataException("Empty code length code.");

            lengths.Clear();
            for (var index = 0; index < literalCount + distanceCount;)
            {
                var symbol = Decode(codeLengths);
                if (symbol < 16)
                {
                    lengths[index++] = (short)symbol;
                    continue;
                }
                short repeated = 0;
                int count;
                if (symbol == 16)
                {
                    if (index == 0) throw new InvalidDataException("Repeat with no previous length.");
                    repeated = lengths[index - 1];
                    count = 3 + Bits(2);
                }
                else
                {
                    count = symbol == 17 ? 3 + Bits(3) : 11 + Bits(7);
                }
                if (index + count > literalCount + distanceCount) throw new InvalidDataException("Too many code lengths.");
                while (count-- > 0) lengths[index++] = repeated;
            }
            if (lengths[256] == 0) throw new InvalidDataException("No end-of-block code.");

            var literals = Huffman.Build(lengths[..literalCount]);
            var distances = Huffman.Build(lengths.Slice(literalCount, distanceCount));
            return Codes(literals, distances);
        }

        /// <summary>Literals and back-references until the end of the block, or until the output is full.</summary>
        public bool Codes(Huffman literals, Huffman distances)
        {
            while (true)
            {
                var symbol = Decode(literals);
                if (symbol < 256)
                {
                    _output[Written++] = (byte)symbol;
                    if (Written == _output.Length) return true;
                    continue;
                }
                if (symbol == 256) return false;

                symbol -= 257;
                if (symbol >= LengthBase.Length) throw new InvalidDataException("Invalid length code.");
                var length = LengthBase[symbol] + Bits(LengthExtra[symbol]);
                var distanceSymbol = Decode(distances);
                if (distanceSymbol >= DistanceBase.Length) throw new InvalidDataException("Invalid distance code.");
                var distance = DistanceBase[distanceSymbol] + Bits(DistanceExtra[distanceSymbol]);
                if (distance > Written) throw new InvalidDataException("Distance before the start of the output.");
                // Copied a byte at a time: a match may overlap the bytes it produces.
                for (; length > 0; length--)
                {
                    _output[Written] = _output[Written - distance];
                    if (++Written == _output.Length) return true;
                }
            }
        }

        /// <summary>One symbol, reading its code a bit at a time (codes are stored most significant bit first).</summary>
        private int Decode(Huffman huffman)
        {
            int code = 0, first = 0, index = 0;
            for (var length = 1; length <= MaxBits; length++)
            {
                code |= Bits(1);
                var count = huffman.Counts[length];
                if (code - count < first) return huffman.Symbols[index + (code - first)];
                index += count;
                first = (first + count) << 1;
                code <<= 1;
            }
            throw new InvalidDataException("Invalid Huffman code.");
        }
    }
}
