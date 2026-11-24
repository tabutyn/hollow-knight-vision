using System.Text.Json.Serialization;

namespace HollowKnightVision.Windows.Core;

public sealed record BotAtlasScore(int NewCells = 0, int Damage = 0, int BlueHealthGained = 0,
    int DwellDecisions = 0, int HeldAttackDecisions = 0, double? PathProgress = null, double? PathAlignment = null)
{
    [JsonIgnore]
    public double Reward => 4 * ((PathProgress ?? 0) + .5 * (PathAlignment ?? 0)) + BlueHealthGained
        - 32d * Damage - .12 * DwellDecisions - .05 * HeldAttackDecisions;
}

/// <summary>Scores the evidence at playback's policy decision cadence.</summary>
internal sealed class BotAtlasRun
{
    private double lastFrameAt = double.NegativeInfinity, lastViewAt = double.NegativeInfinity;
    private long generation;
    private int room;
    private string? scene;
    private int? priorEarned;
    private AtlasPoint? anchor;
    private double enteredAt, healthSince;
    private int? lastHealth, candidateHealth;
    private BotBlueHealthOutcomes? lastBlueHealth;
    private int newCells, damage, blueHealth, dwell, heldAttack;
    private double? pathProgress, pathAlignment;

    internal BotAtlasScore Score => new(newCells, damage, blueHealth, dwell, heldAttack, pathProgress, pathAlignment);

    internal void Observe(BotCaptureObservation row, InputButtons action, InputButtons previous, string? sceneName)
    {
        var time = row.CaptureTimestamp;
        if (!double.IsFinite(time) || time <= lastFrameAt) return;
        lastFrameAt = time;
        if (row.HackerPathVersion == 1 && row.PathAlignment is >= -3 and <= 3 and var alignment)
            pathAlignment = (pathAlignment ?? 0) + alignment;
        if (row.PathProgress is >= 0 and <= 100 and var progress)
            pathProgress = (pathProgress ?? 0) + progress;
        if ((previous & action & InputButtons.ActionX) != 0) heldAttack++;
        if (row.BlueHealthOutcomes is { Gained: >= 0 } current)
        {
            if (lastBlueHealth is { } prior && prior.Epoch == current.Epoch)
            {
                if (current.Gained >= prior.Gained)
                {
                    blueHealth += current.Gained - prior.Gained;
                    lastBlueHealth = current;
                }
            }
            else lastBlueHealth = current;
        }
        else lastBlueHealth = null;

        if (row.AtlasView is not { Width: > 0 and < double.PositiveInfinity, Camera.Count: 2 } view)
        {
            anchor = null;
            candidateHealth = priorEarned = null;
            return;
        }

        if (generation != row.Generation || room != row.RoomId || time - lastViewAt > .6
            || sceneName is not null && scene is not null && sceneName != scene)
        {
            anchor = null;
            priorEarned = lastHealth = candidateHealth = null;
        }
        if (priorEarned is { } earned) newCells += Math.Max(0, view.EarnedCells - earned);
        generation = row.Generation;
        room = row.RoomId;
        scene = sceneName ?? scene;
        lastViewAt = time;
        priorEarned = view.EarnedCells;

        var visible = row.Features.Count >= 5 && row.Features[4] > 0;
        // Compare displacement at the canonical 640-pixel capture scale.
        var position = new AtlasPoint(640 * (view.Camera[0] / view.Width + (visible ? row.Features[0] : .5)),
            640 * (view.Camera[1] / view.Width + (visible ? row.Features[1] : .28)));
        const double changeDistance = 1.25 * BotAtlasFrontiers.CellSize;
        if (anchor is not { } start || LengthSquared(start, position) >= changeDistance * changeDistance)
        {
            anchor = position;
            enteredAt = time;
        }
        if (time - enteredAt >= 2) dwell++;

        if (view.Health != candidateHealth)
        {
            candidateHealth = view.Health;
            healthSince = time;
        }
        if (candidateHealth is { } health && time - healthSince >= .22)
        {
            if (lastHealth is { } oldHealth) damage += Math.Max(0, oldHealth - health);
            lastHealth = health;
        }
    }

    private static double LengthSquared(AtlasPoint left, AtlasPoint right)
    {
        var dx = left.X - right.X;
        var dy = left.Y - right.Y;
        return dx * dx + dy * dy;
    }
}
