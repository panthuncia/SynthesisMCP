using System.Buffers.Binary;
using System.Text;

namespace SafePatch.Protocol.Tests;

public class FrameChannelTests
{
    public static TheoryData<Message> AllMessages() =>
    [
        new Hello(ProtocolVersion.Current, "abc"),
        new Start([1, 2, 3], ["run-patcher", "--GameRelease", "SkyrimSE"],
            [new SharedFile(@"C:\Data\Skyrim.esm", Handle: 1234), new SharedFile(@"C:\plugins.txt", Contents: [42])]),
        new Start([1], [], [], SettingsPath: "settings.json"),
        new Start([1], [], [], GameIniPath: @"C:\Users\u\Documents\My Games\Skyrim Special Edition\Skyrim.ini"),
        new AssetRequest(@"meshes\x.nif"),
        new AssetReply(5678),
        new AssetReply(null),
        new Submit([[1, 2, 3]], "log"),
        new Submit([[1], [2, 3]], "log", Persistence: [7, 8]),
        new Failed("boom", "log"),
        new Result(true, []),
        new Result(false, ["rejected"]),
    ];

    [Theory]
    [MemberData(nameof(AllMessages))]
    public void Messages_round_trip(Message message)
    {
        var decoded = RoundTrip(message);

        Assert.Equal(message.GetType(), decoded.GetType());
        Assert.Equal(Serialize(message), Serialize(decoded));
    }

    [Fact]
    public void Receive_returns_null_on_clean_end_of_stream()
    {
        var channel = new FrameChannel(new MemoryStream(), Stream.Null);
        Assert.Null(channel.Receive());
    }

    [Fact]
    public void Receive_typed_rejects_the_wrong_message()
    {
        var channel = ChannelOver(Frame(Serialize(new Failed("x", ""))));
        Assert.Throws<ProtocolException>(() => channel.Receive<Hello>());
    }

    [Theory]
    [InlineData("""{"$kind":"hello","protocolVersion":1,"nonce":"n","extra":1}""")]            // unknown member
    [InlineData("""{"$kind":"launchMissiles"}""")]                                                // unknown discriminator
    [InlineData("""{"protocolVersion":1,"nonce":"n"}""")]                                         // missing discriminator
    [InlineData("""{"$kind":"hello","protocolVersion":1}""")]                                     // missing required value
    [InlineData("""{"$kind":"hello","protocolVersion":1,"nonce":null}""")]                        // null for non-nullable
    [InlineData("""{"$kind":"submit","outputPlugins":["not base64!"],"log":""}""")]             // bad bytes
    [InlineData("""{"$kind":"start","program":"","arguments":[],"files":[{"handle":1}]}""")]      // file without a path
    [InlineData("""{"$kind":"result","accepted":"yes","errors":[]}""")]                          // wrong type
    [InlineData("""[1,2,3]""")]
    [InlineData("""not json""")]
    [InlineData("""null""")]
    public void Malformed_messages_are_rejected(string json)
    {
        var channel = ChannelOver(Frame(Encoding.UTF8.GetBytes(json)));
        Assert.Throws<ProtocolException>(() => channel.Receive());
    }

    [Fact]
    public void Deeply_nested_json_is_rejected()
    {
        var json = """{"$kind":"failed","error":"x","nested":""" + new string('[', 100) + new string(']', 100) + "}";
        var channel = ChannelOver(Frame(Encoding.UTF8.GetBytes(json)));
        Assert.Throws<ProtocolException>(() => channel.Receive());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(1025)]
    public void Frame_lengths_outside_the_limit_are_rejected(int length)
    {
        var header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, length);
        var channel = new FrameChannel(new MemoryStream(header), Stream.Null, maxFrameBytes: 1024);
        Assert.Throws<ProtocolException>(() => channel.Receive());
    }

    [Fact]
    public void Truncated_header_and_payload_are_rejected()
    {
        Assert.Throws<ProtocolException>(() => ChannelOver([1, 0]).Receive());
        Assert.Throws<ProtocolException>(() => ChannelOver([10, 0, 0, 0, (byte)'{']).Receive());
    }

    [Fact]
    public void Oversized_outgoing_frames_are_refused()
    {
        var channel = new FrameChannel(Stream.Null, new MemoryStream(), maxFrameBytes: 16);
        Assert.Throws<ProtocolException>(() => channel.Send(new Failed(new string('x', 100), "")));
    }

    private static Message RoundTrip(Message message)
    {
        var buffer = new MemoryStream();
        new FrameChannel(Stream.Null, buffer).Send(message);
        buffer.Position = 0;
        return new FrameChannel(buffer, Stream.Null).Receive()!;
    }

    private static byte[] Serialize(Message m) => System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(m, FrameChannel.JsonOptions);

    private static byte[] Frame(byte[] payload)
    {
        var frame = new byte[payload.Length + 4];
        BinaryPrimitives.WriteInt32LittleEndian(frame, payload.Length);
        payload.CopyTo(frame, 4);
        return frame;
    }

    private static FrameChannel ChannelOver(byte[] bytes) => new(new MemoryStream(bytes), Stream.Null);
}
