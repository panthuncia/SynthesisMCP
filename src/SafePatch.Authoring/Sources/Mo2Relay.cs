using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Text;
using SafePatch.Host;
using SafePatch.Sandbox.Windows;

namespace SafePatch.Authoring.Sources;

/// <summary>
/// Runs SafePatch inside Mod Organizer 2's virtual file system, as MO2 runs xEdit and Synthesis. An MCP client or a
/// shell starts SafePatch directly and talks to it over standard input and output, but a program MO2 starts is not
/// that process's child. So the process the client started (outside) asks MO2 to start a second copy of itself
/// (inside), with the same arguments, and relays its standard streams to it over a named pipe only the current user
/// can open. The inside copy checks it really runs under MO2, and the outside copy that the pipe's other end is it.
/// </summary>
public static class Mo2Relay
{
    /// <summary>Given to the inside copy: the pipe to reach the outside one through.</summary>
    public const string PipeOption = "--mo2-relay";

    /// <summary>Reads the MO2 profile without starting MO2, from outside its virtual file system.</summary>
    public const string NoVfsOption = "--no-vfs";

    /// <summary>How long MO2 may take to start SafePatch; MO2 may be starting itself, or showing a dialog.</summary>
    public static readonly TimeSpan StartTimeout = TimeSpan.FromMinutes(3);

    /// <summary>
    /// Whether this process should restart itself inside MO2: it was given an MO2 instance, is not already inside
    /// MO2's virtual file system, and was not told to read the instance without it.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static bool Needed(IReadOnlyList<string> arguments) =>
        arguments.Contains("--mo2") && !arguments.Contains(NoVfsOption) && !arguments.Contains(PipeOption) && !Mo2Vfs.Active;

    /// <summary>The value of a <c>--name value</c> option in a command line, or null.</summary>
    public static string? Option(IReadOnlyList<string> arguments, string name)
    {
        var index = arguments.ToList().IndexOf(name);
        return index >= 0 && index + 1 < arguments.Count ? arguments[index + 1] : null;
    }

    /// <summary>
    /// The outside copy: starts this program with <paramref name="arguments"/> through MO2 and relays standard input,
    /// output and error until it exits. Returns its exit code.
    /// </summary>
    /// <param name="modOrganizer">ModOrganizer.exe; by default the instance's own (a portable instance) or the installed one.</param>
    [SupportedOSPlatform("windows")]
    public static int Run(string instanceFolder, string? profile, string? modOrganizer, IReadOnlyList<string> arguments, Stream input, Stream output, Stream error)
    {
        var instance = Path.GetFullPath(File.Exists(instanceFolder) ? Path.GetDirectoryName(instanceFolder)! : instanceFolder);
        var exe = modOrganizer ?? FindModOrganizer(instance)
            ?? throw new SafePatchException($"Could not find ModOrganizer.exe for the MO2 instance {instance} (it is not a portable instance, and MO2 is not running). Pass --mo2-exe <path to ModOrganizer.exe>, or {NoVfsOption} to read the profile without MO2.");
        if (!File.Exists(exe)) throw new SafePatchException($"{exe} does not exist; --mo2-exe must name ModOrganizer.exe.");
        var (program, prefix) = Self();
        var pipeName = $"SafePatch.Mo2Relay.{Guid.NewGuid():N}";

        using var pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        var start = new ProcessStartInfo(exe) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(exe)! };
        // A global instance is named by its folder under %LOCALAPPDATA%\ModOrganizer; a portable one is found by the exe.
        if (!File.Exists(Path.Combine(instance, "ModOrganizer.exe")))
        {
            start.ArgumentList.Add("-i");
            start.ArgumentList.Add(Path.GetFileName(instance));
        }
        if (profile is not null)
        {
            start.ArgumentList.Add("-p");
            start.ArgumentList.Add(profile);
        }
        start.ArgumentList.Add("run");
        start.ArgumentList.Add(program);
        start.ArgumentList.Add("-a");
        start.ArgumentList.Add(CommandLine([.. prefix, .. arguments, PipeOption, pipeName]));
        // Relative paths in the arguments (a program to test, a file to export to) mean the same inside.
        start.ArgumentList.Add("-c");
        start.ArgumentList.Add(Environment.CurrentDirectory);

        using var mo2 = Process.Start(start) ?? throw new SafePatchException($"Could not start {exe}.");
        using (var timeout = new CancellationTokenSource(StartTimeout))
        {
            var connecting = pipe.WaitForConnectionAsync(timeout.Token);
            var exited = mo2.WaitForExitAsync(timeout.Token);
            try
            {
                // MO2 exits at once when it hands the program to an MO2 already running; only a failure is final.
                if (Task.WhenAny(connecting, exited).GetAwaiter().GetResult() == exited && !connecting.IsCompleted && mo2.ExitCode != 0)
                    throw new SafePatchException($"Mod Organizer 2 did not start SafePatch (exit code {mo2.ExitCode}). Its log is in the instance's logs folder.");
                connecting.GetAwaiter().GetResult();
            }
            catch (OperationCanceledException)
            {
                throw new SafePatchException($"Mod Organizer 2 did not start SafePatch within {StartTimeout.TotalMinutes:0} minutes. Check MO2 for a dialog waiting for an answer.");
            }
        }
        var client = Process.GetProcessById(Mo2Vfs.ClientProcessId(pipe));
        if (!string.Equals(client.MainModule?.FileName, program, StringComparison.OrdinalIgnoreCase))
            throw new SafePatchException($"A process other than SafePatch ({client.MainModule?.FileName}) connected to its relay pipe; stopping.");

        return Relay(pipe, input, output, error);
    }

    /// <summary>The outside copy's side of a connected relay: forwards input, and writes out what comes back.</summary>
    internal static int Relay(Stream pipe, Stream input, Stream output, Stream error)
    {
        var frames = new RelayFrames(pipe);
        _ = Task.Run(async () =>
        {
            var buffer = new byte[RelayFrames.MaxPayload];
            try
            {
                int read;
                while ((read = await input.ReadAsync(buffer)) > 0) await frames.WriteAsync(RelayFrames.Input, buffer.AsMemory(0, read));
                await frames.WriteAsync(RelayFrames.InputEnd, ReadOnlyMemory<byte>.Empty);
            }
            catch (IOException)
            {
                // The inside copy has gone; the read loop below reports how it ended.
            }
        });
        int? exitCode = null;
        while (frames.Read() is { } frame)
        {
            switch (frame.Kind)
            {
                case RelayFrames.Output:
                    output.Write(frame.Payload.Span);
                    output.Flush();
                    break;
                case RelayFrames.Error:
                    error.Write(frame.Payload.Span);
                    error.Flush();
                    break;
                case RelayFrames.Exit:
                    exitCode = BitConverter.ToInt32(frame.Payload.Span);
                    break;
                default:
                    throw new SafePatchException($"SafePatch under MO2 sent an unknown relay frame ({frame.Kind}).");
            }
        }
        if (exitCode is null)
        {
            var message = Encoding.UTF8.GetBytes("SafePatch under Mod Organizer 2 ended without reporting how.\n");
            error.Write(message);
            return 1;
        }
        return exitCode.Value;
    }

    /// <summary>The inside copy's end of the relay. Fails closed unless this process runs inside MO2's virtual file system.</summary>
    [SupportedOSPlatform("windows")]
    public static Endpoint Connect(string pipeName)
    {
        if (!Mo2Vfs.Active) throw new SafePatchException("SafePatch was asked to run inside Mod Organizer 2, but MO2's virtual file system is not loaded.");
        var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        pipe.Connect((int)TimeSpan.FromSeconds(30).TotalMilliseconds);
        // MO2 gives a console program its own window; the relay carries everything instead.
        Mo2Vfs.DetachConsole();
        return new Endpoint(pipe);
    }

    /// <summary>The inside copy's standard streams, carried by the relay pipe.</summary>
    public sealed class Endpoint : IDisposable
    {
        private readonly Stream _pipe;
        private readonly RelayFrames _frames;

        internal Endpoint(Stream pipe)
        {
            _pipe = pipe;
            _frames = new RelayFrames(pipe);
            Input = new InputStream(_frames);
            Output = new OutputStream(_frames, RelayFrames.Output);
            Error = new OutputStream(_frames, RelayFrames.Error);
        }

        public Stream Input { get; }
        public Stream Output { get; }
        public Stream Error { get; }

        /// <summary>Reports the exit code to the outside copy, which exits with it.</summary>
        public void Exit(int code)
        {
            try
            {
                _frames.WriteAsync(RelayFrames.Exit, BitConverter.GetBytes(code)).AsTask().GetAwaiter().GetResult();
            }
            catch (IOException)
            {
                // The outside copy has gone.
            }
        }

        public void Dispose() => _pipe.Dispose();
    }

    /// <summary>
    /// This program as MO2 must start it: <c>dotnet</c> with its assembly. The launcher <c>dotnet tool install</c>
    /// writes is built for CET shadow stacks, which usvfs's injection breaks; <c>dotnet</c> itself is not.
    /// </summary>
    private static (string Program, string[] Prefix) Self()
    {
        var assembly = System.Reflection.Assembly.GetEntryAssembly()?.Location;
        var dotnet = Path.GetFullPath(Path.Combine(System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory(), "..", "..", "..", "dotnet.exe"));
        if (string.IsNullOrEmpty(assembly) || !File.Exists(dotnet))
            throw new SafePatchException("Cannot find this program's assembly and the dotnet host, to start it through MO2.");
        return (dotnet, [assembly]);
    }

    /// <summary>
    /// ModOrganizer.exe for an instance: a portable instance's own, else the MO2 that is running. (The nxm link handler
    /// in the registry is not used: it names whichever MO2 registered last, which may be another, older install.)
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static string? FindModOrganizer(string instance)
    {
        var portable = Path.Combine(instance, "ModOrganizer.exe");
        if (File.Exists(portable)) return portable;
        var running = Process.GetProcessesByName("ModOrganizer")
            .Select(p =>
            {
                try
                {
                    return p.MainModule?.FileName;
                }
                catch (System.ComponentModel.Win32Exception)
                {
                    return null;
                }
            })
            .OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return running.Count == 1 ? running[0] : null;
    }

    /// <summary>Arguments joined as a Windows command line, quoted so the program's parser reads them back unchanged.</summary>
    internal static string CommandLine(IEnumerable<string> arguments) => string.Join(' ', arguments.Select(Quote));

    private static string Quote(string argument)
    {
        if (argument.Length > 0 && !argument.Any(c => c is ' ' or '\t' or '"')) return argument;
        var quoted = new StringBuilder("\"");
        var backslashes = 0;
        foreach (var c in argument)
        {
            if (c == '\\')
            {
                backslashes++;
                continue;
            }
            quoted.Append('\\', c == '"' ? backslashes * 2 + 1 : backslashes).Append(c);
            backslashes = 0;
        }
        return quoted.Append('\\', backslashes * 2).Append('"').ToString();
    }

    private sealed class InputStream(RelayFrames frames) : Stream
    {
        private ReadOnlyMemory<byte> _pending;
        private bool _ended;

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            while (_pending.IsEmpty && !_ended)
            {
                // A broken pipe means the outside copy has gone: to this program, its input has ended.
                var frame = frames.Read();
                if (frame is null || frame.Value.Kind == RelayFrames.InputEnd) _ended = true;
                else if (frame.Value.Kind == RelayFrames.Input) _pending = frame.Value.Payload;
                else throw new IOException($"Unexpected relay frame ({frame.Value.Kind}).");
            }
            var count = Math.Min(buffer.Length, _pending.Length);
            _pending.Span[..count].CopyTo(buffer);
            _pending = _pending[count..];
            return count;
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            _pending.IsEmpty && !_ended ? new(Task.Run(() => Read(buffer.Span), cancellationToken)) : new(Read(buffer.Span));

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class OutputStream(RelayFrames frames, byte kind) : Stream
    {
        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

        public override void Write(ReadOnlySpan<byte> buffer) =>
            WriteAsync(buffer.ToArray()).AsTask().GetAwaiter().GetResult();

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            for (var offset = 0; offset < buffer.Length; offset += RelayFrames.MaxPayload)
                await frames.WriteAsync(kind, buffer.Slice(offset, Math.Min(RelayFrames.MaxPayload, buffer.Length - offset)), cancellationToken);
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}

/// <summary>
/// The relay's frames: a kind byte, a little-endian length, then the payload. Writes from several threads are
/// serialised; one thread reads.
/// </summary>
internal sealed class RelayFrames(Stream pipe)
{
    public const byte Input = 0, InputEnd = 1, Output = 2, Error = 3, Exit = 4;
    public const int MaxPayload = 64 * 1024;

    private readonly SemaphoreSlim _writing = new(1, 1);
    private readonly byte[] _header = new byte[5];

    public async ValueTask WriteAsync(byte kind, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default)
    {
        var frame = new byte[5 + payload.Length];
        frame[0] = kind;
        BitConverter.TryWriteBytes(frame.AsSpan(1, 4), payload.Length);
        payload.CopyTo(frame.AsMemory(5));
        await _writing.WaitAsync(cancellationToken);
        try
        {
            await pipe.WriteAsync(frame, cancellationToken);
            await pipe.FlushAsync(cancellationToken);
        }
        finally
        {
            _writing.Release();
        }
    }

    /// <summary>The next frame, or null once the other end has closed the pipe.</summary>
    public (byte Kind, ReadOnlyMemory<byte> Payload)? Read()
    {
        try
        {
            if (!Fill(_header)) return null;
            var length = BitConverter.ToInt32(_header, 1);
            if (length is < 0 or > MaxPayload) throw new IOException($"Relay frame of {length} bytes.");
            var payload = new byte[length];
            if (!Fill(payload)) return null;
            return (_header[0], payload);
        }
        catch (IOException) when (!pipe.CanRead)
        {
            return null;
        }
    }

    private bool Fill(byte[] buffer)
    {
        try
        {
            pipe.ReadExactly(buffer);
            return true;
        }
        catch (EndOfStreamException)
        {
            return false;
        }
    }
}
