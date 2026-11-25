using HollowKnightVision.Windows.Core;

internal static class BotStarterPolicyTests
{
    internal static void TestBuiltInPoliciesUseSharedFeaturesAndRelease()
    {
        const int schema = BotPolicyContract.CurrentVersion;
        var names = BotPolicyContract.Names(schema)!.ToList();
        var newest = names.Count * (BotRecordingContract.HistoryLength - 1);
        var input = new float[names.Count * BotRecordingContract.HistoryLength];
        var explore = BotStarterPolicy.Load();
        Check(explore.ObservationSchema == schema && explore.Decide(input).Buttons == InputButtons.Right,
            "The untrained explorer should have an initial direction with fresh neutral geometry.");
        Set(("atlas_present", 1), ("atlas_left", .9f), ("atlas_right", .2f));
        Check(explore.Decide(input).Buttons == InputButtons.Left, "Atlas coverage should direct default exploration.");
        Set(("shared_path_present", 1), ("shared_path_dx", .3f), ("road_present", 1), ("road_dx", -.6f), ("blockade_direction", 1));
        Check(explore.Decide(input).Buttons == (InputButtons.Right | InputButtons.ActionZ),
            "The registered route should win, with a jump at a measured blockade.");
        Set(("previous_jump", 1));
        Check(explore.Decide(input).Buttons == InputButtons.Right, "A held jump has to release.");

        var traverse = BotStarterPolicy.Load(BotAbilitySkill.Traverse);
        Set(("previous_jump", 0));
        Check(traverse.Decide(input, waypoint: new(-.5, .6, .2, .4, false)).Buttons == (InputButtons.Left | InputButtons.ActionZ),
            "A specific bound traverse waypoint takes precedence over the generic route.");
        var fight = BotStarterPolicy.Load(BotAbilitySkill.Fight);
        var recover = BotStarterPolicy.Load(BotAbilitySkill.Recover);
        Set(("enemy_0_present", 1), ("enemy_0_x", -.05f), ("enemy_0_y", .08f));
        Check(fight.Decide(input).Buttons == (InputButtons.Left | InputButtons.ActionX)
            && recover.Decide(input).Buttons == InputButtons.Right, "Combat and recovery must use the same observed enemy.");
        Set(("previous_attack", 1));
        Check(fight.Decide(input).Buttons == InputButtons.Left, "The built-in attack must release before another press.");
        Set(("enemy_0_present", 0), ("previous_attack", 0), ("health_known", 1), ("health_fraction", .1f),
            ("lifeblood_cocoon_present", 1), ("lifeblood_cocoon_dx", .02f), ("lifeblood_cocoon_dy", .02f));
        Check(recover.Decide(input).Buttons == (InputButtons.Right | InputButtons.ActionX),
            "Low health with space should use the visible Lifeblood cocoon.");
        var progress = BotStarterPolicy.Load(BotAbilitySkill.Progress);
        Set(("geo_cache_present", 1), ("geo_cache_dx", -.04f), ("geo_cache_dy", .02f));
        Check(progress.Decide(input).Buttons == (InputButtons.Left | InputButtons.ActionX),
            "Progress should use the visible Geo cache before a route.");

        foreach (var domain in new[] { BotCaptureDomain.Gameplay, BotCaptureDomain.Hacker })
        {
            var playback = new BotPolicyPlayback(explore, domain);
            var source = domain == BotCaptureDomain.Hacker ? "human-authored" : "detected";
            var features = new double[BotRecordingContract.FeatureNames.Count];
            features[0] = .5;
            features[4] = features[6] = 1;
            var row = new BotCaptureObservation(24, 24, 24.01, 18, 1, 1, features, [], source)
                { DetectionTimestamp = 24 };
            var decision = playback.Evaluate(row, InputButtons.None, 24.03)?.Decision;
            Check(decision?.Action.Buttons == InputButtons.Right && decision.Value.Deadline < 24.22,
                "An untrained session can drive either camera domain only for a bounded fresh observation.");
            var stale = row with { CaptureTimestamp = 24.05, CapturedAt = 24.05, DetectionTimestamp = 24.05, AvailableAt = 24.06 };
            Check(playback.Evaluate(stale, InputButtons.Right, 25)?.Decision is null,
                "A stale observation must stop issuing movement even before the first training.");
        }

        void Set(params (string Name, float Value)[] values)
        {
            foreach (var entry in values) input[newest + names.IndexOf(entry.Name)] = entry.Value;
        }
    }

    private static void Check(bool value, string operation)
    {
        if (!value) throw new InvalidOperationException(operation);
    }
}
