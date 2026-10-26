using System.IO;

namespace XEnchantmentPatcher;

// Direct Win32 calls and native tools stop at MAX_PATH unless a path is given in
// extended-length form; .NET's own file APIs already handle long paths.
internal static class ExtendedLengthPaths
{
    public static string Of(string path)
    {
        if (path.StartsWith(@"\\?\", StringComparison.Ordinal) || path.StartsWith(@"\\.\", StringComparison.Ordinal)) return path;
        var full = Path.GetFullPath(path);
        return full.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + full[2..] : @"\\?\" + full;
    }
}
