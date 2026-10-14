using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

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

internal static class GameRuntimeModePreferences
{
    private static string PathName => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "HollowKnightVision", "runtime-mode-v1.txt");

    internal static GameRuntimeMode? Load()
    {
        if (!File.Exists(PathName)) return null;
        return File.ReadAllText(PathName).Trim().ToLowerInvariant() switch
        {
            "normal" => GameRuntimeMode.Normal,
            "modded" => GameRuntimeMode.Modded,
            _ => null
        };
    }

    internal static void Save(GameRuntimeMode mode)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(PathName)!);
        File.WriteAllText(PathName, mode.ToString().ToLowerInvariant());
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

    internal GameRuntimeModeWindow()
    {
        Title = "Hollow Knight Vision";
        Width = 480;
        Height = 250;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        var panel = new StackPanel { Margin = new Thickness(24) };
        panel.Children.Add(new TextBlock
        {
            Text = Text("Choose how to run Hollow Knight"),
            FontSize = 20,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 10)
        });
        panel.Children.Add(new TextBlock
        {
            Text = Text("runtimeHelp"),
            TextWrapping = TextWrapping.Wrap,
            Foreground = Brushes.Gray,
            Margin = new Thickness(0, 0, 0, 20)
        });
        var buttons = new UniformGrid { Columns = 2 };
        buttons.Children.Add(Choice("Normal Hollow Knight", GameRuntimeMode.Normal));
        buttons.Children.Add(Choice("Modded Hollow Knight", GameRuntimeMode.Modded));
        panel.Children.Add(buttons);
        Content = panel;
    }

    private Button Choice(string title, GameRuntimeMode mode)
    {
        var button = new Button { Content = Text(title), Margin = new Thickness(6), Padding = new Thickness(12) };
        button.Click += (_, _) =>
        {
            Selection = mode;
            DialogResult = true;
        };
        return button;
    }
}
