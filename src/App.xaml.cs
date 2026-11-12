using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace XEnchantmentPatcher;

public partial class App : System.Windows.Application
{
    // Set once an unhandled exception has ended the session: closing windows then neither
    // asks the user anything nor records a completion over the failure.
    internal static bool EndingAfterFailure { get; private set; }

    protected override async void OnStartup(StartupEventArgs e)
    {
        try
        {
            ProcessPerformance.PreferPerformance();
            // Script proofs alternate between fixed grammar and symbol-specific
            // expressions. Retain a bounded working set without changing matching.
            System.Text.RegularExpressions.Regex.CacheSize = 512;
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            SessionRuntime.HoldBuild();
            var launch = SessionLaunch.Prepare(e.Args);
            if (!launch.Gui)
            {
                var code = Cli.Run(launch.Arguments);
                SessionRuntime.Finish(code); Shutdown(code); return;
            }
            // Older builds kept decompiled scripts here. GUI only: the test suites start the command line.
            try { Directory.Delete(Path.Combine(SettingsStore.DefaultFolder, "ScriptAnalysis"), true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            DispatcherUnhandledException += ReportUnhandledException;
            base.OnStartup(e);
            // Explicit construction keeps WPF from creating a hidden GUI after CLI
            // Shutdown. StartupUri cannot be set to null on this WPF runtime.
            var settings = SettingsStore.Load();
            UiTheme.Apply(settings.DarkTheme);
            var setup = new FolderSetupWindow(settings);
            if (setup.ShowDialog() != true)
            {
                if (!EndingAfterFailure) { SessionRuntime.Finish(0); Shutdown(); }
                return;
            }
            var main = new MainWindow(settings);
            MainWindow = main;
            var scanning = new ScanWindow(main);
            scanning.Show();
            try { await main.InitializeAsync(); }
            finally { scanning.Finish(); }
            if (EndingAfterFailure) return;
            ShutdownMode = ShutdownMode.OnLastWindowClose;
            main.Show();
        }
        catch (Exception ex)
        {
            SessionRuntime.StartupFailure(ex);
            if (e.Args.Length == 0 || e.Args.Contains("--gui"))
                System.Windows.MessageBox.Show(ex.Message, "XEP session could not start", MessageBoxButton.OK, MessageBoxImage.Error);
            else Console.Error.WriteLine(ex);
            Shutdown(1);
        }
    }

    // An exception no handler caught (an async void handler, a binding, a timer): log it,
    // record the session as failed, show the message and end the application cleanly.
    private void ReportUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        e.Handled = true;
        SessionRuntime.Log("Unhandled exception: " + e.Exception);
        if (EndingAfterFailure) return;
        EndingAfterFailure = true;
        try { SessionRuntime.Finish(1); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SessionRuntime.ExitCode = 1;
            SessionRuntime.Log("The failed session could not be recorded: " + ex.Message);
        }
        // A generation installs its files in several moves; ending the process between them
        // would leave mixed output, so the shutdown waits for the write (MainWindow.Generate).
        var writing = MainWindow is XEnchantmentPatcher.MainWindow { WritingOutput: true };
        var details = SessionRuntime.LogKeepsDetails && SessionRuntime.DirectoryPath is { } folder ? $" The session log in {folder} has the details." : "";
        System.Windows.MessageBox.Show(SessionRuntime.ShownText(e.Exception) + "\n\nXEnchantmentPatcher will close"
            + (writing ? " once the generation has finished writing its files." : ".") + details,
            "XEnchantmentPatcher stopped", MessageBoxButton.OK, MessageBoxImage.Error);
        // Read again: a write that was running when the box opened has shut the application down
        // itself, and none can start once EndingAfterFailure is set.
        if (MainWindow is not XEnchantmentPatcher.MainWindow { WritingOutput: true }) Shutdown(1);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // Closing with discarded unsaved choices / failed completion metadata
        // must agree with run.json, rather than WPF's default successful exit.
        if (SessionRuntime.ExitCode is { } code) e.ApplicationExitCode = code;
        SessionRuntime.Dispose(); base.OnExit(e);
    }
}
