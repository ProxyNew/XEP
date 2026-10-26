using System.Diagnostics;
using System.IO;

namespace XEnchantmentPatcher;

// One effective lookup shared by asset analysis and its report diagnostic.
internal sealed class ScriptAnalysisTool
{
    public string? ExecutablePath { get; }
    public string? Warning => ExecutablePath is null
        ? "Script analysis is unavailable because Champollion.exe could not be found. "
            + "Restore the bundled Champollion folder beside the patcher executable, or set XEP_CHAMPOLLION_PATH to the executable. "
            + "You can continue reviewing equipment; source contexts with unresolved script evidence remain protected. "
            + "See item review details and script diagnostics for specific causes."
        : null;

    public ScriptAnalysisTool() : this(Environment.GetEnvironmentVariable("XEP_CHAMPOLLION_PATH"), AppContext.BaseDirectory) { }

    internal ScriptAnalysisTool(string? configured, string baseDirectory, Func<string, bool>? fileExists = null)
    {
        fileExists ??= File.Exists;
        if (!string.IsNullOrWhiteSpace(configured) && fileExists(configured))
        { ExecutablePath = configured; return; }
        var bundled = Path.Combine(baseDirectory, "Champollion", "Champollion.exe");
        if (fileExists(bundled)) { ExecutablePath = bundled; return; }
        for (var dir = new DirectoryInfo(baseDirectory); dir is not null; dir = dir.Parent)
            foreach (var path in new[] { Path.Combine(dir.FullName, "Champollion.exe"), Path.Combine(dir.FullName, "Champollion", "Champollion.exe") })
                if (fileExists(path)) { ExecutablePath = path; return; }
    }

    // The decompiler command line: one PEX into an output folder. The folder is passed
    // in extended-length form so deep folders work; Champollion rejects that form for
    // its input file, which stays as given.
    internal static ProcessStartInfo DecompileStart(string executable, string pex, string outputFolder)
    {
        var start = new ProcessStartInfo(executable)
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(pex); start.ArgumentList.Add("-p"); start.ArgumentList.Add(ExtendedLengthPaths.Of(outputFolder));
        return start;
    }
}
