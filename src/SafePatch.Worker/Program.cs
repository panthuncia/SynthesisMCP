using System.IO.Pipes;
using SafePatch.Worker.Core;

// Usage: SafePatch.Worker --nonce <hex> --in <pipe handle> --out <pipe handle>
// The host launches this inside the sandbox; the pipe handles are the only ones it inherits, and it
// duplicates read-only file handles in later. Console output is captured and returned to the host.
if (args is not ["--nonce", var nonce, "--in", var inHandle, "--out", var outHandle]) return 2;

var log = new StringWriter();
Console.SetOut(log);
Console.SetError(log);

using var fromHost = new AnonymousPipeClientStream(PipeDirection.In, inHandle);
using var toHost = new AnonymousPipeClientStream(PipeDirection.Out, outHandle);
return WorkerRuntime.Run(fromHost, toHost, nonce, BrokeredFileSystem.FromHandle, log);
