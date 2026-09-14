using RetailPOS.Application.Hardware;
using RetailPOS.Domain.Enums;

namespace RetailPOS.Infrastructure.Hardware;

public sealed class SimulatedWeighingScale : IWeighingScale
{
    public decimal CurrentWeight { get; set; } = 1.250m;
    public ScaleWeightUnit Unit { get; set; } = ScaleWeightUnit.Kilogram;
    public bool IsStable { get; set; } = true;
    public bool IsZero => CurrentWeight == 0m;
    public bool IsTare { get; set; }
    public bool SimulateDisconnected { get; set; }

    public Task<ScaleWeightReadingResult> ReadWeightAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (SimulateDisconnected)
            return Task.FromResult(ScaleWeightReadingResult.Fail("Scale disconnected."));

        var raw = $"ST,GS,+   {CurrentWeight:F3}kg";
        return Task.FromResult(ScaleWeightReadingResult.Ok(CurrentWeight, Unit, IsStable, IsZero, IsTare, raw));
    }

    public Task<bool> ZeroAsync(CancellationToken cancellationToken = default)
    {
        if (SimulateDisconnected)
            return Task.FromResult(false);

        CurrentWeight = 0m;
        return Task.FromResult(true);
    }

    public Task<bool> TareAsync(CancellationToken cancellationToken = default)
    {
        if (SimulateDisconnected)
            return Task.FromResult(false);

        IsTare = true;
        CurrentWeight = 0m;
        return Task.FromResult(true);
    }

    public Task<DeviceStatusResult> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        if (SimulateDisconnected)
            return Task.FromResult(DeviceStatusResult.Offline("Scale disconnected"));

        return Task.FromResult(DeviceStatusResult.Online($"Scale ready ({CurrentWeight} {Unit})"));
    }
}
