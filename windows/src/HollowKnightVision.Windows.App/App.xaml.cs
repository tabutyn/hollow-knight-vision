using System.Diagnostics;
using System.IO;
using System.Windows;
using HollowKnightVision.Windows.Host;

namespace HollowKnightVision.Windows.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown;
        try
        {
            var options = GameLaunchOptions.Parse(e.Args);
            var runningExecutables = Process.GetProcessesByName("hollow_knight")
                .Select(TryExecutablePath)
                .Where(path => path is not null)
                .Cast<string>()
                .ToArray();
            var normalExecutable = HollowKnightGameLauncher.FindCompatibleExecutable(
                requireModded: false,
                options.GamePath,
                runningExecutables);
            var moddedExecutable = HollowKnightGameLauncher.FindCompatibleExecutable(
                requireModded: true,
                options.GamePath,
                runningExecutables);
            var mode = options.Mode;
            if (GameRuntimeModeLaunchPolicy.RequiresChoice(mode))
            {
                var chooser = new GameRuntimeModeWindow(
                    normalExecutable is not null,
                    moddedExecutable is not null);
                if (chooser.ShowDialog() != true || chooser.Selection is null)
                {
                    Shutdown(0);
                    return;
                }
                mode = chooser.Selection.Value;
            }
            var selectedMode = mode
                ?? throw new InvalidOperationException("A Hollow Knight mode was not selected.");
            var selectedExecutable = selectedMode == GameRuntimeMode.Normal
                ? normalExecutable
                : moddedExecutable;
            if (selectedExecutable is null)
            {
                throw new FileNotFoundException(
                    $"A compatible {selectedMode} Hollow Knight installation was not found.");
            }
            var window = new MainWindow(selectedMode, selectedExecutable);
            MainWindow = window;
            ShutdownMode = System.Windows.ShutdownMode.OnMainWindowClose;
            window.Show();
        }
        catch (Exception error)
        {
            MessageBox.Show(error.Message, "Hollow Knight Vision", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(64);
        }
    }

    private static string? TryExecutablePath(Process process)
    {
        try { return process.MainModule?.FileName; }
        catch (Exception error) when (error is InvalidOperationException
            or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            return null;
        }
        finally
        {
            process.Dispose();
        }
    }
}
