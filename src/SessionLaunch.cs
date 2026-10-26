using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace XEnchantmentPatcher;

internal static class SessionRuntime
{
    public static string Id { get; private set; } = Guid.NewGuid().ToString("N");
    public static string? DirectoryPath { get; private set; }
    private static FileLease? lease;
    private static FileStream? buildLease;
    private static StreamWriter? sessionLog;
    public static string? LogError { get; private set; }
    internal static int? ExitCode { get; set; }
    private static readonly object gate = new();
    private static readonly Dictionary<string, object?> metadata = [];

    public static void Start(string directory, string? identity, bool gui)
    {
        if (identity is not null && (identity.Length == 0 || identity.Length > 80 || identity.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '-' and not '_')))
            throw new ArgumentException("Run ID must contain only letters, digits, '-' or '_', up to 80 characters.");
        Id = identity ?? Id;
        DirectoryPath = Path.GetFullPath(directory); Directory.CreateDirectory(DirectoryPath);
        lease = FileLease.Acquire(Path.Combine(DirectoryPath, ".session.lock"), "session directory " + DirectoryPath);
        if (File.Exists(Path.Combine(DirectoryPath, "run.json")))
        {
            var existing = DirectoryPath;
            Dispose(); DirectoryPath = null;
            throw new IOException("This session directory already contains a run. Choose a new directory: " + existing);
        }
        sessionLog = new StreamWriter(new FileStream(Path.Combine(DirectoryPath, "session.log"), FileMode.CreateNew, FileAccess.Write, FileShare.Read)) { AutoFlush = true };
        using var process = Process.GetCurrentProcess();
        metadata["RunId"] = Id; metadata["Pid"] = Environment.ProcessId;
        metadata["ProcessStartedUtc"] = process.StartTime.ToUniversalTime().ToString("O");
        metadata["StartedUtc"] = DateTimeOffset.UtcNow; metadata["Mode"] = gui ? "gui" : "cli";
        metadata["Executable"] = Environment.ProcessPath;
        metadata["Build"] = typeof(SessionRuntime).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (Environment.ProcessPath is { } exe)
        {
            using var binary = File.OpenRead(exe);
            metadata["ExecutableSha256"] = Convert.ToHexString(SHA256.HashData(binary));
        }
        metadata["Runtime"] = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription;
        metadata["SessionProtocol"] = 2; metadata["OutputLockProtocol"] = GenerationLease.Protocol;
        metadata["Arguments"] = Environment.GetCommandLineArgs().Skip(1).ToArray();
        metadata["State"] = "starting"; Log($"Starting {Id}, PID {Environment.ProcessId}, build {metadata["Build"]}, mode {metadata["Mode"]}."); Write();
    }
    public static void Settings(string file, string? output)
    {
        if (DirectoryPath is null || lease is null) return;
        lock (gate) { metadata["SettingsFile"] = file; metadata["SettingsSha256"] = AtomicFile.Version(file); metadata["OutputFolder"] = output; Log($"Settings: {file}; output: {output}"); Write(); }
    }
    public static void LoadOrder(string data, IEnumerable<string> plugins)
    {
        if (DirectoryPath is null || lease is null) return;
        lock (gate)
        {
            var ordered = plugins.ToArray(); metadata["DataFolder"] = data; metadata["Plugins"] = ordered;
            metadata["LoadOrderReadCount"] = metadata.TryGetValue("LoadOrderReadCount", out var count) ? (int)count! + 1 : 1;
            metadata["LoadOrderSha256"] = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', ordered))));
            metadata["LoadOrderObservedUtc"] = DateTimeOffset.UtcNow; metadata["State"] = "running"; Write();
            Log($"Observed {ordered.Length} plugins; load-order SHA256 {metadata["LoadOrderSha256"]}.");
        }
    }
    public static void EffectiveSettings(AppSettings settings)
    {
        if (DirectoryPath is null || lease is null) return;
        lock (gate)
        {
            var path = Path.Combine(DirectoryPath, "effective-settings.json");
            AtomicFile.WriteText(path, JsonSerializer.Serialize(settings, JsonContext.Default.AppSettings));
            metadata["EffectiveSettingsSha256"] = AtomicFile.Version(path);
            metadata["OutputFolder"] = settings.OutputFolder; Write();
        }
    }
    public static void Finish(int code)
    {
        ExitCode = code;
        if (DirectoryPath is null || lease is null) return;
        lock (gate) { metadata["ExitCode"] = code; metadata["State"] = code == 0 ? "completed" : "failed"; metadata["FinishedUtc"] = DateTimeOffset.UtcNow; Log($"EXIT {code}"); Write(); }
    }
    public static void HoldBuild()
    {
        var path = Path.Combine(AppContext.BaseDirectory, ".xep-publish.lock");
        if (File.Exists(path)) buildLease = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
    }
    public static void Log(string message)
    {
        lock (gate)
        {
            try { if (sessionLog is not null) { sessionLog.WriteLine($"{DateTimeOffset.UtcNow:O} {message}"); LogError = null; } }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { LogError = "The session log could not be written. Settings and generation status are reported separately. " + ex.Message; }
        }
    }
    public static void Dispose()
    {
        try { sessionLog?.Dispose(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { LogError = "Could not flush the session log on exit: " + ex.Message; }
        finally { sessionLog = null; lease?.Dispose(); lease = null; buildLease?.Dispose(); buildLease = null; }
    }
    public static void StartupFailure(Exception error)
    {
        ExitCode = 1;
        if (DirectoryPath is null || lease is null) return;
        try { AtomicFile.WriteText(Path.Combine(DirectoryPath, "startup-error.txt"), error.ToString()); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Console.Error.WriteLine("Could not record startup failure: " + ex.Message); }
        try { Finish(1); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Console.Error.WriteLine("Could not record failed completion: " + ex.Message); }
    }
    private static void Write() => AtomicFile.WriteText(Path.Combine(DirectoryPath!, "run.json"), JsonSerializer.Serialize(metadata, new JsonSerializerOptions { WriteIndented = true }));
}

internal sealed record SessionLaunch(bool Gui, string[] Arguments)
{
    internal static string AutomaticSessionFolder { get; set; } = Path.Combine(SettingsStore.DefaultFolder, "Sessions");
    public static SessionLaunch Prepare(string[] args)
    {
        var gui = args.Length == 0 || args.Contains("--gui");
        string? settings = null, snapshot = null, directory = null, id = null, output = null, authorRules = null;
        var remaining = new List<string>();
        for (var i = 0; i < args.Length; i++)
        {
            string Value() => ++i < args.Length ? args[i] : throw new ArgumentException($"Missing value for {args[i - 1]}.");
            switch (args[i])
            {
                case "--gui": break;
                case "--settings-file": settings = Path.GetFullPath(Value()); break;
                case "--settings-snapshot": snapshot = Path.GetFullPath(Value()); break;
                case "--session-dir": directory = Path.GetFullPath(Value()); break;
                case "--run-id": id = Value(); break;
                case "--output-folder" when gui: output = Path.GetFullPath(Value()); break;
                case "--author-rules-input" when gui: authorRules = Path.GetFullPath(Value()); break;
                default: remaining.Add(args[i]); break;
            }
        }
        if (gui && remaining.Count != 0) throw new ArgumentException("GUI session options: --settings-file or --settings-snapshot, --session-dir, --run-id, --output-folder, --author-rules-input.");
        if (authorRules is not null && (directory is null || settings is null && snapshot is null))
            throw new ArgumentException("--author-rules-input requires an isolated GUI session with --session-dir and private settings.");
        if (settings is not null && snapshot is not null) throw new ArgumentException("Choose --settings-file OR --settings-snapshot.");
        if (id is not null && directory is null) throw new ArgumentException("--run-id requires --session-dir.");
        if (settings is not null || snapshot is not null || directory is not null || output is not null)
        {
            directory ??= Path.Combine(AutomaticSessionFolder, SessionRuntime.Id);
            SessionRuntime.Start(directory, id, gui);
            // An output-only GUI launch is an isolated copy of the ordinary
            // settings. Never fall through to (or save over) the user's output.
            if (output is not null && settings is null && snapshot is null)
            {
                var clone = SettingsStore.Load();
                settings = Path.Combine(directory, "settings.json");
                if (File.Exists(settings)) throw new IOException("Private session settings already exist: " + settings);
                clone.OutputFolder = output;
                AtomicFile.WriteText(settings, JsonSerializer.Serialize(clone, JsonContext.Default.AppSettings));
            }
            if (snapshot is not null)
            {
                settings = Path.Combine(directory, "settings.json");
                if (File.Exists(settings)) throw new IOException("Snapshot session settings already exist: " + settings);
                var clone = SettingsStore.ReadSnapshot(snapshot);
                clone.OutputFolder = output ?? Path.Combine(directory, "output");
                AtomicFile.WriteText(settings, JsonSerializer.Serialize(clone, JsonContext.Default.AppSettings));
            }
            if (settings is not null) SettingsStore.UseFile(settings);
            SettingsStore.InitialOutputFolder = output;
            SessionRuntime.Settings(SettingsStore.PathName, SettingsStore.Load().OutputFolder);
            if (authorRules is not null)
            {
                // Private validation only: settings and output must both be inside the session folder.
                var root = Path.GetFullPath(directory) + Path.DirectorySeparatorChar;
                var privateOutput = SettingsStore.Load().OutputFolder;
                if (!Path.GetFullPath(SettingsStore.PathName).StartsWith(root, StringComparison.OrdinalIgnoreCase)
                    || privateOutput is null || !Path.GetFullPath(privateOutput).StartsWith(root, StringComparison.OrdinalIgnoreCase))
                    throw new ArgumentException("--author-rules-input requires private settings and output inside the session folder.");
                if (!File.Exists(authorRules)) throw new FileNotFoundException("Author rules input does not exist: " + authorRules);
                AuthorRules.PrivateInput = authorRules;
                SessionRuntime.Log("Author rules input override (private validation): " + authorRules);
            }
            if (!gui && !remaining.Contains("--log")) remaining.AddRange(["--log", Path.Combine(directory, "run.log")]);
        }
        return new(gui, remaining.ToArray());
    }
}
