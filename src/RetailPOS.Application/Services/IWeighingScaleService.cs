using RetailPOS.Application.Hardware;
using RetailPOS.Application.Persistence;
using RetailPOS.Contracts.Common;
using RetailPOS.Contracts.Hardware;
using RetailPOS.Domain.Enums;
using RetailPOS.Domain.ValueObjects;

namespace RetailPOS.Application.Services;

public interface IWeighingScaleService
{
    Task<ApiResponse<ScaleWeightReadingDto>> GetCurrentWeightAsync(CancellationToken cancellationToken = default);
    Task<ApiResponse<Quantity>> ValidateAndConvertWeightAsync(decimal weight, ScaleWeightUnit scaleUnit, UnitOfMeasure productUnit);
    Task<ApiResponse<bool>> ZeroScaleAsync(Guid terminalId, Guid userId, CancellationToken cancellationToken = default);
    Task<ApiResponse<bool>> TareScaleAsync(Guid terminalId, Guid userId, CancellationToken cancellationToken = default);
}

public sealed class WeighingScaleService : IWeighingScaleService
{
    private readonly IWeighingScale _scale;
    private readonly IAuditLogRepository _auditRepo;
    private readonly IUnitOfWork _unitOfWork;

    public WeighingScaleService(
        IWeighingScale scale,
        IAuditLogRepository auditRepo,
        IUnitOfWork unitOfWork)
    {
        _scale = scale;
        _auditRepo = auditRepo;
        _unitOfWork = unitOfWork;
    }

    public async Task<ApiResponse<ScaleWeightReadingDto>> GetCurrentWeightAsync(CancellationToken cancellationToken = default)
    {
        var reading = await _scale.ReadWeightAsync(cancellationToken);
        if (!reading.Success)
        {
            return ApiResponse.Fail<ScaleWeightReadingDto>(reading.ErrorMessage ?? "Failed to read from scale.", "SCALE_READ_ERROR");
        }

        var dto = new ScaleWeightReadingDto(
            reading.Weight,
            reading.Unit,
            reading.IsStable,
            reading.IsZero,
            reading.IsTare,
            reading.RawReading,
            DateTime.UtcNow);

        return ApiResponse.Ok(dto);
    }

    public Task<ApiResponse<Quantity>> ValidateAndConvertWeightAsync(decimal weight, ScaleWeightUnit scaleUnit, UnitOfMeasure productUnit)
    {
        if (weight <= 0m)
        {
            return Task.FromResult(ApiResponse.Fail<Quantity>("Weight reading must be greater than zero.", "INVALID_WEIGHT"));
        }

        // Exact decimal conversion
        decimal convertedWeight;
        if (scaleUnit == ScaleWeightUnit.Kilogram && productUnit == UnitOfMeasure.Kilogram)
        {
            convertedWeight = weight;
        }
        else if (scaleUnit == ScaleWeightUnit.Gram && productUnit == UnitOfMeasure.Kilogram)
        {
            convertedWeight = weight / 1000m;
        }
        else if (scaleUnit == ScaleWeightUnit.Kilogram && productUnit == UnitOfMeasure.Gram)
        {
            convertedWeight = weight * 1000m;
        }
        else if (scaleUnit == ScaleWeightUnit.Gram && productUnit == UnitOfMeasure.Gram)
        {
            convertedWeight = weight;
        }
        else
        {
            return Task.FromResult(ApiResponse.Fail<Quantity>(
                $"Scale unit '{scaleUnit}' cannot be converted to product unit of measure '{productUnit}'.",
                "INCOMPATIBLE_UOM"));
        }

        try
        {
            var qty = Quantity.From(convertedWeight, productUnit);
            return Task.FromResult(ApiResponse.Ok(qty));
        }
        catch (Exception ex)
        {
            return Task.FromResult(ApiResponse.Fail<Quantity>(ex.Message, "QUANTITY_ERROR"));
        }
    }

    public async Task<ApiResponse<bool>> ZeroScaleAsync(Guid terminalId, Guid userId, CancellationToken cancellationToken = default)
    {
        var ok = await _scale.ZeroAsync(cancellationToken);
        if (ok)
        {
            await _auditRepo.RecordAsync("Terminal", "SCALE_ZEROED", userId.ToString(), $"[Terminal:{terminalId}] Scale zero calibrated", cancellationToken);
        }
        return ok ? ApiResponse.Ok(true) : ApiResponse.Fail<bool>("Scale zero command failed.", "SCALE_ZERO_FAILED");
    }

    public async Task<ApiResponse<bool>> TareScaleAsync(Guid terminalId, Guid userId, CancellationToken cancellationToken = default)
    {
        var ok = await _scale.TareAsync(cancellationToken);
        if (ok)
        {
            await _auditRepo.RecordAsync("Terminal", "SCALE_TARED", userId.ToString(), $"[Terminal:{terminalId}] Scale tared", cancellationToken);
        }
        return ok ? ApiResponse.Ok(true) : ApiResponse.Fail<bool>("Scale tare command failed.", "SCALE_TARE_FAILED");
    }
}
