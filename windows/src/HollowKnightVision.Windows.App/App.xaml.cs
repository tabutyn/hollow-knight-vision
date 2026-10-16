using System.Windows;

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
            var mode = options.Mode;
            if (GameRuntimeModeLaunchPolicy.RequiresChoice(mode))
            {
                var chooser = new GameRuntimeModeWindow();
                if (chooser.ShowDialog() != true || chooser.Selection is null)
                {
                    Shutdown(0);
                    return;
                }
                mode = chooser.Selection.Value;
            }
            var selectedMode = mode
                ?? throw new InvalidOperationException("A Hollow Knight mode was not selected.");
            var window = new MainWindow(selectedMode, options.GamePath);
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
}
