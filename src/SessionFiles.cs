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

// No code deletes a lock file: that could create two independent locks. The OS
// deletes it when the holder's handle closes, also on termination. File-sharing
// exclusion also covers junction/path aliases.
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
                var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read, 4096, FileOptions.DeleteOnClose);
                try
                {
                    var text = JsonSerializer.Serialize(new { Pid = Environment.ProcessId, SessionId = SessionRuntime.Id,
                        Resource = resource, AcquiredUtc = DateTimeOffset.UtcNow });
                    var bytes = Encoding.UTF8.GetBytes(text); stream.SetLength(0); stream.Write(bytes); stream.Flush(true);
                    return new(stream);
                }
                catch { stream.Dispose(); throw; }
            }
            catch (Exception ex) when (IsHeld(ex) && watch.Elapsed < (wait ?? TimeSpan.Zero)) { Thread.Sleep(30); }
            catch (Exception ex) when (IsHeld(ex))
            {
                string owner; try { owner = AtomicFile.ReadText(path); } catch { owner = "owner information unavailable"; }
                throw new LeaseContentionException(resource, path, owner, wait ?? TimeSpan.Zero, ex);
            }
        }
    }
    // Access denied: released while another session read the owner (delete pending).
    private static bool IsHeld(Exception error) => error is UnauthorizedAccessException || error is IOException && (error.HResult & 0xFFFF) is 32 or 33;
    public void Dispose() => stream.Dispose();
}

internal sealed class LeaseContentionException(string resource, string path, string owner, TimeSpan waited, Exception inner)
    : IOException($"Another XEP session is using {resource}. Wait for that session to finish, then retry. Wait limit: {waited.TotalSeconds:g} seconds. Lock: {path}; {owner}", inner);
