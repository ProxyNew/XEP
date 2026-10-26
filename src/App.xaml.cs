using System.Windows;

namespace XEnchantmentPatcher;

public partial class App : System.Windows.Application
{
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
            base.OnStartup(e);
            // Explicit construction keeps WPF from creating a hidden GUI after CLI
            // Shutdown. StartupUri cannot be set to null on this WPF runtime.
            var settings = SettingsStore.Load();
            UiTheme.Apply(settings.DarkTheme);
            var setup = new FolderSetupWindow(settings);
            if (setup.ShowDialog() != true) { SessionRuntime.Finish(0); Shutdown(); return; }
            var main = new MainWindow(settings);
            MainWindow = main;
            var scanning = new ScanWindow(main);
            scanning.Show();
            try { await main.InitializeAsync(); }
            finally { scanning.Finish(); }
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
    protected override void OnExit(ExitEventArgs e)
    {
        // Closing with discarded unsaved choices / failed completion metadata
        // must agree with run.json, rather than WPF's default successful exit.
        if (SessionRuntime.ExitCode is { } code) e.ApplicationExitCode = code;
        SessionRuntime.Dispose(); base.OnExit(e);
    }
}
