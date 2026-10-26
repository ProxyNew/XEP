using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace XEnchantmentPatcher;

// Protocol 2: writers in the same Windows account lock the physical directory,
// not its spelling. Directory/reparse handles without delete sharing prevent
// renaming or retargeting the path while generation writes through it.
internal sealed class GenerationLease : IDisposable
{
    internal const string Protocol = "windows-file-id-v2";
    internal static string LockFolder { get; set; } = Path.Combine(SettingsStore.DefaultFolder, "locks", "generation-v2");
    private readonly List<SafeFileHandle> directories = [];
    // Windows can opt individual directories into case-sensitive names. Only
    // physical file IDs decide equivalence; spelling is used for traversal only.
    private readonly HashSet<string> pinnedPaths = new(StringComparer.Ordinal);
    private FileLease? lease;
    public static IDisposable Acquire(string output)
    {
        var folder = Path.GetDirectoryName(Path.GetFullPath(output))!;
        Directory.CreateDirectory(folder);
        var result = new GenerationLease();
        try
        {
            result.PinChain(folder);
            var physical = OpenDirectory(folder, reparsePoint: false);
            result.directories.Add(physical);
            if (!GetFileInformationByHandleEx(physical, 18, out var id, (uint)Marshal.SizeOf<FileIdInfo>()))
                throw NativeError("Cannot establish the physical identity of output folder " + folder);
            if (id.Low == 0 && id.High == 0) throw new IOException("The filesystem did not supply a usable directory file ID: " + folder);
            var resolved = new StringBuilder(32768);
            var length = GetFinalPathNameByHandleW(physical, resolved, (uint)resolved.Capacity, 0);
            if (length == 0 || length >= resolved.Capacity) throw NativeError("Cannot resolve output folder " + folder);
            result.PinChain(resolved.ToString());
            var key = $"{id.VolumeSerialNumber:X16}-{id.Low:X16}-{id.High:X16}";
            Directory.CreateDirectory(LockFolder);
            result.PinChain(LockFolder);
            result.lease = FileLease.Acquire(Path.Combine(LockFolder, key + ".lock"), "output folder " + folder);
            return result;
        }
        catch (LeaseContentionException) { result.Dispose(); throw; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception)
        {
            result.Dispose();
            throw new IOException($"Cannot safely lock output folder {folder}. No generation was started. "
                + "This destination must support Windows directory file IDs and stable directory handles. Use a supported local destination; do not bypass locking. " + ex.Message, ex);
        }
        catch { result.Dispose(); throw; }
    }
    private void PinChain(string path)
    {
        var chain = new Stack<string>();
        for (string? current = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)); current is not null;
             current = Directory.GetParent(current)?.FullName) chain.Push(current);
        // Pin ancestors before descendants. Pin both the supplied chain and the
        // resolved target chain; a junction name is a separate object to pin.
        foreach (var directory in chain)
        {
            if (!pinnedPaths.Add(directory)) continue;
            directories.Add(OpenDirectory(directory, reparsePoint: true));
            var info = new DirectoryInfo(directory);
            if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                // Also pin intermediate links inside a link's target path.
                // Pinning only the final target misses junction -> junction.
                var target = info.ResolveLinkTarget(returnFinalTarget: false)
                    ?? throw new IOException("Cannot resolve directory reparse target: " + directory);
                PinChain(target.FullName);
            }
        }
    }
    private static SafeFileHandle OpenDirectory(string path, bool reparsePoint)
    {
        var handle = CreateFileW(ExtendedLengthPaths.Of(path), 0x80000000 /* GENERIC_READ: participate in delete-sharing checks */, 3 /* share read/write, never delete */, IntPtr.Zero, 3 /* OPEN_EXISTING */,
            0x02000000u /* BACKUP_SEMANTICS */ | (reparsePoint ? 0x00200000u : 0), IntPtr.Zero);
        if (!handle.IsInvalid) return handle;
        var error = NativeError("Cannot pin directory " + path); handle.Dispose(); throw error;
    }
    private static Win32Exception NativeError(string message) => new(Marshal.GetLastWin32Error(), message);
    public void Dispose()
    {
        lease?.Dispose(); lease = null;
        for (var i = directories.Count - 1; i >= 0; i--) directories[i].Dispose();
        directories.Clear();
    }
    [StructLayout(LayoutKind.Sequential)] private struct FileIdInfo { public ulong VolumeSerialNumber, Low, High; }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle handle, int infoClass, out FileIdInfo info, uint size);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(SafeFileHandle handle, StringBuilder path, uint size, uint flags);
}
