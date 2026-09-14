using RetailPOS.Application.Persistence;
using RetailPOS.Contracts.Common;
using RetailPOS.Contracts.Configuration;
using RetailPOS.Domain.Common;
using RetailPOS.Domain.Entities.Configuration;

namespace RetailPOS.Application.Services;

public sealed class StoreService : IStoreService
{
    private readonly IStoreRepository _storeRepo;
    private readonly IAuditLogRepository _auditRepo;
    private readonly IUnitOfWork _unitOfWork;

    public StoreService(
        IStoreRepository storeRepo,
        IAuditLogRepository auditRepo,
        IUnitOfWork unitOfWork)
    {
        _storeRepo = storeRepo;
        _auditRepo = auditRepo;
        _unitOfWork = unitOfWork;
    }

    public async Task<ApiResponse<StoreSettingsDto>> GetSettingsAsync(CancellationToken cancellationToken = default)
    {
        var store = await _storeRepo.GetStoreAsync(cancellationToken);
        if (store == null)
            return ApiResponse.Fail<StoreSettingsDto>("Store configuration not found.", "STORE_NOT_FOUND");

        var dto = new StoreSettingsDto(
            store.Id,
            store.StoreName,
            store.Address,
            store.Phone,
            store.TaxRegistrationNumber,
            store.ReceiptFooterMessage,
            store.DefaultPricingModel,
            store.NegativeStockPolicy,
            store.CashRoundingEnabled,
            store.TimeZoneId);

        return ApiResponse.Ok(dto);
    }

    public async Task<ApiResponse<StoreSettingsDto>> UpdateSettingsAsync(
        UpdateStoreSettingsRequest request,
        string executingUser,
        CancellationToken cancellationToken = default)
    {
        var store = await _storeRepo.GetStoreAsync(cancellationToken);
        if (store == null)
            return ApiResponse.Fail<StoreSettingsDto>("Store configuration not found.", "STORE_NOT_FOUND");

        if (!string.IsNullOrWhiteSpace(request.TimeZoneId))
        {
            Store.ValidateWindowsTimeZone(request.TimeZoneId);
        }

        await _storeRepo.UpdateStoreSettingsAsync(
            request.StoreName,
            request.Address,
            request.Phone,
            request.TaxRegistrationNumber,
            request.ReceiptFooterMessage,
            request.CashRoundingEnabled,
            request.TimeZoneId,
            cancellationToken);

        var details = $"Store settings updated. CashRounding: {request.CashRoundingEnabled}, TimeZone: {request.TimeZoneId}.";
        await _auditRepo.RecordAsync("Store", "STORE_CONFIG_CHANGED", executingUser, details, cancellationToken);
        await _unitOfWork.SaveChangesAsync(cancellationToken);

        var updatedStore = await _storeRepo.GetStoreAsync(cancellationToken);
        var dto = new StoreSettingsDto(
            updatedStore!.Id,
            updatedStore.StoreName,
            updatedStore.Address,
            updatedStore.Phone,
            updatedStore.TaxRegistrationNumber,
            updatedStore.ReceiptFooterMessage,
            updatedStore.DefaultPricingModel,
            updatedStore.NegativeStockPolicy,
            updatedStore.CashRoundingEnabled,
            updatedStore.TimeZoneId);

        return ApiResponse.Ok(dto);
    }
}
