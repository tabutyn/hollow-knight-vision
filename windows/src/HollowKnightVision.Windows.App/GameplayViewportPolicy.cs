namespace HollowKnightVision.Windows.App;

internal readonly record struct ViewportPoint(double X, double Y)
{
    internal bool IsFinite => double.IsFinite(X) && double.IsFinite(Y);
}

internal readonly record struct ViewportSize(double Width, double Height)
{
    internal bool IsUsable => double.IsFinite(Width) && double.IsFinite(Height)
        && Width > 1 && Height > 1;
}

internal readonly record struct ViewportRect(double X, double Y, double Width, double Height)
{
    internal double Right => X + Width;
    internal double Bottom => Y + Height;
    internal double CenterX => X + Width * .5;
    internal double CenterY => Y + Height * .5;
    internal bool IsUsable => double.IsFinite(X) && double.IsFinite(Y)
        && double.IsFinite(Width) && double.IsFinite(Height)
        && double.IsFinite(Right) && double.IsFinite(Bottom)
        && double.IsFinite(CenterX) && double.IsFinite(CenterY)
        && Width > 0 && Height > 0;

    internal ViewportRect Union(ViewportRect other)
    {
        if (!IsUsable) return other;
        if (!other.IsUsable) return this;
        var left = Math.Min(X, other.X);
        var top = Math.Min(Y, other.Y);
        var right = Math.Max(Right, other.Right);
        var bottom = Math.Max(Bottom, other.Bottom);
        return new(left, top, right - left, bottom - top);
    }
}

internal readonly record struct ViewportTransform(double Scale, double OffsetX, double OffsetY)
{
    internal static ViewportTransform Identity => new(1, 0, 0);
    internal bool IsUsable => double.IsFinite(Scale) && Scale > 0
        && double.IsFinite(OffsetX) && double.IsFinite(OffsetY);
}

internal enum ViewportProjectionMode
{
    Room,
    World
}

internal readonly record struct ViewportProjectionTile(
    ViewportRect Bounds,
    double Factor,
    ViewportPoint RoomPosition,
    double RoomScale);

/// <summary>
/// Canonical room/world projection shared by rendering, bounds, and hit tests.
/// Room parallax subtracts factor-scaled camera travel. World placement ignores
/// the live camera and applies the persisted room translation and scale.
/// </summary>
internal static class GameplayViewportProjection
{
    internal static bool IsFinite(ViewportRect rect) => rect.IsUsable;

    internal static ViewportRect Project(
        ViewportProjectionTile tile,
        ViewportProjectionMode mode,
        ViewportPoint camera) => mode switch
        {
            ViewportProjectionMode.Room => new ViewportRect(
                tile.Bounds.X - tile.Factor * camera.X,
                tile.Bounds.Y - tile.Factor * camera.Y,
                tile.Bounds.Width,
                tile.Bounds.Height),
            ViewportProjectionMode.World => new ViewportRect(
                tile.RoomPosition.X + tile.Bounds.X * tile.RoomScale,
                tile.RoomPosition.Y + tile.Bounds.Y * tile.RoomScale,
                tile.Bounds.Width * tile.RoomScale,
                tile.Bounds.Height * tile.RoomScale),
            _ => throw new ArgumentOutOfRangeException(nameof(mode))
        };

    internal static ViewportRect? Bounds(
        IEnumerable<ViewportProjectionTile> tiles,
        ViewportProjectionMode mode,
        ViewportPoint camera)
    {
        ArgumentNullException.ThrowIfNull(tiles);
        ViewportRect? combined = null;
        foreach (var tile in tiles)
        {
            var projected = Project(tile, mode, camera);
            if (!IsFinite(projected)) continue;
            combined = combined is { } existing
                ? existing.Union(projected)
                : projected;
            if (combined is { } candidate && !IsFinite(candidate)) return null;
        }
        return combined;
    }

    internal static ViewportPoint? Unproject(
        ViewportPoint point,
        ViewportProjectionTile tile,
        ViewportProjectionMode mode,
        ViewportPoint camera)
    {
        var projected = Project(tile, mode, camera);
        if (!IsFinite(projected) || !point.IsFinite
            || point.X < projected.X || point.X >= projected.Right
            || point.Y < projected.Y || point.Y >= projected.Bottom)
        {
            return null;
        }

        var u = (point.X - projected.X) / projected.Width;
        var v = (point.Y - projected.Y) / projected.Height;
        var result = new ViewportPoint(
            tile.Bounds.X + u * tile.Bounds.Width,
            tile.Bounds.Y + v * tile.Bounds.Height);
        return result.IsFinite ? result : null;
    }
}

/// <summary>
/// Settles a screen-space translation without changing viewport zoom. Samples
/// are monotonic even when a caller supplies an older timestamp, and a new
/// correction begins from the offset actually displayed at interruption time.
/// </summary>
internal sealed class ViewportTranslationSettler
{
    internal const double DefaultDuration = .2;

    private ViewportPoint startOffset;
    private double? startedAt;
    private double? lastSampledAt;

    internal ViewportTranslationSettler(
        ViewportPoint? offset = null,
        double duration = DefaultDuration)
    {
        var initial = offset is { IsFinite: true } value ? value : new ViewportPoint(0, 0);
        CurrentOffset = initial;
        TargetOffset = initial;
        startOffset = initial;
        Duration = double.IsFinite(duration) ? Math.Max(0, duration) : 0;
    }

    internal ViewportPoint CurrentOffset { get; private set; }
    internal ViewportPoint TargetOffset { get; private set; }
    internal double Duration { get; }

    internal void Settle(ViewportPoint target, double timestamp)
    {
        if (!target.IsFinite || !double.IsFinite(timestamp)) return;
        var displayed = Offset(timestamp);
        startOffset = displayed;
        CurrentOffset = displayed;
        TargetOffset = target;
        startedAt = lastSampledAt;
    }

    internal ViewportPoint Offset(double timestamp)
    {
        if (!double.IsFinite(timestamp)) return CurrentOffset;
        var sampledAt = Math.Max(timestamp, lastSampledAt ?? timestamp);
        lastSampledAt = sampledAt;
        if (startedAt is not { } start) return CurrentOffset;
        if (Duration <= 0)
        {
            CurrentOffset = TargetOffset;
            startedAt = null;
            return CurrentOffset;
        }

        var progress = Math.Clamp((sampledAt - start) / Duration, 0, 1);
        CurrentOffset = new ViewportPoint(
            startOffset.X + (TargetOffset.X - startOffset.X) * progress,
            startOffset.Y + (TargetOffset.Y - startOffset.Y) * progress);
        if (progress >= 1) startedAt = null;
        return CurrentOffset;
    }
}

/// <summary>
/// Platform-neutral owner of the viewport transform. WPF supplies geometry and
/// renders the returned matrix; mode changes, animation, and input all mutate
/// this one state so an interrupted animation always resumes from what was
/// actually displayed.
/// </summary>
internal sealed class GameplayViewportController
{
    internal const double TransitionDuration = .36;
    internal const double FitResponseTime = .22;
    internal const double FitMargin = .90;
    internal const double CurrentMargin = .88;
    internal const double MinimumScale = .08;
    internal const double MaximumScale = 12;

    private sealed record Transition(
        ViewportTransform Start,
        ViewportTransform Target,
        double StartedAt);

    private Transition? transition;
    private ViewportTransform? fitFollowTarget;
    private double? fitFollowLastAt;
    private bool initialized;
    private ViewportPoint authoritativeWorldBasisOffset;
    private ViewportTranslationSettler worldBasisSettler = new();
    private bool didReceiveWorldBasisOffset;

    internal string Mode { get; private set; } = "Current";
    internal ViewportTransform Transform { get; private set; } = ViewportTransform.Identity;
    internal double CurrentZoomMultiplier { get; private set; } = 1;
    internal bool IsAnimating => transition is not null || fitFollowTarget is { } target
        && !NearlyEqual(Transform, target);

    internal void Reset(string mode = "Current")
    {
        Mode = mode;
        Transform = ViewportTransform.Identity;
        CurrentZoomMultiplier = 1;
        transition = null;
        fitFollowTarget = null;
        fitFollowLastAt = null;
        initialized = false;
        authoritativeWorldBasisOffset = default;
        worldBasisSettler = new();
        didReceiveWorldBasisOffset = false;
    }

    internal ViewportTransform Configure(
        string mode,
        ViewportSize viewport,
        ViewportRect? fitBounds,
        ViewportRect? followBounds,
        ViewportPoint? focus,
        double timestamp,
        bool explicitRequest = false)
    {
        Advance(timestamp);
        var changed = !string.Equals(Mode, mode, StringComparison.Ordinal)
            || explicitRequest;
        if (changed && mode == "Current") CurrentZoomMultiplier = 1;
        Mode = mode;

        if (mode == "Free Fly")
        {
            transition = null;
            fitFollowTarget = null;
            fitFollowLastAt = null;
            initialized = true;
            return Transform;
        }

        var target = Target(mode, viewport, fitBounds, followBounds, focus);
        if (target is null) return Transform;
        if (!initialized)
        {
            Transform = target.Value;
            initialized = true;
            fitFollowTarget = mode == "Fit" ? target : null;
            fitFollowLastAt = timestamp;
            return Transform;
        }

        if (changed)
        {
            transition = new(Transform, target.Value, timestamp);
            fitFollowTarget = mode == "Fit" ? target : null;
            fitFollowLastAt = null;
            return Transform;
        }

        if (mode == "Fit")
        {
            fitFollowTarget = target;
            if (transition is not null) transition = transition with { Target = target.Value };
            fitFollowLastAt ??= timestamp;
            return Transform;
        }

        fitFollowTarget = null;
        fitFollowLastAt = null;
        if (transition is not null)
        {
            transition = transition with { Target = target.Value };
            return Advance(timestamp);
        }
        Transform = target.Value;
        return Transform;
    }

    internal ViewportTransform Advance(double timestamp)
    {
        if (!double.IsFinite(timestamp)) return Transform;
        if (transition is { } active)
        {
            var elapsed = Math.Max(0, timestamp - active.StartedAt);
            var progress = Math.Clamp(elapsed / TransitionDuration, 0, 1);
            var eased = progress * progress * (3 - 2 * progress);
            Transform = Interpolate(active.Start, active.Target, eased);
            if (progress >= 1)
            {
                transition = null;
                fitFollowLastAt = timestamp;
            }
            return Transform;
        }

        if (fitFollowTarget is not { } target || NearlyEqual(Transform, target))
        {
            fitFollowLastAt = timestamp;
            return Transform;
        }
        var elapsedSinceDisplay = fitFollowLastAt is { } prior
            ? Math.Max(0, timestamp - prior) : 0;
        fitFollowLastAt = timestamp;
        if (elapsedSinceDisplay <= 0) return Transform;
        var alpha = 1 - Math.Exp(-Math.Min(elapsedSinceDisplay, .05) / FitResponseTime);
        Transform = Interpolate(Transform, target, alpha);
        return Transform;
    }

    internal ViewportTransform ZoomFreeFly(double wheelDelta, ViewportPoint cursor, double timestamp)
    {
        Advance(timestamp);
        if (wheelDelta == 0 || !double.IsFinite(wheelDelta) || !cursor.IsFinite
            || !Transform.IsUsable) return Transform;
        transition = null;
        fitFollowTarget = null;
        fitFollowLastAt = null;
        Mode = "Free Fly";
        initialized = true;
        var factor = Math.Pow(1.12, Math.Clamp(wheelDelta / 120, -32, 32));
        var nextScale = Math.Clamp(Transform.Scale * factor, MinimumScale, MaximumScale);
        var worldX = (cursor.X - Transform.OffsetX) / Transform.Scale;
        var worldY = (cursor.Y - Transform.OffsetY) / Transform.Scale;
        Transform = new(nextScale,
            cursor.X - worldX * nextScale,
            cursor.Y - worldY * nextScale);
        return Transform;
    }

    internal ViewportTransform ZoomCurrent(
        double wheelDelta,
        ViewportSize viewport,
        ViewportRect followBounds,
        ViewportPoint focus,
        double timestamp)
    {
        Advance(timestamp);
        if (Mode != "Current") return Transform;
        transition = null;
        fitFollowTarget = null;
        fitFollowLastAt = null;
        if (!viewport.IsUsable || !followBounds.IsUsable || !focus.IsFinite
            || !double.IsFinite(wheelDelta) || wheelDelta == 0
            || !Transform.IsUsable) return Transform;
        var factor = Math.Pow(1.12, Math.Clamp(wheelDelta / 120, -32, 32));
        var nextScale = Math.Clamp(Transform.Scale * factor, MinimumScale, MaximumScale);
        var baseScale = Math.Min(
            viewport.Width / followBounds.Width,
            viewport.Height / followBounds.Height) * CurrentMargin;
        if (!double.IsFinite(baseScale) || baseScale <= 0) return Transform;
        CurrentZoomMultiplier = nextScale / baseScale;
        Transform = new(nextScale,
            viewport.Width * .5 - focus.X * nextScale,
            viewport.Height * .5 - focus.Y * nextScale);
        initialized = true;
        return Transform;
    }

    internal ViewportTransform SetFreeFlyTransform(ViewportTransform transform)
    {
        if (!transform.IsUsable) return Transform;
        Mode = "Free Fly";
        Transform = transform;
        transition = null;
        fitFollowTarget = null;
        fitFollowLastAt = null;
        initialized = true;
        return Transform;
    }

    internal ViewportTransform Rebase(double localX, double localY)
    {
        if (!double.IsFinite(localX) || !double.IsFinite(localY)) return Transform;
        Transform = Rebased(Transform, localX, localY);
        if (transition is { } active)
            transition = active with
            {
                Start = Rebased(active.Start, localX, localY),
                Target = Rebased(active.Target, localX, localY)
            };
        if (fitFollowTarget is { } fit) fitFollowTarget = Rebased(fit, localX, localY);
        return Transform;
    }

    /// <summary>
    /// Projects atlas/world coordinates through the same scale-and-translate
    /// transform that MainWindow applies to the WPF scene canvas.
    /// </summary>
    internal ViewportPoint? Project(ViewportPoint point)
    {
        if (!point.IsFinite || !Transform.IsUsable) return null;
        return new(
            point.X * Transform.Scale + Transform.OffsetX,
            point.Y * Transform.Scale + Transform.OffsetY);
    }

    internal void SetInitialWorldBasisOffset(ViewportPoint offset)
    {
        if (!offset.IsFinite) return;
        authoritativeWorldBasisOffset = offset;
        worldBasisSettler = new ViewportTranslationSettler(offset);
        didReceiveWorldBasisOffset = true;
    }

    internal void UpdateWorldBasisOffset(ViewportPoint offset, double timestamp)
    {
        if (!offset.IsFinite || !double.IsFinite(timestamp)) return;
        if (!didReceiveWorldBasisOffset)
        {
            SetInitialWorldBasisOffset(offset);
            return;
        }
        if (offset == authoritativeWorldBasisOffset) return;
        authoritativeWorldBasisOffset = offset;
        worldBasisSettler.Settle(offset, timestamp);
    }

    internal ViewportPoint AtlasPan(double timestamp)
    {
        var displayed = worldBasisSettler.Offset(timestamp);
        return new ViewportPoint(
            Transform.OffsetX
                + (displayed.X - authoritativeWorldBasisOffset.X) * Transform.Scale,
            Transform.OffsetY
                + (displayed.Y - authoritativeWorldBasisOffset.Y) * Transform.Scale);
    }

    private ViewportTransform? Target(
        string mode,
        ViewportSize viewport,
        ViewportRect? fitBounds,
        ViewportRect? followBounds,
        ViewportPoint? focus)
    {
        if (!viewport.IsUsable) return null;
        if (mode == "Fit" && fitBounds is { IsUsable: true } fit)
            return Frame(viewport, fit, new(fit.CenterX, fit.CenterY), FitMargin);
        if (mode == "Current" && followBounds is { IsUsable: true } follow)
        {
            var center = focus is { IsFinite: true } point
                ? point : new ViewportPoint(follow.CenterX, follow.CenterY);
            return Frame(viewport, follow, center,
                CurrentMargin * CurrentZoomMultiplier);
        }
        return null;
    }

    private static ViewportTransform Frame(
        ViewportSize viewport,
        ViewportRect bounds,
        ViewportPoint focus,
        double margin)
    {
        var scale = Math.Clamp(Math.Min(
            viewport.Width / bounds.Width,
            viewport.Height / bounds.Height) * margin,
            MinimumScale,
            MaximumScale);
        return new(scale,
            viewport.Width * .5 - focus.X * scale,
            viewport.Height * .5 - focus.Y * scale);
    }

    private static ViewportTransform Rebased(ViewportTransform value, double x, double y) =>
        new(value.Scale,
            value.OffsetX + x * value.Scale,
            value.OffsetY + y * value.Scale);

    private static ViewportTransform Interpolate(
        ViewportTransform start,
        ViewportTransform target,
        double progress) => new(
            start.Scale + (target.Scale - start.Scale) * progress,
            start.OffsetX + (target.OffsetX - start.OffsetX) * progress,
            start.OffsetY + (target.OffsetY - start.OffsetY) * progress);

    private static bool NearlyEqual(ViewportTransform left, ViewportTransform right) =>
        Math.Abs(left.Scale - right.Scale) < .000_001
        && Math.Abs(left.OffsetX - right.OffsetX) < .001
        && Math.Abs(left.OffsetY - right.OffsetY) < .001;
}

internal static class GameplayViewportPolicy
{
    internal static double CurrentZoom(double zoom, double wheelDelta)
    {
        if (!double.IsFinite(zoom) || zoom <= 0) zoom = 1;
        zoom = Math.Clamp(zoom, 0.1, 8);
        if (!double.IsFinite(wheelDelta)) return zoom;
        return Math.Clamp(zoom * Math.Pow(1.12,
            Math.Clamp(wheelDelta / 120, -32, 32)), 0.1, 8);
    }

    internal static bool CanPan(string mode) => mode is "Current" or "Free Fly";
    internal static bool ForwardsGamePointer(string mode) => mode != "Free Fly";

    // Unknown frames include fades and cutscenes. Do not interpret a missing
    // HUD as an instruction to relocate or refit the live image.
    internal static bool UsesAtlasCoordinates(string? context, bool wasRegistered) =>
        context == "game" || context is null && wasRegistered;

    // Retained for the existing policy contract; the controller is the actual
    // transform owner used by MainWindow.
    internal static bool KeepTransform(string mode, bool preserveFit, bool hasTransform) =>
        hasTransform && (mode == "Free Fly" || mode == "Fit" && preserveFit);

    internal static ViewportPoint? NormalizeGamePointer(
        ViewportPoint viewPoint,
        ViewportRect displayedLiveFrame,
        bool invertY)
    {
        if (!viewPoint.IsFinite || !displayedLiveFrame.IsUsable
            || viewPoint.X < displayedLiveFrame.X
            || viewPoint.Y < displayedLiveFrame.Y
            || viewPoint.X > displayedLiveFrame.Right
            || viewPoint.Y > displayedLiveFrame.Bottom) return null;
        var x = (viewPoint.X - displayedLiveFrame.X) / displayedLiveFrame.Width;
        var y = (viewPoint.Y - displayedLiveFrame.Y) / displayedLiveFrame.Height;
        return new(x, invertY ? 1 - y : y);
    }
}
