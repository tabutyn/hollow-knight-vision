using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace HollowKnightVision.Windows.App;

internal sealed record GameLaunchOptions(GameRuntimeMode? Mode, string? GamePath)
{
    internal static GameLaunchOptions Parse(string[] arguments)
    {
        GameRuntimeMode? mode = null;
        string? gamePath = null;
        for (var index = 0; index < arguments.Length; index++)
        {
            var argument = arguments[index];
            string? modeValue = null;
            if (argument == "--game-mode")
            {
                if (++index >= arguments.Length)
                    throw new ArgumentException("--game-mode requires normal or modded.");
                modeValue = arguments[index];
            }
            else if (argument.StartsWith("--game-mode=", StringComparison.Ordinal))
            {
                modeValue = argument["--game-mode=".Length..];
            }
            if (modeValue is not null)
            {
                if (mode is not null)
                    throw new ArgumentException("--game-mode may be specified only once.");
                mode = modeValue.ToLowerInvariant() switch
                {
                    "normal" => GameRuntimeMode.Normal,
                    "modded" => GameRuntimeMode.Modded,
                    _ => throw new ArgumentException(
                        $"Invalid --game-mode value '{modeValue}'; expected normal or modded.")
                };
                continue;
            }
            if (argument == "--game-path")
            {
                if (++index >= arguments.Length)
                    throw new ArgumentException("--game-path requires a path.");
                gamePath = arguments[index];
            }
            else if (argument.StartsWith("--game-path=", StringComparison.Ordinal))
            {
                gamePath = argument["--game-path=".Length..];
            }
        }
        return new GameLaunchOptions(mode, gamePath);
    }
}

internal sealed class GameRuntimeModeWindow : Window
{
    internal GameRuntimeMode? Selection { get; private set; }

    private static string Text(string key)
    {
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "HollowKnightVision", "initialization-language-v1.txt");
        var language = File.Exists(path) ? File.ReadAllText(path).Trim() : "en";
        return StencilGuidanceText.Text(language, key);
    }

    internal GameRuntimeModeWindow(bool normalAvailable, bool moddedAvailable)
    {
        Title = "Hollow Knight Vision";
        Width = 540;
        Height = 330;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        var panel = new Grid { Margin = new Thickness(30, 26, 30, 24) };
        panel.RowDefinitions.Add(new RowDefinition());
        panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var icon = new Image
        {
            Source = new BitmapImage(new Uri(
                "pack://application:,,,/Resources/HollowKnightVision.ico",
                UriKind.Absolute)),
            Width = 156,
            Height = 156,
            Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 24)
        };
        panel.Children.Add(icon);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center
        };
        Grid.SetRow(buttons, 1);
        var normal = Choice("Normal Hollow Knight", GameRuntimeMode.Normal);
        normal.IsEnabled = normalAvailable;
        var modded = Choice("Modded Hollow Knight", GameRuntimeMode.Modded);
        modded.IsEnabled = moddedAvailable;
        var quit = new Button
        {
            Content = Text("Quit"),
            IsCancel = true,
            MinWidth = 88,
            MinHeight = 42,
            Margin = new Thickness(6),
            Padding = new Thickness(14, 8, 14, 8)
        };
        buttons.Children.Add(normal);
        buttons.Children.Add(modded);
        buttons.Children.Add(quit);
        panel.Children.Add(buttons);
        Content = panel;
    }

    private Button Choice(string title, GameRuntimeMode mode)
    {
        var button = new Button
        {
            Content = Text(title),
            MinWidth = 148,
            MinHeight = 42,
            Margin = new Thickness(6),
            Padding = new Thickness(14, 8, 14, 8)
        };
        button.Click += (_, _) =>
        {
            Selection = mode;
            DialogResult = true;
        };
        return button;
    }
}
