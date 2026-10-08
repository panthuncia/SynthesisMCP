using Microsoft.Win32.SafeHandles;

namespace SafePatch.Host;

/// <summary>Starts a worker process connected by a pair of streams.</summary>
public interface IWorkerLauncher
{
    IWorkerProcess Launch(string nonce);
}

public interface IWorkerProcess : IDisposable
{
    /// <summary>Messages from the worker.</summary>
    Stream FromWorker { get; }

    /// <summary>Messages to the worker.</summary>
    Stream ToWorker { get; }

    /// <summary>
    /// Makes an open file readable by the worker and returns the handle value to send it.
    /// The worker gets read access only, whatever access <paramref name="file"/> has.
    /// </summary>
    long ShareReadOnly(SafeFileHandle file);

    /// <summary>Terminates the worker. Must unblock any pending read on <see cref="FromWorker"/>.</summary>
    void Kill();

    /// <summary>How the worker ended (e.g. its exit code), for error messages; null if unknown.</summary>
    string? DescribeExit();
}

/// <summary>
/// Validates the plugin the worker produced against the policy and, only if all of it passes,
/// applies it to the real patch. Throws <see cref="PatchRejectedException"/> on rejection.
/// </summary>
public interface IPatchCommitter
{
    /// <param name="outputPlugins">The worker's plugin, then any split parts (<c>_2</c>, <c>_3</c>...). Untrusted.</param>
    /// <param name="persistence">The worker's updated FormKey allocation file, if it returned one. Untrusted.</param>
    IReadOnlyList<RecordChange> Commit(IReadOnlyList<byte[]> outputPlugins, byte[]? persistence, PatchPolicy policy);
}

/// <summary>One record the patch adds, changes or removes, for review.</summary>
/// <param name="IsRemoved">The record was removed from the patch mod: an earlier patcher's override is reverted,
/// or a record it added is deleted.</param>
public sealed record RecordChange(string FormKey, string RecordType, string? EditorId, bool IsNew, IReadOnlyList<string> Fields, bool IsRemoved = false);

/// <summary>A file the worker may read, at the path the program will ask for.</summary>
/// <param name="Inline">Copy the contents (small text files) rather than sharing a handle.</param>
/// <param name="SharedAs">The path the worker sees, when the file lives elsewhere: a plugin in an MO2 mod folder is
/// shared as if it were in the game's Data folder. By default, <paramref name="Path"/>.</param>
public sealed record InputFile(string Path, bool Inline = false, string? SharedAs = null)
{
    public string WorkerPath => SharedAs ?? Path;
}

/// <summary>What the worker runs: the Synthesis arguments and the files they refer to.</summary>
/// <param name="SettingsFile">Which of <paramref name="Files"/> holds the user's settings, if the program has settings and the user saved some.</param>
/// <param name="DataFolder">The Data folder loose assets are served from, if any.</param>
/// <param name="GameIniPath">Which of <paramref name="Files"/> is the game INI listing archives, if it was found.</param>
public sealed record RunInputs(
    IReadOnlyList<string> Arguments, IReadOnlyList<InputFile> Files, string? SettingsFile = null, string? DataFolder = null, string? GameIniPath = null, bool ObserveReads = false);
