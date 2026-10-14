using System.Windows;

namespace HollowKnightVision.Windows.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        try
        {
            var options = GameLaunchOptions.Parse(e.Args);
            var mode = options.Mode ?? GameRuntimeModePreferences.Load();
            if (mode is null)
            {
                var chooser = new GameRuntimeModeWindow();
                if (chooser.ShowDialog() != true || chooser.Selection is null)
                {
                    Shutdown(0);
                    return;
                }
                mode = chooser.Selection.Value;
                GameRuntimeModePreferences.Save(mode.Value);
            }
            var window = new MainWindow(mode.Value, options.GamePath);
            MainWindow = window;
            window.Show();
        }
        catch (Exception error)
        {
            MessageBox.Show(error.Message, "Hollow Knight Vision", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(64);
        }
    }
}
