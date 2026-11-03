using System;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RetailPOS.Client.Hardware;
using RetailPOS.Client.Services;
using RetailPOS.Contracts.Catalog;
using RetailPOS.Contracts.Common;
using RetailPOS.Contracts.Hardware;
using RetailPOS.Contracts.Sales;
using RetailPOS.Domain.Enums;

namespace RetailPOS.Client.ViewModels;

public sealed partial class PosViewModel : ObservableObject, IDisposable
{
    private static readonly CultureInfo IndianCulture = CultureInfo.GetCultureInfo("en-IN");

    private readonly PosApiClient _apiClient;
    private readonly TerminalHardwareCoordinator _hardwareCoordinator;
    private readonly SynchronizationContext? _syncContext;

    // Idempotency tracking
    private Guid? _activeClientTransactionId;
    private string? _lastPayloadHash;

    [ObservableProperty]
    public partial string SearchQuery { get; set; } = string.Empty;

    [ObservableProperty]
    public partial ObservableCollection<ProductSearchResultViewModel> SearchResults { get; set; } = new();

    [ObservableProperty]
    public partial ProductSearchResultViewModel? SelectedSearchResult { get; set; }

    [ObservableProperty]
    public partial bool HasSearchResults { get; set; }

    [ObservableProperty]
    public partial ObservableCollection<CartItemViewModel> CartItems { get; set; } = new();

    [ObservableProperty]
    public partial CartItemViewModel? SelectedCartItem { get; set; }

    // Bill Summary
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FormattedGrossSubtotal))]
    public partial long GrossSubtotalPaise { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FormattedTotalDiscounts))]
    public partial long TotalDiscountsPaise { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FormattedTaxableSubtotal))]
    public partial long TaxableSubtotalPaise { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FormattedTotalTax))]
    public partial long TotalTaxPaise { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FormattedRounding))]
    public partial long RoundingAdjustmentPaise { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FormattedFinalPayable))]
    [NotifyPropertyChangedFor(nameof(CanCheckout))]
    public partial long FinalPayablePaise { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanCheckout))]
    public partial int ItemCount { get; set; }

    [ObservableProperty]
    public partial decimal TotalUnits { get; set; }

    // Formatted presentation strings
    public string FormattedGrossSubtotal => $"₹{(GrossSubtotalPaise / 100.0m).ToString("N2", IndianCulture)}";
    public string FormattedTotalDiscounts => $"₹{(TotalDiscountsPaise / 100.0m).ToString("N2", IndianCulture)}";
    public string FormattedTaxableSubtotal => $"₹{(TaxableSubtotalPaise / 100.0m).ToString("N2", IndianCulture)}";
    public string FormattedTotalTax => $"₹{(TotalTaxPaise / 100.0m).ToString("N2", IndianCulture)}";
    public string FormattedRounding => $"₹{(RoundingAdjustmentPaise / 100.0m).ToString("N2", IndianCulture)}";
    public string FormattedFinalPayable => $"₹{(FinalPayablePaise / 100.0m).ToString("N2", IndianCulture)}";

    // Payment Section
    [ObservableProperty]
    public partial bool IsPaymentOpen { get; set; }

    [ObservableProperty]
    public partial PaymentMethod SelectedPaymentMethod { get; set; } = PaymentMethod.Cash;

    [ObservableProperty]
    public partial string TenderedAmountRupees { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FormattedChangeDue))]
    public partial long ChangeDuePaise { get; set; }

    public string FormattedChangeDue => $"₹{(ChangeDuePaise / 100.0m).ToString("N2", IndianCulture)}";

    [ObservableProperty]
    public partial string CardReferenceNumber { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsCashSelected { get; set; } = true;

    [ObservableProperty]
    public partial bool IsCardSelected { get; set; }

    // Operator and Terminal Info
    [ObservableProperty]
    public partial string OperatorDisplay { get; set; } = "Not Authenticated";

    [ObservableProperty]
    public partial string TerminalDisplay { get; set; } = "Terminal: Standalone";

    // Checkout outcome
    [ObservableProperty]
    public partial string? LastCompletedInvoiceNumber { get; set; }

    [ObservableProperty]
    public partial bool IsCheckoutSuccess { get; set; }

    [ObservableProperty]
    public partial string StatusMessage { get; set; } = "Ready for scanning or product search.";

    [ObservableProperty]
    public partial string? ErrorMessage { get; set; }

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    public partial bool IsSessionLocked { get; set; }

    public bool CanCheckout => ItemCount > 0 && FinalPayablePaise >= 0 && !IsSessionLocked && !IsBusy;

    public PosViewModel(
        PosApiClient? apiClient = null,
        TerminalHardwareCoordinator? hardwareCoordinator = null)
    {
        _apiClient = apiClient ?? new PosApiClient();
        _hardwareCoordinator = hardwareCoordinator ?? new TerminalHardwareCoordinator();
        _syncContext = SynchronizationContext.Current;

        // Subscribe to session state changes
        ClientSessionManager.Instance.StateChanged += OnSessionStateChanged;
        UpdateSessionPresentation(ClientSessionManager.Instance.State);

        // Subscribe to scanner wedge events
        _hardwareCoordinator.Scanner.BarcodeScanned += OnHardwareBarcodeScanned;
    }

    private void OnSessionStateChanged(ClientSessionState state)
    {
        RunOnUi(() => UpdateSessionPresentation(state));
    }

    private void UpdateSessionPresentation(ClientSessionState state)
    {
        var session = ClientSessionManager.Instance;
        OperatorDisplay = session.IsAuthenticated
            ? $"{session.CurrentFullName ?? session.CurrentUsername} ({session.CurrentRole})"
            : "Not Authenticated";

        TerminalDisplay = session.CurrentTerminalId.HasValue
            ? $"Terminal: {session.CurrentTerminalId.Value.ToString()[..8]}..."
            : "Terminal: Ready";

        switch (state)
        {
            case ClientSessionState.Locked:
                // Rule: Freeze interaction, PRESERVE active cart in memory
                IsSessionLocked = true;
                IsPaymentOpen = false;
                StatusMessage = "Terminal is locked. Enter PIN to resume billing.";
                break;

            case ClientSessionState.Active:
                // Rule: Restore same cart
                IsSessionLocked = false;
                StatusMessage = "Ready for scanning or product search.";
                break;

            case ClientSessionState.Expired:
            case ClientSessionState.NotAuthenticated:
                // Rule: Clear cart on logout or session expiration
                IsSessionLocked = false;
                ClearCartInternal();
                ResetCheckoutState();
                StatusMessage = "Session expired or logged out.";
                break;
        }

        OnPropertyChanged(nameof(CanCheckout));
    }

    private void OnHardwareBarcodeScanned(object? sender, BarcodeScannedEventArgs e)
    {
        if (IsSessionLocked || string.IsNullOrWhiteSpace(e.Barcode))
            return;

        RunOnUi(async () =>
        {
            await ProcessScannedBarcodeAsync(e.Barcode);
        });
    }

    [RelayCommand]
    public async Task ProcessScannedBarcodeAsync(string barcode)
    {
        if (IsSessionLocked || string.IsNullOrWhiteSpace(barcode))
            return;

        IsBusy = true;
        ErrorMessage = null;
        StatusMessage = $"Resolving barcode '{barcode}'...";

        try
        {
            var result = await _apiClient.GetProductByBarcodeAsync(barcode);
            if (!result.Success || result.Data == null)
            {
                ErrorMessage = $"Barcode '{barcode}' not found in catalog.";
                StatusMessage = "Product not found.";
                return;
            }

            AddProductToCart(result.Data);
            StatusMessage = $"Added {result.Data.Name} via scanner.";
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Error resolving barcode: {ex.Message}";
            StatusMessage = "Lookup failed.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    public async Task SearchProductsAsync(string? query = null)
    {
        var searchQuery = query ?? SearchQuery;
        if (string.IsNullOrWhiteSpace(searchQuery))
        {
            SearchResults.Clear();
            HasSearchResults = false;
            StatusMessage = "Please enter product name, SKU, or barcode to search.";
            return;
        }

        IsBusy = true;
        ErrorMessage = null;
        StatusMessage = $"Searching for '{searchQuery}'...";

        try
        {
            var result = await _apiClient.SearchProductsAsync(searchQuery, 1, 20);
            SearchResults.Clear();

            if (result.Success && result.Data?.Items != null && result.Data.Items.Count > 0)
            {
                foreach (var item in result.Data.Items)
                {
                    SearchResults.Add(new ProductSearchResultViewModel(item));
                }
                HasSearchResults = true;
                StatusMessage = $"Found {result.Data.TotalCount} product(s).";

                // Fast entry: exactly 1 match adds immediately
                if (result.Data.Items.Count == 1)
                {
                    AddProductToCart(result.Data.Items[0]);
                    SearchQuery = string.Empty;
                    SearchResults.Clear();
                    HasSearchResults = false;
                }
            }
            else
            {
                HasSearchResults = false;
                StatusMessage = $"No products found matching '{searchQuery}'.";
            }
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Search failed: {ex.Message}";
            StatusMessage = "Search error.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    public void SelectSearchResult(ProductSearchResultViewModel? item)
    {
        if (item == null) return;

        var dto = new ProductPosDto(
            item.Id,
            item.Sku,
            item.Name,
            item.UnitCode,
            item.SellingPricePaise,
            item.TaxCategoryId,
            item.PrimaryBarcode,
            item.InStock,
            item.IsActive);

        AddProductToCart(dto);
        SearchQuery = string.Empty;
        SearchResults.Clear();
        HasSearchResults = false;
        SelectedSearchResult = null;
    }

    public void AddProductToCart(ProductPosDto product)
    {
        ArgumentNullException.ThrowIfNull(product);

        var existing = CartItems.FirstOrDefault(c => c.ProductId == product.Id);
        if (existing != null)
        {
            existing.Quantity += 1m;
            existing.LineGrossPaise = (long)(existing.UnitSellingPricePaise * existing.Quantity);
            existing.LineTotalPaise = existing.LineGrossPaise;
        }
        else
        {
            var newItem = new CartItemViewModel(
                product.Id,
                product.Sku,
                product.Name,
                product.UnitCode,
                product.SellingPricePaise,
                quantity: 1m,
                inStock: product.InStock);

            CartItems.Add(newItem);
        }

        UpdateCartTotalsLocally();
        _ = TriggerCartCalculationAsync();
    }

    [RelayCommand]
    public void IncrementQuantity(CartItemViewModel? item)
    {
        if (item == null || IsSessionLocked) return;

        item.Quantity += 1m;
        item.LineGrossPaise = (long)(item.UnitSellingPricePaise * item.Quantity);
        item.LineTotalPaise = item.LineGrossPaise;

        UpdateCartTotalsLocally();
        _ = TriggerCartCalculationAsync();
    }

    [RelayCommand]
    public void DecrementQuantity(CartItemViewModel? item)
    {
        if (item == null || IsSessionLocked) return;

        if (item.Quantity > 1m)
        {
            item.Quantity -= 1m;
            item.LineGrossPaise = (long)(item.UnitSellingPricePaise * item.Quantity);
            item.LineTotalPaise = item.LineGrossPaise;
            UpdateCartTotalsLocally();
            _ = TriggerCartCalculationAsync();
        }
        else
        {
            RemoveItem(item);
        }
    }

    [RelayCommand]
    public void RemoveItem(CartItemViewModel? item)
    {
        if (item == null || IsSessionLocked) return;

        CartItems.Remove(item);
        UpdateCartTotalsLocally();
        _ = TriggerCartCalculationAsync();
    }

    [RelayCommand]
    public void NewBill(bool force = false)
    {
        if (IsSessionLocked) return;

        ClearCartInternal();
        ResetCheckoutState();
        StatusMessage = "New bill started.";
    }

    private void ClearCartInternal()
    {
        CartItems.Clear();
        GrossSubtotalPaise = 0;
        TotalDiscountsPaise = 0;
        TaxableSubtotalPaise = 0;
        TotalTaxPaise = 0;
        RoundingAdjustmentPaise = 0;
        FinalPayablePaise = 0;
        ItemCount = 0;
        TotalUnits = 0;
        _activeClientTransactionId = null;
        _lastPayloadHash = null;
        OnPropertyChanged(nameof(CanCheckout));
    }

    private void ResetCheckoutState()
    {
        IsPaymentOpen = false;
        IsCheckoutSuccess = false;
        LastCompletedInvoiceNumber = null;
        TenderedAmountRupees = string.Empty;
        ChangeDuePaise = 0;
        CardReferenceNumber = string.Empty;
        ErrorMessage = null;
    }

    private void UpdateCartTotalsLocally()
    {
        ItemCount = CartItems.Count;
        TotalUnits = CartItems.Sum(c => c.Quantity);

        // Preliminary display until server calculation response returns
        GrossSubtotalPaise = CartItems.Sum(c => c.LineGrossPaise);
        FinalPayablePaise = GrossSubtotalPaise;
        OnPropertyChanged(nameof(CanCheckout));
    }

    public async Task TriggerCartCalculationAsync()
    {
        if (CartItems.Count == 0)
        {
            GrossSubtotalPaise = 0;
            TotalDiscountsPaise = 0;
            TaxableSubtotalPaise = 0;
            TotalTaxPaise = 0;
            RoundingAdjustmentPaise = 0;
            FinalPayablePaise = 0;
            return;
        }

        try
        {
            var items = CartItems.Select(c => new CartItemRequest(
                c.ProductId,
                c.Quantity,
                c.DiscountType,
                c.DiscountValue)).ToList();

            var request = new CalculateSaleRequest(
                items,
                DiscountType.None,
                0m,
                SelectedPaymentMethod);

            var calcResult = await _apiClient.CalculateCartAsync(request);
            if (calcResult.Success && calcResult.Data != null)
            {
                var data = calcResult.Data;
                GrossSubtotalPaise = data.GrossSubtotalPaise;
                TotalDiscountsPaise = data.TotalLineDiscountsPaise + data.InvoiceDiscountPaise;
                TaxableSubtotalPaise = data.TaxableSubtotalPaise;
                TotalTaxPaise = data.TotalTaxPaise;
                RoundingAdjustmentPaise = data.RoundingAdjustmentPaise;
                FinalPayablePaise = data.FinalPayablePaise;

                // Sync server line details
                if (data.Lines != null && data.Lines.Count == CartItems.Count)
                {
                    for (int i = 0; i < data.Lines.Count; i++)
                    {
                        var serverLine = data.Lines[i];
                        var localLine = CartItems[i];
                        localLine.LineGrossPaise = serverLine.GrossAmountPaise;
                        localLine.LineDiscountPaise = serverLine.DiscountAmountPaise;
                        localLine.LineTaxPaise = serverLine.TaxAmountPaise;
                        localLine.LineTotalPaise = serverLine.LineTotalPaise;
                    }
                }

                UpdateChangeDue();
            }
        }
        catch
        {
            // Transient network issue on live calculation; retain local preliminary totals
        }
    }

    [RelayCommand]
    public void SelectPaymentMethod(string method)
    {
        if (Enum.TryParse<PaymentMethod>(method, true, out var parsed))
        {
            SelectedPaymentMethod = parsed;
            IsCashSelected = parsed == PaymentMethod.Cash;
            IsCardSelected = parsed == PaymentMethod.Card;

            // Trigger authoritative recalculation because cash rounding rules vary by payment method
            _ = TriggerCartCalculationAsync();
        }
    }

    [RelayCommand]
    public void OpenPayment()
    {
        if (CartItems.Count == 0 || IsSessionLocked) return;

        IsPaymentOpen = true;
        IsCheckoutSuccess = false;
        ErrorMessage = null;
        SelectPaymentMethod("Cash");

        // Preset exact cash amount
        TenderedAmountRupees = (FinalPayablePaise / 100.0m).ToString("0.##", CultureInfo.InvariantCulture);
        UpdateChangeDue();
    }

    [RelayCommand]
    public void ClosePayment()
    {
        IsPaymentOpen = false;
    }

    [RelayCommand]
    public void SetPresetCashAmount(string amountStr)
    {
        if (amountStr.Equals("Exact", StringComparison.OrdinalIgnoreCase))
        {
            TenderedAmountRupees = (FinalPayablePaise / 100.0m).ToString("0.##", CultureInfo.InvariantCulture);
        }
        else if (decimal.TryParse(amountStr, NumberStyles.Any, CultureInfo.InvariantCulture, out var preset))
        {
            TenderedAmountRupees = preset.ToString("0.##", CultureInfo.InvariantCulture);
        }

        UpdateChangeDue();
    }

    public void UpdateChangeDue()
    {
        if (decimal.TryParse(TenderedAmountRupees, NumberStyles.Any, CultureInfo.InvariantCulture, out var tenderedRupees))
        {
            var tenderedPaise = (long)Math.Round(tenderedRupees * 100m, MidpointRounding.AwayFromZero);
            ChangeDuePaise = Math.Max(0, tenderedPaise - FinalPayablePaise);
        }
        else
        {
            ChangeDuePaise = 0;
        }
    }

    partial void OnTenderedAmountRupeesChanged(string value)
    {
        UpdateChangeDue();
    }

    [RelayCommand]
    public async Task ExecuteCheckoutAsync()
    {
        if (CartItems.Count == 0 || IsSessionLocked || IsBusy)
            return;

        // Payment validation
        long tenderedPaise = 0;
        if (SelectedPaymentMethod == PaymentMethod.Cash)
        {
            if (!decimal.TryParse(TenderedAmountRupees, NumberStyles.Any, CultureInfo.InvariantCulture, out var rupees) || rupees <= 0)
            {
                ErrorMessage = "Please enter a valid cash tendered amount.";
                return;
            }

            tenderedPaise = (long)Math.Round(rupees * 100m, MidpointRounding.AwayFromZero);
            if (tenderedPaise < FinalPayablePaise)
            {
                ErrorMessage = $"Tendered amount ₹{rupees:N2} is less than total payable {FormattedFinalPayable}.";
                return;
            }
        }

        IsBusy = true;
        ErrorMessage = null;
        StatusMessage = "Submitting authoritative checkout...";

        try
        {
            var cartRequests = CartItems.Select(c => new CartItemRequest(
                c.ProductId,
                c.Quantity,
                c.DiscountType,
                c.DiscountValue)).ToList();

            var paymentRequests = new List<PaymentLineRequest>();
            if (SelectedPaymentMethod == PaymentMethod.Cash)
            {
                paymentRequests.Add(new PaymentLineRequest(
                    PaymentMethod.Cash,
                    AmountAppliedPaise: FinalPayablePaise,
                    TenderedAmountPaise: tenderedPaise));
            }
            else
            {
                paymentRequests.Add(new PaymentLineRequest(
                    PaymentMethod.Card,
                    AmountAppliedPaise: FinalPayablePaise,
                    TenderedAmountPaise: null,
                    ReferenceNumber: string.IsNullOrWhiteSpace(CardReferenceNumber) ? null : CardReferenceNumber.Trim()));
            }

            // Generate payload signature to govern idempotency key
            var payloadSignature = $"{string.Join(";", cartRequests.Select(r => $"{r.ProductId}:{r.Quantity}:{r.DiscountValue}"))}|{SelectedPaymentMethod}:{FinalPayablePaise}";
            if (_activeClientTransactionId == null || _lastPayloadHash != payloadSignature)
            {
                // New checkout or payload changed: generate fresh transaction ID
                _activeClientTransactionId = Guid.NewGuid();
                _lastPayloadHash = payloadSignature;
            }

            var saleRequest = new CreateSaleRequest(
                ClientTransactionId: _activeClientTransactionId.Value,
                Items: cartRequests,
                Payments: paymentRequests,
                OrderDiscountType: DiscountType.None,
                OrderDiscountValue: 0m);

            var checkoutResult = await _apiClient.CreateSaleAsync(saleRequest);
            if (!checkoutResult.Success || checkoutResult.Data == null)
            {
                ErrorMessage = checkoutResult.ErrorMessage ?? "Checkout rejected by server.";
                StatusMessage = "Checkout failed.";
                return;
            }

            var invoice = checkoutResult.Data;
            LastCompletedInvoiceNumber = invoice.InvoiceNumber; // Opaque server-provided string
            IsCheckoutSuccess = true;
            IsPaymentOpen = false;
            StatusMessage = $"Sale completed! Invoice: {invoice.InvoiceNumber}";

            // Cash Drawer Triggering Rule: ONLY when authoritative Directive.ShouldKick == true
            if (invoice.DrawerKick?.ShouldKick == true)
            {
                try
                {
                    var kickResult = await _hardwareCoordinator.OpenDrawerAsync(invoice.DrawerKick.Reason ?? "Sale checkout");
                    await _apiClient.ReportDrawerKickResultAsync(new ReportDrawerKickResultRequest(
                        "Sale",
                        invoice.Id,
                        kickResult.Success,
                        kickResult.ErrorMessage));
                }
                catch
                {
                    // Best effort drawer opening; does not invalidate sale
                }
            }

            // Receipt Printing
            try
            {
                await _apiClient.PrintReceiptAsync(invoice.Id);
            }
            catch
            {
                // Thermal print failure does NOT rollback committed sale
            }

            // Reset transaction tracking and clear cart
            _activeClientTransactionId = null;
            _lastPayloadHash = null;
            CartItems.Clear();
            UpdateCartTotalsLocally();
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Checkout error: {ex.Message}. Safe to retry.";
            StatusMessage = "Network/server error during checkout.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void RunOnUi(Action action)
    {
        if (_syncContext != null && SynchronizationContext.Current != _syncContext)
        {
            _syncContext.Post(_ => action(), null);
        }
        else
        {
            action();
        }
    }

    public void Dispose()
    {
        ClientSessionManager.Instance.StateChanged -= OnSessionStateChanged;
        _hardwareCoordinator.Scanner.BarcodeScanned -= OnHardwareBarcodeScanned;
    }
}
