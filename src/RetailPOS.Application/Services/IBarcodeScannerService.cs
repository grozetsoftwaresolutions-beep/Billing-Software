using RetailPOS.Application.Hardware;
using RetailPOS.Application.Persistence;
using RetailPOS.Contracts.Common;
using RetailPOS.Contracts.Hardware;

namespace RetailPOS.Application.Services;

public interface IBarcodeScannerService
{
    Task<ApiResponse<BarcodeLookupResultDto>> LookupBarcodeAsync(string barcode, CancellationToken cancellationToken = default);
    BarcodeScanClassification ClassifyInput(IReadOnlyList<(char Key, long IntervalMs)> keystrokes);
}

public sealed class BarcodeScannerService : IBarcodeScannerService
{
    private readonly IProductRepository _productRepo;
    private readonly IBarcodeScanner _scanner;

    public BarcodeScannerService(IProductRepository productRepo, IBarcodeScanner scanner)
    {
        _productRepo = productRepo;
        _scanner = scanner;
    }

    public async Task<ApiResponse<BarcodeLookupResultDto>> LookupBarcodeAsync(string barcode, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(barcode))
            return ApiResponse.Fail<BarcodeLookupResultDto>("Barcode cannot be empty.", "INVALID_BARCODE");

        var cleanBarcode = barcode.Trim();
        var product = await _productRepo.GetByBarcodeAsync(cleanBarcode, cancellationToken);
        if (product == null)
        {
            // Also check SKU
            product = await _productRepo.GetBySkuAsync(cleanBarcode, cancellationToken);
        }

        if (product == null || !product.IsActive)
        {
            return ApiResponse.Fail<BarcodeLookupResultDto>(
                $"Unknown barcode '{cleanBarcode}'. Product was not found or is inactive.",
                "UNKNOWN_BARCODE");
        }

        var result = new BarcodeLookupResultDto(
            cleanBarcode,
            true,
            product.Id,
            product.SKU,
            product.Name,
            product.SellingPrice.MinorUnits,
            product.Unit.ToString(),
            product.TaxCategoryId,
            "Product found.");

        return ApiResponse.Ok(result);
    }

    public BarcodeScanClassification ClassifyInput(IReadOnlyList<(char Key, long IntervalMs)> keystrokes)
    {
        return _scanner.ClassifyInput(keystrokes);
    }
}
