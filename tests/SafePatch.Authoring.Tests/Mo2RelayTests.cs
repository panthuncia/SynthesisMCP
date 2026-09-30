using System.IO.Pipes;
using System.Text;
using SafePatch.Authoring.Sources;

namespace SafePatch.Authoring.Tests;

/// <summary>
/// The relay between the copy of SafePatch a client starts and the copy MO2 runs, over a real named pipe in one
/// process. Starting through MO2 itself is covered by the MO2 end-to-end tests.
/// </summary>
public sealed class Mo2RelayTests
{
    [Fact]
    public async Task Carries_input_output_error_and_the_exit_code()
    {
        var name = $"SafePatch.Mo2RelayTests.{Guid.NewGuid():N}";
        using var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        using var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        client.Connect(5000);
        server.WaitForConnection();

        // The inside copy: echoes its input in upper case, notes on error how much it read, and exits with 3.
        var inside = Task.Run(() =>
        {
            using var endpoint = new Mo2Relay.Endpoint(client);
            var text = new StreamReader(endpoint.Input).ReadToEnd();
            endpoint.Output.Write(Encoding.UTF8.GetBytes(text.ToUpperInvariant()));
            endpoint.Error.Write(Encoding.UTF8.GetBytes($"read {text.Length}"));
            endpoint.Exit(3);
        }, TestContext.Current.CancellationToken);

        // Larger than one frame, so it is split and put back together.
        var input = new string('a', 200_000) + "\nend";
        var output = new MemoryStream();
        var error = new MemoryStream();
        var code = Mo2Relay.Relay(server, new MemoryStream(Encoding.UTF8.GetBytes(input)), output, error);
        await inside.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        Assert.Equal(3, code);
        Assert.Equal(input.ToUpperInvariant(), Encoding.UTF8.GetString(output.ToArray()));
        Assert.Equal($"read {input.Length}", Encoding.UTF8.GetString(error.ToArray()));
    }

    [Fact]
    public void Reports_an_inside_copy_that_ends_without_an_exit_code()
    {
        var name = $"SafePatch.Mo2RelayTests.{Guid.NewGuid():N}";
        using var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        client.Connect(5000);
        server.WaitForConnection();
        client.Dispose();

        var error = new MemoryStream();
        Assert.Equal(1, Mo2Relay.Relay(server, new MemoryStream(), new MemoryStream(), error));
        Assert.Contains("ended without reporting how", Encoding.UTF8.GetString(error.ToArray()));
    }

    [Theory]
    [InlineData("plain")]
    [InlineData("with space")]
    [InlineData(@"C:\Program Files\x\")]
    [InlineData("quote\"inside")]
    [InlineData(@"back\\""slash")]
    [InlineData("")]
    public void Quotes_arguments_so_they_read_back_unchanged(string argument)
    {
        var line = Mo2Relay.CommandLine(["first", argument, "last"]);
        Assert.Equal(["first", argument, "last"], CommandLineToArgs(line));
    }

    /// <summary>Windows's own parser, as a started program reads its command line.</summary>
    private static string[] CommandLineToArgs(string line)
    {
        var pointer = CommandLineToArgvW("program.exe " + line, out var count);
        try
        {
            return [.. Enumerable.Range(1, count - 1).Select(i => System.Runtime.InteropServices.Marshal.PtrToStringUni(System.Runtime.InteropServices.Marshal.ReadIntPtr(pointer, i * IntPtr.Size))!)];
        }
        finally
        {
            LocalFree(pointer);
        }
    }

    [System.Runtime.InteropServices.DllImport("shell32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CommandLineToArgvW(string commandLine, out int count);

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);
}
