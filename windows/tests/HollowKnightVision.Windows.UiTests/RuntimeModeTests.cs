using System.Collections;
using System.IO;
using System.Reflection;
using System.Runtime.ExceptionServices;
using HollowKnightVision.Windows.App;
using HollowKnightVision.Windows.Core;
using HollowKnightVision.Windows.Host;

internal static class RuntimeModeTests
{
    public static void Run()
    {
        var tests = new (string Name, Action Test)[]
        {
            ("ordinary launch always requires mode choice", TestOrdinaryLaunchAlwaysRequiresModeChoice),
            ("game-mode argument accepts both forms", TestGameModeArgumentAcceptsBothForms),
            ("game-mode argument rejects invalid and repeated values", TestGameModeArgumentRejectsInvalidAndRepeatedValues),
            ("game-path argument accepts managed launch forms", TestGamePathArgumentAcceptsManagedLaunchForms),
            ("normal initialization omits mod-only menu label", TestNormalInitializationOmitsModOnlyMenuLabel),
            ("running installation must match selected mode", TestRunningInstallationMustMatchSelectedMode),
            ("discovery only enables complete compatible installations", TestDiscoveryOnlyEnablesCompleteCompatibleInstallations)
        };
        foreach (var (name, test) in tests)
        {
            test();
            Console.WriteLine($"PASS {name}");
        }
    }

    public static void TestOrdinaryLaunchAlwaysRequiresModeChoice()
    {
        Check(RequiresChoice(null));
        Check(!RequiresChoice(GameRuntimeMode.Normal));
        Check(!RequiresChoice(GameRuntimeMode.Modded));
    }

    public static void TestGameModeArgumentAcceptsBothForms()
    {
        Equal(
            GameRuntimeMode.Normal,
            Mode(Parse("Vision", "--game-mode", "normal"))!.Value);
        Equal(
            GameRuntimeMode.Modded,
            Mode(Parse("Vision", "--game-mode=MODDED"))!.Value);
    }

    public static void TestGameModeArgumentRejectsInvalidAndRepeatedValues()
    {
        Throws<ArgumentException>(() => Parse(
            "Vision", "--game-mode", "vanilla"));
        Throws<ArgumentException>(() => Parse(
            "Vision", "--game-mode=normal", "--game-mode=modded"));
    }

    public static void TestGamePathArgumentAcceptsManagedLaunchForms()
    {
        Equal(
            "/Games/Stock/hollow_knight.app",
            GamePath(Parse(
                "Vision", "--game-path", "/Games/Stock/hollow_knight.app"))!);
        Equal(
            "/Games/Modded/hollow_knight.app",
            GamePath(Parse(
                "Vision", "--game-path=/Games/Modded/hollow_knight.app"))!);
        Throws<ArgumentException>(() => Parse("Vision", "--game-path"));
    }

    public static void TestNormalInitializationOmitsModOnlyMenuLabel()
    {
        var normal = MissingSteps(GameRuntimeMode.Normal);
        var modded = MissingSteps(GameRuntimeMode.Modded);

        Check(!normal.Any(step => step.ClassIdentifier == "shared.mods"));
        Check(modded.Any(step => step.ClassIdentifier == "shared.mods"));
        Equal(normal.Length + 6, modded.Length);
        Check(!normal.Any(step => step.ContextIdentifier == "mods"));
    }

    public static void TestRunningInstallationMustMatchSelectedMode()
    {
        using var fixture = new InstallationFixture();
        var executable = fixture.CreateExecutable("Selected");
        var managed = fixture.Managed("Selected");

        Equal(
            InstallationIssue.NotHollowKnightInstallation,
            Issue(executable, GameRuntimeMode.Normal)!.Value);
        File.WriteAllBytes(Path.Combine(managed, "Assembly-CSharp.dll"), []);
        Check(Issue(executable, GameRuntimeMode.Normal) is null);
        Equal(
            InstallationIssue.IncompleteModdedGame,
            Issue(executable, GameRuntimeMode.Modded)!.Value);

        var receiver = Path.Combine(
            managed,
            "Mods", "HollowKnightVisionInputReceiver",
            "HollowKnightVisionInputReceiver.dll");
        Directory.CreateDirectory(Path.GetDirectoryName(receiver)!);
        File.WriteAllBytes(receiver, []);
        Equal(
            InstallationIssue.ModdedGameInNormalMode,
            Issue(executable, GameRuntimeMode.Normal)!.Value);
        Equal(
            InstallationIssue.IncompleteModdedGame,
            Issue(executable, GameRuntimeMode.Modded)!.Value);

        File.WriteAllBytes(Path.Combine(managed, "MMHOOK_Assembly-CSharp.dll"), []);
        Check(Issue(executable, GameRuntimeMode.Modded) is null);
        Equal(
            InstallationIssue.ModdedGameInNormalMode,
            Issue(executable, GameRuntimeMode.Normal)!.Value);
    }

    public static void TestDiscoveryOnlyEnablesCompleteCompatibleInstallations()
    {
        using var fixture = new InstallationFixture();
        var normal = fixture.CreateComplete("Normal", modded: false);
        var modded = fixture.CreateComplete("Modded", modded: true);
        var disabled = fixture.CreateComplete("Modded.hkv-disabled", modded: true);
        var shortcut = Path.Combine(
            fixture.Root,
            "Shortcut",
            "hollow_knight.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(shortcut)!);

        var candidates = new[] { disabled, shortcut, modded, normal };
        Equal(
            Path.GetFullPath(normal),
            HollowKnightGameLauncher.FindCompatibleExecutable(
                requireModded: false,
                additionalCandidates: candidates)!);
        Equal(
            Path.GetFullPath(modded),
            HollowKnightGameLauncher.FindCompatibleExecutable(
                requireModded: true,
                additionalCandidates: candidates)!);
    }

    private static object Parse(params string[] arguments)
    {
        var type = typeof(GameRuntimeMode).Assembly.GetType(
            "HollowKnightVision.Windows.App.GameLaunchOptions",
            throwOnError: true)!;
        var parse = type.GetMethod(
            "Parse",
            BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(type.FullName, "Parse");
        try
        {
            return parse.Invoke(null, [arguments])!;
        }
        catch (TargetInvocationException error) when (error.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(error.InnerException).Throw();
            throw;
        }
    }

    private static GameRuntimeMode? Mode(object options) =>
        (GameRuntimeMode?)options.GetType().GetProperty("Mode")!.GetValue(options);

    private static string? GamePath(object options) =>
        (string?)options.GetType().GetProperty("GamePath")!.GetValue(options);

    private static bool RequiresChoice(GameRuntimeMode? mode)
    {
        var type = typeof(GameRuntimeMode).Assembly.GetType(
            "HollowKnightVision.Windows.App.GameRuntimeModeLaunchPolicy",
            throwOnError: true)!;
        var method = type.GetMethod(
            "RequiresChoice",
            BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(type.FullName, "RequiresChoice");
        return (bool)method.Invoke(null, [mode])!;
    }

    private static RuntimeStep[] MissingSteps(GameRuntimeMode mode)
    {
        var type = typeof(GameRuntimeMode).Assembly.GetType(
            "HollowKnightVision.Windows.App.StencilInitializationPolicy",
            throwOnError: true)!;
        var method = type.GetMethod(
            "MissingSteps",
            BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(type.FullName, "MissingSteps");
        var values = (IEnumerable)method.Invoke(
            null,
            [Array.Empty<SavedLabelingExample>(), "en", mode])!;
        return values.Cast<object>().Select(value => new RuntimeStep(
            (string)value.GetType().GetProperty("ContextIdentifier")!.GetValue(value)!,
            (string)value.GetType().GetProperty("ClassIdentifier")!.GetValue(value)!))
            .ToArray();
    }

    private static InstallationIssue? Issue(
        string executable,
        GameRuntimeMode mode)
    {
        try
        {
            HollowKnightGameLauncher.ValidateInstallation(
                executable,
                requireModded: mode == GameRuntimeMode.Modded);
            return null;
        }
        catch (InvalidOperationException error)
        {
            if (error.Message.StartsWith(
                "The selected path is not a complete",
                StringComparison.Ordinal))
            {
                return InstallationIssue.NotHollowKnightInstallation;
            }
            if (error.Message.StartsWith(
                "Normal mode requires",
                StringComparison.Ordinal))
            {
                return InstallationIssue.ModdedGameInNormalMode;
            }
            if (error.Message.StartsWith(
                "Modded mode requires",
                StringComparison.Ordinal))
            {
                return InstallationIssue.IncompleteModdedGame;
            }
            throw;
        }
    }

    private static void Throws<T>(Action operation)
        where T : Exception
    {
        try
        {
            operation();
        }
        catch (T)
        {
            return;
        }
        throw new Exception($"Expected {typeof(T).Name}.");
    }

    private static void Equal<T>(T expected, T actual)
        where T : notnull
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new Exception($"Expected {expected}; received {actual}.");
        }
    }

    private static void Check(bool condition)
    {
        if (!condition)
        {
            throw new Exception("Runtime-mode assertion failed.");
        }
    }

    private enum InstallationIssue
    {
        NotHollowKnightInstallation,
        ModdedGameInNormalMode,
        IncompleteModdedGame
    }

    private sealed record RuntimeStep(
        string ContextIdentifier,
        string ClassIdentifier);

    private sealed class InstallationFixture : IDisposable
    {
        public InstallationFixture()
        {
            Root = Path.Combine(
                Path.GetTempPath(),
                $"hkv-runtime-mode-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Root);
        }

        public string Root { get; }

        public string Managed(string name) => Path.Combine(
            Root,
            name,
            "hollow_knight_Data",
            "Managed");

        public string CreateExecutable(string name)
        {
            var directory = Path.Combine(Root, name);
            Directory.CreateDirectory(directory);
            var executable = Path.Combine(directory, "hollow_knight.exe");
            File.WriteAllBytes(executable, []);
            Directory.CreateDirectory(Managed(name));
            return executable;
        }

        public string CreateComplete(string name, bool modded)
        {
            var executable = CreateExecutable(name);
            var managed = Managed(name);
            File.WriteAllBytes(Path.Combine(managed, "Assembly-CSharp.dll"), []);
            if (modded)
            {
                File.WriteAllBytes(
                    Path.Combine(managed, "MMHOOK_Assembly-CSharp.dll"),
                    []);
                var receiver = Path.Combine(
                    managed,
                    "Mods", "HollowKnightVisionInputReceiver",
                    "HollowKnightVisionInputReceiver.dll");
                Directory.CreateDirectory(Path.GetDirectoryName(receiver)!);
                File.WriteAllBytes(receiver, []);
            }
            return executable;
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Root, recursive: true);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
            }
        }
    }
}
