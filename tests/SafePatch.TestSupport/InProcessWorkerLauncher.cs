using System.IO.Pipes;
using Microsoft.Win32.SafeHandles;
using SafePatch.Host;
using SafePatch.Worker.Core;

namespace SafePatch.TestSupport;

/// <summary>
/// Runs a worker entry point on a background thread, connected by real anonymous pipes. Shared
/// file handles are passed as-is, since the "worker" is in this process.
/// NO SANDBOX: for testing the host/worker session only.
/// </summary>
/// <param name="workerMain">Worker entry point: (fromHost, toHost, nonce) → exit code.</param>
public sealed class InProcessWorkerLauncher(Func<Stream, Stream, string, int> workerMain) : IWorkerLauncher
{
    /// <summary>The real worker runtime: the real Synthesis pipeline over the brokered file system.</summary>
    public static InProcessWorkerLauncher Real() => new((i, o, n) => WorkerRuntime.Run(i, o, n, BrokeredFileSystem.FromHandle));

    public IWorkerProcess Launch(string nonce)
    {
        var toWorker = new AnonymousPipeServerStream(PipeDirection.Out);
        var workerIn = new AnonymousPipeClientStream(PipeDirection.In, toWorker.ClientSafePipeHandle);
        var fromWorker = new AnonymousPipeServerStream(PipeDirection.In);
        var workerOut = new AnonymousPipeClientStream(PipeDirection.Out, fromWorker.ClientSafePipeHandle);

        var worker = Task.Factory.StartNew(() =>
        {
            using (workerIn)
            using (workerOut)
            {
                return workerMain(workerIn, workerOut, nonce);
            }
        }, TaskCreationOptions.LongRunning);

        return new Worker(worker, fromWorker, toWorker, workerOut);
    }

    private sealed class Worker(Task<int> worker, Stream fromWorker, Stream toWorker, Stream workerOut) : IWorkerProcess
    {
        public Stream FromWorker => fromWorker;
        public Stream ToWorker => toWorker;

        // Same process: the handle value is already valid for the "worker".
        public long ShareReadOnly(SafeFileHandle file) => file.DangerousGetHandle().ToInt64();

        // Closing the worker's write end makes the host's pending read see end-of-stream,
        // as a killed process would. A runaway worker thread is left behind.
        public void Kill() => workerOut.Dispose();

        public string? DescribeExit() => worker.Status switch
        {
            TaskStatus.RanToCompletion => $"exit code {worker.Result}",
            TaskStatus.Faulted => $"worker threw {worker.Exception!.InnerException!.GetType().Name}",
            _ => null,
        };

        public void Dispose()
        {
            toWorker.Dispose();
            fromWorker.Dispose();
        }
    }
}
