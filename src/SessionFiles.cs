using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace XEnchantmentPatcher;

internal static class AtomicFile
{
    internal static TimeSpan RetryWindow { get; set; } = TimeSpan.FromSeconds(3);

    public static void WriteText(string path, string text)
    {
        path = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                var bytes = Encoding.UTF8.GetBytes(text);
                stream.Write(bytes); stream.Flush(flushToDisk: true);
            }
            var replacing = Stopwatch.StartNew();
            while (true)
            {
                try
                {
                    // Same-volume rename/replace. Avoid ReplaceFile: an injected
                    // MO2 process was observed stuck inside that native API.
                    File.Move(temporary, path, overwrite: true);
                    break;
                }
                // Windows readers/antivirus may briefly hold the target without
                // delete sharing. Never fall back to truncating the live file.
                catch (IOException ex) when ((ex.HResult & 0xFFFF) is 5 or 32 or 33 && replacing.Elapsed < RetryWindow) { Thread.Sleep(10); }
                catch (UnauthorizedAccessException) when (replacing.Elapsed < RetryWindow) { Thread.Sleep(10); }
            }
        }
        finally
        {
            // Cleanup must not replace the original save error (e.g. access
            // denied) with a second error deleting a temporary file.
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    public static string ReadText(string path)
    {
        var opening = Stopwatch.StartNew();
        while (true)
        {
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream); return reader.ReadToEnd();
            }
            // A replace racing CreateFile can briefly report the old name as
            // missing/delete-pending. Retry opening; never consume a temp file.
            catch (IOException ex) when ((ex.HResult & 0xFFFF) is 2 or 5 or 32 or 33 && opening.Elapsed < RetryWindow) { Thread.Sleep(10); }
            catch (UnauthorizedAccessException) when (opening.Elapsed < RetryWindow) { Thread.Sleep(10); }
        }
    }
    internal static bool Exists(string path)
    {
        try
        {
            if ((File.GetAttributes(path) & FileAttributes.Directory) != 0)
                throw new IOException("Expected a file, but the path is a directory: " + path);
            return true;
        }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
        // File.Exists hides access errors as false. A denied settings file must
        // fail loading/saving, never be mistaken for an absent settings file.
    }
    public static string? Version(string path) => Exists(path)
        ? Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(ReadText(path)))) : null;
}

// The stable sidecar is never deleted: deleting a lock file could create two
// independent locks. File-sharing exclusion also covers junction/path aliases.
// The OS releases the handle on termination; stale diagnostic text is not a lock.
internal sealed class FileLease : IDisposable
{
    private readonly FileStream stream;
    private FileLease(FileStream stream) => this.stream = stream;
    public static FileLease Acquire(string path, string resource, TimeSpan? wait = null)
    {
        path = Path.GetFullPath(path); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var watch = Stopwatch.StartNew();
        while (true)
        {
            try
            {
                var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);
                try
                {
                    var text = JsonSerializer.Serialize(new { Pid = Environment.ProcessId, SessionId = SessionRuntime.Id,
                        Resource = resource, AcquiredUtc = DateTimeOffset.UtcNow });
                    var bytes = Encoding.UTF8.GetBytes(text); stream.SetLength(0); stream.Write(bytes); stream.Flush(true);
                    return new(stream);
                }
                catch { stream.Dispose(); throw; }
            }
            catch (IOException ex) when (IsSharingConflict(ex) && watch.Elapsed < (wait ?? TimeSpan.Zero)) { Thread.Sleep(30); }
            catch (IOException ex) when (IsSharingConflict(ex))
            {
                string owner; try { owner = AtomicFile.ReadText(path); } catch { owner = "owner information unavailable"; }
                throw new LeaseContentionException(resource, path, owner, wait ?? TimeSpan.Zero, ex);
            }
        }
    }
    private static bool IsSharingConflict(IOException error) => (error.HResult & 0xFFFF) is 32 or 33;
    public void Dispose() => stream.Dispose();
}

internal sealed class LeaseContentionException(string resource, string path, string owner, TimeSpan waited, Exception inner)
    : IOException($"Another XEP session is using {resource}. Wait for that session to finish, then retry. Wait limit: {waited.TotalSeconds:g} seconds. Lock: {path}; {owner}", inner);

internal static class CompletedScriptCache
{
    // New cache namespace: older builds may write directly into their legacy
    // directories. No new reader consumes one of those possibly partial files.
    public static string? Get(string directory, string key, Func<string?> create)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, key + ".psc");
        if (File.Exists(path)) return AtomicFile.ReadText(path);
        using var lease = AcquireProducer(directory, key, TimeSpan.FromSeconds(35));
        if (File.Exists(path)) return AtomicFile.ReadText(path);
        var completed = create();
        if (!string.IsNullOrWhiteSpace(completed)) AtomicFile.WriteText(path, completed);
        return completed;
    }

    internal static FileLease AcquireProducer(string directory, string key, TimeSpan wait)
    {
        try { return FileLease.Acquire(Path.Combine(directory, key + ".lock"), "script cache " + key, wait); }
        catch (LeaseContentionException ex)
        {
            throw new IOException($"Timed out waiting for another XEP script-cache producer ({key}) after {wait.TotalSeconds:g} seconds. "
                + "Wait for the reported producer session/PID to finish, then retry the scan. Changing the output folder does not resolve shared script-cache contention. "
                + "Do not delete a live cache lock. " + ex.Message, ex);
        }
    }
}
