namespace HollowKnightVision.Windows.Core;

public sealed class SparseGroundRecoveryGate
{
    private (AtlasPoint Position, double Timestamp)? pending;
    private (AtlasPoint Position, AtlasPoint Current, double Timestamp)? broadPending;

    public void Reset() { pending = null; broadPending = null; }

    public bool Accepts(AtlasPoint candidate, AtlasPoint current, double solveWidth,
        double timestamp, int textureSupport, double? textureError)
    {
        if (!double.IsFinite(solveWidth) || solveWidth <= 0
            || !double.IsFinite(candidate.X) || !double.IsFinite(candidate.Y)
            || !double.IsFinite(current.X) || !double.IsFinite(current.Y)
            || !double.IsFinite(timestamp) || textureSupport < 3
            || textureError is not { } error || !double.IsFinite(error)
            || error is < 0 or > 18)
        {
            Reset(); return false;
        }
        var scale = solveWidth / 640;
        if (Math.Abs(candidate.X - current.X) > 64 * scale
            || Math.Abs(candidate.Y - current.Y) > 96 * scale)
        {
            Reset(); return false;
        }
        var broadPrior = broadPending;
        broadPending = textureSupport >= 12 ? (candidate, current, timestamp) : null;
        if (broadPrior is { } broad && textureSupport >= 12 && error <= 6)
        {
            var dt = timestamp - broad.Timestamp;
            var dx = Math.Abs(candidate.X - broad.Position.X);
            var dy = Math.Abs(candidate.Y - broad.Position.Y);
            var correctionY = candidate.Y - current.Y;
            var priorCorrectionY = broad.Position.Y - broad.Current.Y;
            if (dt is >= .008 and <= .08
                && dx <= 16 * scale && dy <= 12 * scale && Math.Max(dx, dy) >= scale
                && Math.Abs(correctionY) > 32 * scale && Math.Abs(correctionY) <= 64 * scale
                && Math.Abs(priorCorrectionY) > 32 * scale && Math.Abs(priorCorrectionY) <= 64 * scale
                && correctionY * priorCorrectionY > 0)
            {
                Reset(); return true;
            }
        }
        if (error > 12) { pending = null; return false; }
        var prior = pending;
        pending = (candidate, timestamp);
        if (prior is not { } value) return false;
        var elapsed = timestamp - value.Timestamp;
        var motionX = candidate.X - value.Position.X;
        var motionY = candidate.Y - value.Position.Y;
        if (elapsed is < .008 or > .08
            || Math.Abs(motionX) > Math.Min(48, 16 * Math.Max(1, elapsed * 60)) * scale
            || Math.Abs(motionY) < scale
            || Math.Abs(motionY) > Math.Min(64, 32 * Math.Max(1, elapsed * 60)) * scale
            || motionY * (candidate.Y - current.Y) <= 0) return false;
        Reset(); return true;
    }
}

public sealed record GroundPlacementProposal(
    AtlasPoint Position,
    int TextureSupport,
    double? TextureError,
    int InlierCount,
    int GlobalMatchCount,
    bool HasGlobalCorrection,
    bool DistinctFrame,
    bool HasEstablishedGround)
{
    public bool IndependentTextureConsensus { get; init; }
    public bool UniqueLocalTextureMatch { get; init; }
}

public sealed class GroundPlacementRecovery
{
    private sealed record Pending(GroundPlacementProposal Proposal, AtlasPoint Current, double Timestamp);
    private Pending? pending;
    private readonly SparseGroundRecoveryGate sparse = new();
    public bool NeedsVerification { get; private set; }
    public string State => !NeedsVerification ? "trusted" : pending is null ? "unlocated" : "verifying";
    public bool AllowsAtlasWrite => !NeedsVerification;

    public void Reset()
    {
        pending = null; NeedsVerification = false; sparse.Reset();
    }
    public void MissingObservation()
    {
        NeedsVerification = false; pending = null; sparse.Reset();
    }
    public bool RetainsCandidate(double timestamp) => pending is { } value
        && timestamp >= value.Timestamp && timestamp - value.Timestamp <= .12;

    public bool Accepts(GroundPlacementProposal proposal, AtlasPoint current,
        double solveWidth, double timestamp, double? captureElapsed)
    {
        var position = proposal.Position;
        if (!double.IsFinite(solveWidth) || solveWidth <= 0 || !double.IsFinite(timestamp)
            || !double.IsFinite(position.X) || !double.IsFinite(position.Y)
            || !double.IsFinite(current.X) || !double.IsFinite(current.Y))
        {
            MissingObservation(); return false;
        }
        if (!proposal.HasEstablishedGround)
        {
            NeedsVerification = false; pending = null; sparse.Reset(); return true;
        }
        // Sudden camera shake can exceed the normal continuity envelope.
        // A broad search with independent, spatially spread texture agreement
        // supplies direct evidence for that same frame; speed alone must not
        // replace it with a smaller coarse-motion estimate.
        var maximumVerifiedStep = 192 * solveWidth / 640;
        if (proposal.IndependentTextureConsensus && proposal.DistinctFrame
            && !proposal.HasGlobalCorrection
            && (proposal.TextureSupport >= 10 || (proposal.TextureSupport >= 4 && proposal.UniqueLocalTextureMatch))
            && proposal.TextureError is >= 0 and <= 10
            && Math.Abs(position.X - current.X) <= maximumVerifiedStep
            && Math.Abs(position.Y - current.Y) <= maximumVerifiedStep)
        {
            Confirm(); return true;
        }
        var continuous = GroundPoseContinuityGate.Accepts(position, current, solveWidth,
            proposal.GlobalMatchCount, proposal.HasGlobalCorrection, proposal.InlierCount,
            proposal.TextureSupport, proposal.TextureError, captureElapsed);
        if ((proposal.HasGlobalCorrection
                && proposal.GlobalMatchCount >= GroundPoseContinuityGate.MinimumGlobalMatchSupport)
            || (!NeedsVerification && continuous))
        {
            Confirm(); return true;
        }
        NeedsVerification = true;
        var scale = solveWidth / 640;
        if (proposal.TextureSupport < 3 || proposal.TextureError is not { } error
            || !double.IsFinite(error) || error is < 0 or > 18
            || Math.Abs(position.X - current.X) > 64 * scale
            || Math.Abs(position.Y - current.Y) > 96 * scale)
        {
            pending = null; sparse.Reset(); return false;
        }
        if (!proposal.DistinctFrame)
        {
            if (!RetainsCandidate(timestamp)) { pending = null; sparse.Reset(); }
            return false;
        }
        var prior = pending;
        pending = new(proposal, current, timestamp);
        var sparseAccepted = sparse.Accepts(position, current, solveWidth, timestamp,
            proposal.TextureSupport, error);
        if (prior is not null)
        {
            var dt = timestamp - prior.Timestamp;
            var priorCorrection = new CameraVector(
                prior.Proposal.Position.X - prior.Current.X,
                prior.Proposal.Position.Y - prior.Current.Y);
            var correction = new CameraVector(position.X - current.X, position.Y - current.Y);
            var coherentCorrection = CameraMath.Length(
                correction.X - priorCorrection.X, correction.Y - priorCorrection.Y) <= 24 * scale;
            var motionLimit = Math.Min(48, Math.Max(16, dt * 960)) * scale;
            var coherentMotion = CameraMath.Length(
                position.X - prior.Proposal.Position.X,
                position.Y - prior.Proposal.Position.Y) <= motionLimit;
            var strong = continuous
                ? proposal.TextureSupport >= 3 && error <= 12
                : proposal.TextureSupport >= Math.Max(6, Math.Min(12, prior.Proposal.TextureSupport))
                    && prior.Proposal.TextureSupport >= 6 && error <= 10
                    && Math.Abs(correction.Y) <= 64 * scale;
            if (dt is >= .008 and <= .12 && coherentCorrection && coherentMotion
                && (strong || sparseAccepted))
            {
                Confirm(); return true;
            }
        }
        return false;
    }

    private void Confirm()
    {
        NeedsVerification = false; pending = null; sparse.Reset();
    }
}
