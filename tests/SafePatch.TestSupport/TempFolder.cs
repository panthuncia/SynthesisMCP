namespace SafePatch.TestSupport;

/// <summary>A temporary folder that is deleted on dispose, such as one holding a previous patcher's output.</summary>
public sealed class TempFolder : IDisposable
{
    public TempFolder(string prefix = "SafePatchTemp-") => Path = Directory.CreateTempSubdirectory(prefix).FullName;

    public string Path { get; }

    public string File(string name) => System.IO.Path.Combine(Path, name);

    public void Dispose() => Delete(Path);

    /// <summary>
    /// Deletes a test folder. A worker or server process that just ended can hold its files for a moment
    /// longer, so this retries briefly before giving up.
    /// </summary>
    public static void Delete(string path)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
                return;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException && attempt < 50)
            {
                Thread.Sleep(100);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return;
            }
        }
    }
}
