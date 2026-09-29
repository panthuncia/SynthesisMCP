using System.Buffers.Binary;
using System.Text;

namespace SafePatch.Protocol.Tests;

/// <summary>
/// Feeds <see cref="FrameChannel"/> mutated and random frames. Whatever arrives, receiving must either
/// return a message that re-encodes cleanly or throw <see cref="ProtocolException"/>; any other
/// exception is a bug a hostile peer could use. Seeded, so a failure reproduces; set
/// <c>SAFEPATCH_FUZZ_ITERATIONS</c> to run longer.
/// </summary>
[Trait("Category", "Fuzz")]
public class FrameChannelFuzzTests
{
    private const int Seed = 20260929;

    private static int Iterations =>
        int.TryParse(Environment.GetEnvironmentVariable("SAFEPATCH_FUZZ_ITERATIONS"), out var n) && n > 0 ? n : 20_000;

    private static readonly byte[][] Corpus =
    [
        .. FrameChannelTests.AllMessages().Select(row => JsonSerializerBytes((Message)row.Data)),
        .. new[]
        {
            """{"$kind":"submit","outputPlugins":[null],"log":""}""",
            """{"$kind":"start","program":"AQ==","arguments":[null],"files":[]}""",
            """{"$kind":"assetRequest","path":"\u0000..\\..\\x"}""",
            """{"$kind":"hello","protocolVersion":2147483647,"nonce":""}""",
        }.Select(Encoding.UTF8.GetBytes),
    ];

    /// <summary>JSON fragments spliced in place of a value.</summary>
    private static readonly string[] Values =
    [
        "null", "true", "0", "-1", "2147483648", "1e999", "\"\"", "\"\\u0000\"", "[]", "{}", "[[[[[[[[]]]]]]]]",
        "\"AAAA\"", "\"not base64\"", "{\"$kind\":\"hello\"}", "\"\\ud800\"", new string('9', 400),
    ];

    [Fact]
    public void Mutated_frames_are_decoded_or_rejected_as_protocol_errors()
    {
        var random = new Random(Seed);
        for (var i = 0; i < Iterations; i++)
        {
            var payload = Mutate(random, Corpus[random.Next(Corpus.Length)]);
            AssertSafe(Frame(payload), i);
        }
    }

    [Fact]
    public void Random_bytes_and_bad_headers_are_rejected_as_protocol_errors()
    {
        var random = new Random(Seed + 1);
        for (var i = 0; i < Iterations; i++)
        {
            var bytes = new byte[random.Next(0, 64)];
            random.NextBytes(bytes);
            // Sometimes a plausible header over random or truncated contents.
            if (bytes.Length >= 4 && random.Next(2) == 0)
                BinaryPrimitives.WriteInt32LittleEndian(bytes, random.Next(-8, bytes.Length + 8));
            AssertSafe(bytes, i);
        }
    }

    [Fact]
    public void Several_frames_in_a_row_are_read_until_the_first_bad_one()
    {
        var random = new Random(Seed + 2);
        for (var i = 0; i < Iterations / 10; i++)
        {
            var stream = new MemoryStream();
            for (var frames = random.Next(1, 5); frames > 0; frames--)
            {
                var payload = Corpus[random.Next(Corpus.Length)];
                stream.Write(Frame(random.Next(3) == 0 ? Mutate(random, payload) : payload));
            }
            AssertSafe(stream.ToArray(), i);
        }
    }

    private static void AssertSafe(byte[] bytes, int iteration)
    {
        var channel = new FrameChannel(new MemoryStream(bytes), Stream.Null, maxFrameBytes: 1 << 20);
        try
        {
            while (channel.Receive() is { } message)
            {
                // Whatever decodes must be a well-formed message: it re-encodes and decodes to the same thing.
                var encoded = JsonSerializerBytes(message);
                Assert.Equal(encoded, JsonSerializerBytes(FrameChannel.Decode(encoded)));
            }
        }
        catch (ProtocolException)
        {
        }
        catch (Exception e)
        {
            Assert.Fail($"Iteration {iteration}: {e.GetType().Name} escaped for input {Convert.ToBase64String(bytes)}\n{e}");
        }
    }

    private static byte[] Mutate(Random random, byte[] original)
    {
        var bytes = new List<byte>(original);
        for (var edits = random.Next(1, 4); edits > 0; edits--)
        {
            switch (random.Next(6))
            {
                case 0 when bytes.Count > 0: // flip a bit
                    var at = random.Next(bytes.Count);
                    bytes[at] ^= (byte)(1 << random.Next(8));
                    break;
                case 1 when bytes.Count > 0: // delete a run
                    var start = random.Next(bytes.Count);
                    bytes.RemoveRange(start, Math.Min(random.Next(1, 8), bytes.Count - start));
                    break;
                case 2: // insert random bytes
                    var insert = new byte[random.Next(1, 8)];
                    random.NextBytes(insert);
                    bytes.InsertRange(random.Next(bytes.Count + 1), insert);
                    break;
                case 3 when bytes.Count > 0: // truncate
                    var keep = random.Next(bytes.Count);
                    bytes.RemoveRange(keep, bytes.Count - keep);
                    break;
                case 4: // replace a value after some colon with another JSON value
                    var text = Encoding.UTF8.GetString([.. bytes]);
                    var colons = Enumerable.Range(0, text.Length).Where(c => text[c] == ':').ToList();
                    if (colons.Count == 0) break;
                    var colon = colons[random.Next(colons.Count)];
                    var end = text.IndexOfAny([',', '}', ']'], colon + 1);
                    if (end < 0) end = text.Length;
                    text = text[..(colon + 1)] + Values[random.Next(Values.Length)] + text[end..];
                    bytes = [.. Encoding.UTF8.GetBytes(text)];
                    break;
                default: // splice in part of another corpus entry
                    var other = Corpus[random.Next(Corpus.Length)];
                    var from = random.Next(other.Length);
                    bytes.InsertRange(random.Next(bytes.Count + 1), other.Skip(from).Take(random.Next(1, 32)));
                    break;
            }
        }
        return [.. bytes];
    }

    private static byte[] JsonSerializerBytes(Message message) => System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(message, FrameChannel.JsonOptions);

    private static byte[] Frame(byte[] payload)
    {
        var frame = new byte[payload.Length + 4];
        BinaryPrimitives.WriteInt32LittleEndian(frame, payload.Length);
        payload.CopyTo(frame, 4);
        return frame;
    }
}
