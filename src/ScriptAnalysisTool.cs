using System.Diagnostics;
using System.IO;

namespace XEnchantmentPatcher;

// One effective lookup shared by asset analysis and its report diagnostic.
internal sealed class ScriptAnalysisTool
{
    public string? ExecutablePath { get; }
    public string? Warning => ExecutablePath is null
        ? "Script analysis is unavailable because Champollion.exe could not be found. "
            + "Restore the bundled Champollion folder beside XEnchantmentPatcher.dll, or set XEP_CHAMPOLLION_PATH to the executable. "
            + "You can continue reviewing equipment; source contexts with unresolved script evidence remain protected. "
            + "See item review details and script diagnostics for specific causes."
        : null;

    public ScriptAnalysisTool() : this(Environment.GetEnvironmentVariable("XEP_CHAMPOLLION_PATH"), AppContext.BaseDirectory) { }

    internal ScriptAnalysisTool(string? configured, string baseDirectory)
    {
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured))
        { ExecutablePath = configured; return; }
        var bundled = Path.Combine(baseDirectory, "Champollion", "Champollion.exe");
        if (File.Exists(bundled)) ExecutablePath = bundled;
    }

    // The decompiler command line: every PEX of a folder into an output folder. The
    // output folder is passed in extended-length form so deep folders work; Champollion
    // rejects that form for its input, which stays as given. Champollion 1.3.2 reads a
    // folder only in its threaded mode (-t); a script it rejects gets no PSC file.
    internal static ProcessStartInfo DecompileStart(string executable, string pexFolder, string outputFolder)
    {
        var start = new ProcessStartInfo(executable)
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(pexFolder); start.ArgumentList.Add("-p"); start.ArgumentList.Add(ExtendedLengthPaths.Of(outputFolder)); start.ArgumentList.Add("-t");
        return start;
    }
}
