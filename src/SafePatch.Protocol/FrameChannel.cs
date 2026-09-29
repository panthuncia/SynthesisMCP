using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SafePatch.Protocol;

public sealed class ProtocolException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Length-prefixed JSON frames over a pair of streams. Parsing is strict: unknown members,
/// unknown discriminators, missing required values, deep nesting and oversized frames are rejected.
/// </summary>
public sealed class FrameChannel(Stream input, Stream output, int maxFrameBytes = FrameChannel.DefaultMaxFrameBytes)
{
    // Large enough for the output plugin of a big patch; still bounded.
    public const int DefaultMaxFrameBytes = 512 * 1024 * 1024;

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        MaxDepth = 16,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) },
    };

    public void Send(Message message)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(message, JsonOptions);
        if (payload.Length > maxFrameBytes) throw new ProtocolException($"Outgoing frame of {payload.Length} bytes exceeds the limit.");
        Span<byte> header = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, payload.Length);
        output.Write(header);
        output.Write(payload);
        output.Flush();
    }

    /// <summary>Returns the next message, or null if the peer closed the channel cleanly.</summary>
    public Message? Receive()
    {
        Span<byte> header = stackalloc byte[4];
        var read = input.ReadAtLeast(header, 4, throwOnEndOfStream: false);
        if (read == 0) return null;
        if (read < 4) throw new ProtocolException("Truncated frame header.");

        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length <= 0 || length > maxFrameBytes) throw new ProtocolException($"Frame length {length} is out of range.");

        var payload = new byte[length];
        try
        {
            input.ReadExactly(payload);
        }
        catch (EndOfStreamException e)
        {
            throw new ProtocolException("Truncated frame.", e);
        }
        return Decode(payload);
    }

    /// <summary>Receives a message that must be of type <typeparamref name="T"/>.</summary>
    public T Receive<T>() where T : Message => Receive() switch
    {
        T message => message,
        null => throw new ProtocolException($"Channel closed while expecting {typeof(T).Name}."),
        var other => throw new ProtocolException($"Expected {typeof(T).Name} but received {other.GetType().Name}."),
    };

    public static Message Decode(ReadOnlySpan<byte> payload)
    {
        try
        {
            return JsonSerializer.Deserialize<Message>(payload, JsonOptions)
                   ?? throw new ProtocolException("Null message.");
        }
        catch (Exception e) when (e is JsonException or NotSupportedException or ArgumentException or FormatException)
        {
            throw new ProtocolException($"Malformed message: {e.Message}", e);
        }
    }
}
