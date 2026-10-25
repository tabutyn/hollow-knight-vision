using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using HollowKnightVision.Windows.Core;

[assembly: InternalsVisibleTo("HollowKnightVision.Windows.UiTests")]

namespace HollowKnightVision.Windows.Host;

public static class AutoNavigationRunner
{
    private static readonly TimeSpan InputPulse = TimeSpan.FromMilliseconds(85);

    public static async Task<int> RunAsync(
        string? gameExecutable,
        int timeoutSeconds,
        int intervalMilliseconds)
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
        Console.CancelKeyPress += Cancel;
        try
        {
            var window = HollowKnightWindowLocator.FindBest();
            if (window is null)
            {
                var process = HollowKnightGameLauncher.Start(gameExecutable);
                WriteEvent("game-launched", new { processId = process.Id, executable = process.MainModule?.FileName });
                window = await WaitForWindowAsync(cancellation.Token).ConfigureAwait(false);
            }
            WriteEvent("game-window", new { window.ProcessId, window.Title, window.ClientWidth, window.ClientHeight });
            var foreground = HollowKnightWindowLocator.TryBringToForeground(window);
            WriteEvent("game-foreground", new { ok = foreground, window.ProcessId });
            if (!foreground)
            {
                throw new InvalidOperationException(
                    "Hollow Knight could not be brought to the foreground for visible-client capture.");
            }
            await Task.Delay(500, cancellation.Token).ConfigureAwait(false);

            await using var receiver = await ReceiverConnection
                .ConnectWithRetryAsync(cancellation.Token).ConfigureAwait(false);
            WriteEvent("receiver-ready", new { sessionID = receiver.SessionId });

            using var monitorCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                cancellation.Token);
            var monitor = new ReceiverMonitor();
            var monitorTask = monitor.RunAsync(receiver, monitorCancellation.Token);

            var startup = StartupEvidenceFlow.CreateDefault();
            WriteEvent("startup-recognizers", new { startup.Diagnostic });
            var capture = new GdiWindowCapture();
            long frameId = 0;
            GameStartupPhase? reportedPhase = null;
            try
            {
                while (!cancellation.IsCancellationRequested)
                {
                    var latest = HollowKnightWindowLocator.FindBest()
                        ?? throw new InvalidOperationException("Hollow Knight window closed during startup.");
                    var native = capture.Capture(latest, ++frameId);
                    var frame = CpuBgraNormalizer.Normalize(native);
                    var observation = startup.Observe(frame, monitor.GameplayEvidence);
                    var evidence = observation.Evidence;
                    var decision = observation.Decision;
                    if (reportedPhase != decision.Phase)
                    {
                        reportedPhase = decision.Phase;
                        WriteEvent("startup-phase", new
                        {
                            phase = decision.Phase.ToString(),
                            screen = evidence.Screen?.ToString(),
                            evidence.GameplayLikely,
                            evidence.SelectedOption,
                            frameId
                        });
                    }
                    if (decision.Action is not null)
                    {
                        try
                        {
                            await PulseNavigationActionAsync(
                                    decision.Action.Value,
                                    receiver.PulseAsync,
                                    cancellation.Token)
                                .ConfigureAwait(false);
                            // A sample may arrive during the 85 ms pulse. It is
                            // still older than the screen transition caused by
                            // this action and must not reopen the gameplay gate.
                            startup.NavigationActionCompleted(monitor.GameplayEvidence);
                            WriteEvent("startup-action", new
                            {
                                action = decision.Action.ToString(),
                                evidence.SelectedOption,
                                frameId
                            });
                        }
                        catch
                        {
                            startup.Retry(decision.Action.Value);
                            throw;
                        }
                    }
                    if (decision.AdmitsWorldFrames)
                    {
                        WriteEvent("gameplay-ready", new { ok = true, frameId, processId = latest.ProcessId });
                        return 0;
                    }
                    await Task.Delay(intervalMilliseconds, cancellation.Token).ConfigureAwait(false);
                }
                return 7;
            }
            finally
            {
                monitorCancellation.Cancel();
                try
                {
                    await monitorTask.ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (monitorCancellation.IsCancellationRequested)
                {
                }
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            Console.Error.WriteLine($"Automatic navigation timed out after {timeoutSeconds} seconds.");
            return 7;
        }
        finally
        {
            Console.CancelKeyPress -= Cancel;
        }

        void Cancel(object? sender, ConsoleCancelEventArgs eventArgs)
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        }
    }

    private static async Task<HollowKnightWindow> WaitForWindowAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var window = HollowKnightWindowLocator.FindBest();
            if (window is not null) return window;
            await Task.Delay(250, cancellationToken).ConfigureAwait(false);
        }
    }

    private static void WriteEvent(string type, object details) => Console.WriteLine(
        System.Text.Json.JsonSerializer.Serialize(new { type, details }));

    internal static Task PulseNavigationActionAsync(
        GameStartupAction action,
        Func<InputButtons, TimeSpan, CancellationToken, Task> pulse,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(pulse);
        var button = action switch
        {
            GameStartupAction.MoveSelectionUp => InputButtons.Up,
            GameStartupAction.SelectStartGame or GameStartupAction.SelectProfileOne =>
                InputButtons.ActionZ,
            _ => throw new ArgumentOutOfRangeException(nameof(action))
        };
        return pulse(button, InputPulse, cancellationToken);
    }

    internal readonly record struct StartupStencilEvidence(
        MenuStencilResult? Menu,
        HudStencilResult? Hud);

    internal interface IStartupStencilEvidenceProvider
    {
        StartupStencilEvidence Observe(BgraFrame frame);
    }

    internal readonly record struct StartupFrameObservation(
        GameStartupEvidence Evidence,
        GameStartupDecision Decision);

    internal sealed record StartupReceiverEvidence(
        long Generation,
        double ObservedAt,
        bool GameplayLikely)
    {
        internal static StartupReceiverEvidence None { get; } = new(0, 0, false);

        internal bool IsValid => Generation > 0
            && double.IsFinite(ObservedAt)
            && ObservedAt >= 0;
    }

    /// <summary>
    /// The coordinator owns the gameplay latch. Menu evidence can close it even
    /// while the receiver still exposes its last gameplay sample; once closed,
    /// fresh HUD or receiver evidence can establish gameplay again.
    /// </summary>
    internal sealed class StartupEvidenceFlow
    {
        private readonly IStartupStencilEvidenceProvider stencils;
        private readonly GameStartupDetector detector = new();
        private readonly GameStartupCoordinator coordinator;
        private long receiverGenerationFloor;
        private double receiverTimestampFloor = double.NegativeInfinity;

        internal StartupEvidenceFlow(
            IStartupStencilEvidenceProvider stencils,
            int requiredStableFrames = 8,
            string diagnostic = "injected")
        {
            this.stencils = stencils ?? throw new ArgumentNullException(nameof(stencils));
            coordinator = new(requiredStableFrames);
            Diagnostic = diagnostic;
        }

        internal string Diagnostic { get; }
        internal GameStartupPhase Phase => coordinator.Phase;

        internal static StartupEvidenceFlow CreateDefault(
            string? supportRoot = null,
            int requiredStableFrames = 8)
        {
            var provider = LiveStartupStencilEvidenceProvider.CreateDefault(supportRoot);
            return new(provider, requiredStableFrames, provider.Diagnostic);
        }

        internal StartupFrameObservation Observe(
            BgraFrame frame,
            StartupReceiverEvidence receiverEvidence)
        {
            ArgumentNullException.ThrowIfNull(frame);
            ArgumentNullException.ThrowIfNull(receiverEvidence);
            var stencilEvidence = stencils.Observe(frame);
            var evidence = detector.Detect(
                frame,
                stencilEvidence.Menu,
                stencilEvidence.Hud,
                gameplayIsLatched: coordinator.Phase == GameStartupPhase.Gameplay);

            if (stencilEvidence.Menu?.IsMatch == true)
            {
                InvalidateReceiverEvidence(receiverEvidence);
            }

            // A recognized menu is newer, screen-specific evidence and must be
            // allowed to close gameplay. Receiver telemetry may establish or
            // reopen gameplay only when it was observed after the most recent
            // menu or navigation action.
            if (evidence.Screen is null
                && !evidence.GameplayLikely
                && ReceiverEvidenceIsFresh(receiverEvidence))
            {
                evidence = new GameStartupEvidence(null, GameplayLikely: true);
            }

            var decision = coordinator.Observe(evidence);
            if (decision.Action is not null)
            {
                InvalidateReceiverEvidence(receiverEvidence);
            }
            return new(evidence, decision);
        }

        internal void Retry(GameStartupAction action) => coordinator.Retry(action);

        internal void NavigationActionCompleted(StartupReceiverEvidence receiverEvidence) =>
            InvalidateReceiverEvidence(receiverEvidence);

        private bool ReceiverEvidenceIsFresh(StartupReceiverEvidence evidence) =>
            evidence.IsValid
            && evidence.GameplayLikely
            && evidence.Generation > receiverGenerationFloor
            && evidence.ObservedAt > receiverTimestampFloor;

        private void InvalidateReceiverEvidence(StartupReceiverEvidence evidence)
        {
            if (!evidence.IsValid) return;
            receiverGenerationFloor = Math.Max(receiverGenerationFloor, evidence.Generation);
            receiverTimestampFloor = Math.Max(receiverTimestampFloor, evidence.ObservedAt);
        }
    }

    private sealed class LiveStartupStencilEvidenceProvider : IStartupStencilEvidenceProvider
    {
        private readonly MenuStencilTracker menu;
        private readonly HudStencilTracker hud;
        private readonly bool allowGeometryFallback;

        private LiveStartupStencilEvidenceProvider(
            MenuStencilTracker menu,
            HudStencilTracker hud,
            bool allowGeometryFallback,
            string diagnostic)
        {
            this.menu = menu;
            this.hud = hud;
            this.allowGeometryFallback = allowGeometryFallback;
            Diagnostic = diagnostic;
        }

        internal string Diagnostic { get; }

        internal static LiveStartupStencilEvidenceProvider CreateDefault(string? supportRoot)
        {
            supportRoot ??= Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "HollowKnightVision");
            var examplesRoot = Path.Combine(supportRoot, "labeling-v1", "examples");
            var examples = LoadExamples(examplesRoot);
            var calibrationPath = MenuStencilCalibration.WritablePath(examplesRoot);
            var calibration = new MenuSelectorCalibrationTracker(
                calibrationPath,
                MenuStencilCalibration.ProjectMirrorPath(Environment.GetCommandLineArgs()),
                fallbackCalibration: MenuStencilCalibration.LoadDefault(calibrationPath));

            IReadOnlyList<MenuStencilLiveSource> liveCaptures = [];
            try
            {
                liveCaptures = new MenuStencilLiveCaptureStore(
                        MenuStencilLiveCaptureStore.RootForExamples(examplesRoot))
                    .Load()
                    .Select(item => item.Source)
                    .ToArray();
            }
            catch (Exception error) when (error is IOException
                or UnauthorizedAccessException
                or JsonException
                or InvalidDataException)
            {
            }

            MenuStencilCatalog catalog;
            try
            {
                catalog = new MenuStencilCatalogBuilder(
                        LabelingContractCatalog.ScreenContexts,
                        TryLoadFrame,
                        item => LabelingContractEvaluator.AcceptsMenuExample(item))
                    .Build(
                        examples,
                        MenuStencilCalibration.LoadDefault(calibrationPath),
                        liveCaptures);
            }
            catch (MenuStencilCatalogUnavailableException)
            {
                catalog = new([], 640, 360, 0, 360, []);
            }

            var language = LoadLanguage(supportRoot);
            var capturedHud = CapturedHud(examples, language);
            var allowGeometryFallback = catalog.Scenes.Count == 0;
            var diagnostic = FormattableString.Invariant(
                $"menu-scenes={catalog.Scenes.Count}; title-fallback={(allowGeometryFallback ? "geometry" : "disabled")}; hud={(capturedHud is not null ? "captured" : "unavailable")}; language={language}");
            return new(
                new MenuStencilTracker(catalog, minimumSearchInterval: .1, calibration),
                new HudStencilTracker(capturedHud),
                allowGeometryFallback,
                diagnostic);
        }

        public StartupStencilEvidence Observe(BgraFrame frame)
        {
            var timestamp = (frame.ObservedAt - DateTimeOffset.UnixEpoch).TotalSeconds;
            var menuEvidence = menu.Observe(frame, timestamp)
                ?? (allowGeometryFallback
                    ? FallbackMenuEvidence(frame, timestamp)
                    : null);
            return new(
                menuEvidence,
                hud.Observe(frame, gameplay: true, timestamp, generation: 1));
        }

        // Geometry is a last-resort bootstrap for a truly empty menu catalog
        // and runs only on the validated Hollow Knight client image. Saved
        // stencils always own recognition as soon as one usable scene exists.
        private static MenuStencilResult? FallbackMenuEvidence(
            BgraFrame frame,
            double timestamp) =>
            FallbackTitleEvidence(frame, timestamp)
            ?? FallbackProfileEvidence(frame, timestamp);

        private static MenuStencilResult? FallbackTitleEvidence(
            BgraFrame frame,
            double timestamp)
        {
            var anchorCount = TitleAnchors.Count(rectangle =>
                StructuredBrightShape(frame, rectangle, minimumPixels: 4) is not null);
            if (anchorCount < 3) return null;

            var matches = new List<(TitleSelection Selection, int Score)>();
            foreach (var candidate in TitleSelections)
            {
                var left = StructuredBrightShape(
                    frame, candidate.Left, minimumPixels: 3);
                var right = StructuredBrightShape(
                    frame, candidate.Right, minimumPixels: 3);
                if (left is null || right is null) continue;
                matches.Add((candidate, Math.Min(left.Value.Count, right.Value.Count)));
            }
            if (matches.Count != 1) return null;
            var match = matches[0];

            return new MenuStencilResult(
                "main-title",
                IsMatch: true,
                Confidence: Math.Min(1, anchorCount / 5.0),
                match.Selection.Name,
                Anchors: [],
                SelectorCandidates: [],
                SelectorSearchRegions: [],
                SelectorLanguageEvidenceCount: 0,
                SelectorForegroundEvidenceCount: match.Score * 2,
                SceneEvidenceRatio: anchorCount / 5.0,
                Phase: MenuStencilPhase.Searching,
                ComparisonCount: 0,
                SourceTimestamp: timestamp,
                LanguageIdentifier: null);
        }

        private static MenuStencilResult? FallbackProfileEvidence(
            BgraFrame frame,
            double timestamp)
        {
            var left = StructuredBrightShape(
                frame, ProfileLeft, minimumPixels: 5);
            var numberOne = StructuredBrightShape(
                frame, ProfileNumberOne, minimumPixels: 7);
            var right = StructuredBrightShape(
                frame, ProfileRight, minimumPixels: 5);
            if (left is null || numberOne is null || right is null) return null;
            var minimumMidY = Math.Min(
                left.Value.MidY,
                Math.Min(numberOne.Value.MidY, right.Value.MidY));
            var maximumMidY = Math.Max(
                left.Value.MidY,
                Math.Max(numberOne.Value.MidY, right.Value.MidY));
            if (maximumMidY - minimumMidY > frame.Height * .055) return null;

            var foreground = left.Value.Count + numberOne.Value.Count + right.Value.Count;
            return new MenuStencilResult(
                "select-profile",
                IsMatch: true,
                Confidence: .9,
                "1.",
                Anchors: [],
                SelectorCandidates: [],
                SelectorSearchRegions: [],
                SelectorLanguageEvidenceCount: 0,
                SelectorForegroundEvidenceCount: foreground,
                SceneEvidenceRatio: 1,
                Phase: MenuStencilPhase.Searching,
                ComparisonCount: 0,
                SourceTimestamp: timestamp,
                LanguageIdentifier: null);
        }

        private static BrightShape? StructuredBrightShape(
            BgraFrame frame,
            TitleRect rectangle,
            int minimumPixels)
        {
            var x0 = Math.Clamp(
                (int)Math.Floor(rectangle.X * frame.Width / 640.0),
                0,
                frame.Width);
            var x1 = Math.Clamp(
                (int)Math.Ceiling(rectangle.Right * frame.Width / 640.0),
                0,
                frame.Width);
            var y0 = Math.Clamp(
                (int)Math.Floor(rectangle.Y * frame.Height / 360.0),
                0,
                frame.Height);
            var y1 = Math.Clamp(
                (int)Math.Ceiling(rectangle.Bottom * frame.Height / 360.0),
                0,
                frame.Height);
            var area = checked((x1 - x0) * (y1 - y0));
            if (area <= 0) return null;
            var count = 0;
            var minimumX = x1;
            var maximumX = x0 - 1;
            var minimumY = y1;
            var maximumY = y0 - 1;
            for (var y = y0; y < y1; y++)
            {
                var row = frame.Row(y);
                for (var x = x0; x < x1; x++)
                {
                    var offset = x * BgraFrame.BytesPerPixel;
                    var blue = row[offset];
                    var green = row[offset + 1];
                    var red = row[offset + 2];
                    var minimum = Math.Min(red, Math.Min(green, blue));
                    var maximum = Math.Max(red, Math.Max(green, blue));
                    if (minimum < 145 || maximum - minimum > 72) continue;
                    count++;
                    minimumX = Math.Min(minimumX, x);
                    maximumX = Math.Max(maximumX, x);
                    minimumY = Math.Min(minimumY, y);
                    maximumY = Math.Max(maximumY, y);
                }
            }
            if (count < minimumPixels
                || count > area * .6
                || maximumX - minimumX < 1
                || maximumY - minimumY < 1)
            {
                return null;
            }
            return new(count, (minimumY + maximumY) * .5);
        }

        private readonly record struct BrightShape(int Count, double MidY);

        private readonly record struct TitleRect(
            double X,
            double Y,
            double Width,
            double Height)
        {
            internal double Right => X + Width;
            internal double Bottom => Y + Height;
        }

        private readonly record struct TitleSelection(
            string Name,
            TitleRect Left,
            TitleRect Right);

        private static readonly TitleRect[] TitleAnchors =
        [
            new(289, 209, 61, 9),
            new(297, 231, 45, 8),
            new(283, 254, 75, 7),
            new(303, 274, 35, 8),
            new(291, 296, 57, 10)
        ];

        private static readonly TitleSelection[] TitleSelections =
        [
            new("Start Game", new(267, 201, 14, 11), new(359, 202, 13, 11)),
            new("Options", new(276, 224, 12, 10), new(351, 223, 14, 11)),
            new("Achievements", new(260, 246, 13, 9), new(368, 246, 11, 9)),
            new("Extras", new(282.5, 267.5, 13, 10), new(346.5, 267.5, 13, 11)),
            new("Quit Game", new(269, 288, 14, 11), new(356, 287, 15, 13))
        ];

        private static readonly TitleRect ProfileLeft = new(48, 88.2, 48, 46.8);
        private static readonly TitleRect ProfileNumberOne = new(92.8, 88.2, 51.2, 46.8);
        private static readonly TitleRect ProfileRight = new(409.6, 88.2, 57.6, 46.8);

        private static IReadOnlyList<SavedLabelingExample> LoadExamples(string examplesRoot)
        {
            try
            {
                return new LabelingExampleStore(examplesRoot).Load();
            }
            catch (Exception error) when (error is IOException
                or UnauthorizedAccessException
                or ArgumentException)
            {
                return [];
            }
        }

        private static string LoadLanguage(string supportRoot)
        {
            try
            {
                var path = Path.Combine(supportRoot, "initialization-language-v1.txt");
                var language = File.Exists(path) ? File.ReadAllText(path).Trim() : "en";
                return MenuStencilLiveCaptureStore.SupportedLanguages.Contains(language)
                    ? language
                    : "en";
            }
            catch (Exception error) when (error is IOException
                or UnauthorizedAccessException)
            {
                return "en";
            }
        }

        private static HudStencilTemplate? CapturedHud(
            IReadOnlyList<SavedLabelingExample> examples,
            string language)
        {
            var candidates = examples
                .Where(example => example.Manifest.ContextIdentifier == "gameplay-hud"
                    && example.Manifest.ResolvedLanguageIdentifier == language
                    && example.Manifest.ImageWidth == 640
                    && example.Manifest.ImageHeight == 360)
                .OrderByDescending(example => example.Manifest.Annotations.Count(annotation =>
                    !annotation.IsHardNegative
                    && LabelingClassIdentity.CanonicalIdentifier(annotation.ClassIdentifier)
                        == "game.health"))
                .ThenByDescending(example => example.Manifest.CreatedAt)
                .ThenBy(example => example.Id);
            foreach (var example in candidates)
            {
                var frame = TryLoadFrame(example.ImagePath);
                if (frame is null) continue;
                var template = HudStencilTemplate.Captured(
                    frame,
                    example.Manifest.Annotations,
                    example.Id.ToString());
                if (template is not null) return template;
            }
            return null;
        }

        private static BgraFrame? TryLoadFrame(string path)
        {
            try
            {
                using var stream = new FileStream(
                    path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read);
                return StartupPngReader.Read(stream);
            }
            catch (Exception error) when (error is IOException
                or UnauthorizedAccessException
                or InvalidDataException
                or ArgumentException
                or OverflowException)
            {
                return null;
            }
        }
    }

    /// <summary>Reads the RGBA8 PNGs emitted by the shared labeling stores.</summary>
    private static class StartupPngReader
    {
        private static readonly byte[] Signature =
            [137, (byte)'P', (byte)'N', (byte)'G', 13, 10, 26, 10];

        internal static BgraFrame Read(Stream stream)
        {
            ArgumentNullException.ThrowIfNull(stream);
            if (!stream.CanRead || !stream.CanSeek || stream.Length > int.MaxValue)
                throw new InvalidDataException("PNG source stream is invalid.");
            stream.Position = 0;
            var encoded = new byte[checked((int)stream.Length)];
            stream.ReadExactly(encoded);
            if (encoded.Length < Signature.Length
                || !encoded.AsSpan(0, Signature.Length).SequenceEqual(Signature))
                throw new InvalidDataException("PNG signature is invalid.");

            var offset = Signature.Length;
            var width = 0;
            var height = 0;
            var sawHeader = false;
            var sawEnd = false;
            using var compressed = new MemoryStream();
            while (offset < encoded.Length)
            {
                if (encoded.Length - offset < 12)
                    throw new InvalidDataException("PNG chunk is truncated.");
                var lengthValue = BinaryPrimitives.ReadUInt32BigEndian(encoded.AsSpan(offset, 4));
                if (lengthValue > int.MaxValue)
                    throw new InvalidDataException("PNG chunk is too large.");
                var length = (int)lengthValue;
                offset += 4;
                if (length > encoded.Length - offset - 8)
                    throw new InvalidDataException("PNG chunk data is truncated.");
                var type = encoded.AsSpan(offset, 4);
                offset += 4;
                var data = encoded.AsSpan(offset, length);
                offset += length;
                var actualCrc = BinaryPrimitives.ReadUInt32BigEndian(encoded.AsSpan(offset, 4));
                offset += 4;
                var crc = UpdateCrc(0xffffffffu, type);
                if ((UpdateCrc(crc, data) ^ 0xffffffffu) != actualCrc)
                    throw new InvalidDataException("PNG chunk checksum is invalid.");

                var chunkType = Encoding.ASCII.GetString(type);
                if (chunkType == "IHDR")
                {
                    if (sawHeader || data.Length != 13)
                        throw new InvalidDataException("PNG header is invalid.");
                    width = checked((int)BinaryPrimitives.ReadUInt32BigEndian(data[..4]));
                    height = checked((int)BinaryPrimitives.ReadUInt32BigEndian(data.Slice(4, 4)));
                    if (width is < 2 or > 4096
                        || height is < 2 or > 4096
                        || data[8] != 8
                        || data[9] != 6
                        || data[10] != 0
                        || data[11] != 0
                        || data[12] != 0)
                        throw new InvalidDataException("Only bounded non-interlaced RGBA8 PNG is supported.");
                    sawHeader = true;
                }
                else if (chunkType == "IDAT")
                {
                    if (!sawHeader || sawEnd)
                        throw new InvalidDataException("PNG data ordering is invalid.");
                    compressed.Write(data);
                }
                else if (chunkType == "IEND")
                {
                    if (data.Length != 0)
                        throw new InvalidDataException("PNG end chunk is invalid.");
                    sawEnd = true;
                    break;
                }
            }
            if (!sawHeader || !sawEnd || compressed.Length == 0)
                throw new InvalidDataException("PNG is incomplete.");

            var stride = checked(width * BgraFrame.BytesPerPixel);
            var expectedLength = checked((stride + 1) * height);
            var filtered = new byte[expectedLength];
            compressed.Position = 0;
            using (var inflater = new ZLibStream(
                       compressed,
                       CompressionMode.Decompress,
                       leaveOpen: true))
            {
                inflater.ReadExactly(filtered);
                if (inflater.ReadByte() != -1)
                    throw new InvalidDataException("PNG pixel data length is invalid.");
            }

            var rgba = new byte[checked(stride * height)];
            for (var y = 0; y < height; y++)
            {
                var sourceRow = y * (stride + 1);
                var filter = filtered[sourceRow];
                if (filter > 4) throw new InvalidDataException("PNG filter is invalid.");
                var targetRow = y * stride;
                for (var x = 0; x < stride; x++)
                {
                    var encodedByte = filtered[sourceRow + 1 + x];
                    var left = x >= 4 ? rgba[targetRow + x - 4] : (byte)0;
                    var above = y > 0 ? rgba[targetRow - stride + x] : (byte)0;
                    var upperLeft = y > 0 && x >= 4
                        ? rgba[targetRow - stride + x - 4]
                        : (byte)0;
                    var predictor = filter switch
                    {
                        0 => 0,
                        1 => left,
                        2 => above,
                        3 => (left + above) / 2,
                        4 => Paeth(left, above, upperLeft),
                        _ => throw new InvalidDataException("PNG filter is invalid.")
                    };
                    rgba[targetRow + x] = unchecked((byte)(encodedByte + predictor));
                }
            }

            var bgra = new byte[rgba.Length];
            for (var index = 0; index < rgba.Length; index += 4)
            {
                bgra[index] = rgba[index + 2];
                bgra[index + 1] = rgba[index + 1];
                bgra[index + 2] = rgba[index];
                bgra[index + 3] = rgba[index + 3];
            }
            return new(
                width,
                height,
                stride,
                bgra,
                captureId: 0,
                DateTimeOffset.UnixEpoch);
        }

        private static int Paeth(int left, int above, int upperLeft)
        {
            var estimate = left + above - upperLeft;
            var leftDistance = Math.Abs(estimate - left);
            var aboveDistance = Math.Abs(estimate - above);
            var upperLeftDistance = Math.Abs(estimate - upperLeft);
            return leftDistance <= aboveDistance && leftDistance <= upperLeftDistance
                ? left
                : aboveDistance <= upperLeftDistance ? above : upperLeft;
        }

        private static uint UpdateCrc(uint crc, ReadOnlySpan<byte> data)
        {
            foreach (var value in data)
            {
                crc ^= value;
                for (var bit = 0; bit < 8; bit++)
                    crc = (crc >> 1) ^ (0xedb88320u & (uint)-(int)(crc & 1));
            }
            return crc;
        }
    }

    private sealed class ReceiverMonitor
    {
        private long evidenceGeneration;
        private StartupReceiverEvidence gameplayEvidence = StartupReceiverEvidence.None;

        internal StartupReceiverEvidence GameplayEvidence =>
            Volatile.Read(ref gameplayEvidence);

        internal async Task RunAsync(
            ReceiverClient receiver,
            CancellationToken cancellationToken)
        {
            while (true)
            {
                var line = await receiver.ReceiveLineAsync(cancellationToken).ConfigureAwait(false)
                    ?? throw new IOException("Receiver closed during automatic navigation.");
                if (ReceiverWireCodec.MessageType(line) != "groundTruth") continue;
                var sample = ReceiverWireCodec.DecodeLine<ReceiverGroundTruthSample>(line);
                var gameplayLikely = sample.HeroAvailable
                    && !string.IsNullOrWhiteSpace(sample.SceneName)
                    && sample.HasFiniteCoordinates();
                var generation = Interlocked.Increment(ref evidenceGeneration);
                var observedAt = Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
                Volatile.Write(
                    ref gameplayEvidence,
                    new StartupReceiverEvidence(generation, observedAt, gameplayLikely));
            }
        }
    }
}
