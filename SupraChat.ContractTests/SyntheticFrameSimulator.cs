using System;
using System.Collections.Generic;

namespace SupraChat.Core;

/// <summary>Opaque capability for integer-only synthetic frame simulations.</summary>
public sealed class SyntheticFrameFixture
{
    internal SyntheticFrameFixture() { }
}

public enum SyntheticFrameTreatment { DropLast, RepeatLast, FailBeforeDelivery }

public sealed record SyntheticFrameReceipt(
    Guid OperationId, SyntheticFrameTreatment Treatment, int InputFrames,
    int OutputFrames, bool SimulatedFailure, long FirstExecutionOrdinal)
{
    public string EvidenceClass => "SYNTHETIC_ONLY";
    public bool ProviderOperationVerified => false;
}

/// <summary>
/// Bounded, process-local deterministic simulator. No network, content, endpoints,
/// browser objects, credentials or production stream can be supplied to this type.
/// </summary>
public sealed class SyntheticFrameSimulator : IDisposable
{
    private sealed class FixtureState
    {
        public readonly Dictionary<Guid, SyntheticFrameReceipt> FirstOutcomes = new();
        public long NextOrdinal;
    }

    private readonly object _gate = new();
    private readonly Dictionary<SyntheticFrameFixture, FixtureState> _fixtures = new();
    private readonly int _maxFixtures;
    private readonly int _maxOutcomes;
    private bool _disposed;

    public SyntheticFrameSimulator(int maxFixtures = 4, int maxOutcomes = 64)
    {
        if (maxFixtures is < 1 or > 16 || maxOutcomes is < 1 or > 128)
            throw new ArgumentOutOfRangeException(nameof(maxFixtures));
        _maxFixtures = maxFixtures;
        _maxOutcomes = maxOutcomes;
    }

    public SyntheticFrameFixture Open()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_fixtures.Count >= _maxFixtures)
                throw new InvalidOperationException("Synthetic fixture limit reached.");
            var fixture = new SyntheticFrameFixture();
            _fixtures.Add(fixture, new FixtureState());
            return fixture;
        }
    }

    public SyntheticFrameReceipt Apply(SyntheticFrameFixture fixture, Guid operationId,
        SyntheticFrameTreatment treatment, int inputFrames)
    {
        lock (_gate)
        {
            var state = Require(fixture);
            if (operationId == Guid.Empty || !Enum.IsDefined(treatment) || inputFrames is < 1 or > 64)
                throw new UnauthorizedAccessException("Synthetic fixture operation refused.");

            if (state.FirstOutcomes.TryGetValue(operationId, out var prior))
            {
                if (prior.Treatment != treatment || prior.InputFrames != inputFrames)
                    throw new InvalidOperationException("Synthetic operation key conflict.");
                return prior;
            }
            if (state.FirstOutcomes.Count >= _maxOutcomes)
                throw new InvalidOperationException("Synthetic result bound exceeded.");

            var frames = treatment switch
            {
                SyntheticFrameTreatment.DropLast => inputFrames - 1,
                SyntheticFrameTreatment.RepeatLast => inputFrames + 1,
                SyntheticFrameTreatment.FailBeforeDelivery => 0,
                _ => throw new UnauthorizedAccessException("Synthetic operation refused.")
            };
            var receipt = new SyntheticFrameReceipt(operationId, treatment, inputFrames,
                frames, treatment == SyntheticFrameTreatment.FailBeforeDelivery, ++state.NextOrdinal);
            state.FirstOutcomes.Add(operationId, receipt);
            return receipt;
        }
    }

    public void Close(SyntheticFrameFixture fixture)
    {
        lock (_gate)
        {
            Require(fixture);
            _fixtures.Remove(fixture);
        }
    }

    private FixtureState Require(SyntheticFrameFixture fixture)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (fixture is null || !_fixtures.TryGetValue(fixture, out var result))
            throw new UnauthorizedAccessException("Synthetic fixture capability refused.");
        return result;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _fixtures.Clear();
        }
    }
}
