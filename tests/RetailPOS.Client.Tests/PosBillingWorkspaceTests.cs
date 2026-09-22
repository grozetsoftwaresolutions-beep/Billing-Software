using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using RetailPOS.Client.Hardware;
using RetailPOS.Client.Services;
using RetailPOS.Client.ViewModels;
using RetailPOS.Contracts.Auth;
using RetailPOS.Contracts.Catalog;
using RetailPOS.Contracts.Common;
using RetailPOS.Contracts.Hardware;
using RetailPOS.Contracts.Sales;
using RetailPOS.Domain.Enums;
using Xunit;

namespace RetailPOS.Client.Tests;

[Collection("ClientSessionTests")]
public sealed class PosBillingWorkspaceTests
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private sealed class MockPosHttpHandler : HttpMessageHandler
    {
        public Func<HttpRequestMessage, Task<HttpResponseMessage>>? Handler { get; set; }

        public List<HttpRequestMessage> CapturedRequests { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CapturedRequests.Add(request);
            if (Handler != null)
            {
                return await Handler(request);
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }
    }

    private static (PosViewModel ViewModel, MockPosHttpHandler Handler, TerminalHardwareCoordinator Coordinator) CreateTestWorkspace()
    {
        var session = ClientSessionManager.Instance;
        session.ClearSession();
        var login = new LoginResponse("tok", Guid.NewGuid(), "cashier1", "Cashier 1", RoleType.Cashier, Guid.NewGuid(), DateTime.UtcNow.AddHours(8));
        session.SetActiveSession(login);

        var handler = new MockPosHttpHandler();
        var httpClient = new HttpClient(handler);
        var apiClient = new PosApiClient(httpClient, "https://localhost:5101");
        var coordinator = new TerminalHardwareCoordinator();
        var vm = new PosViewModel(apiClient, coordinator);

        return (vm, handler, coordinator);
    }

    [Fact]
    public void InitialState_CartIsEmpty_TotalsAreZero_CheckoutDisabled()
    {
        var (vm, _, _) = CreateTestWorkspace();

        Assert.Empty(vm.CartItems);
        Assert.Equal(0, vm.ItemCount);
        Assert.Equal(0, vm.GrossSubtotalPaise);
        Assert.Equal(0, vm.FinalPayablePaise);
        Assert.False(vm.CanCheckout);
        Assert.False(vm.IsPaymentOpen);
        Assert.False(vm.IsSessionLocked);
        Assert.Null(vm.LastCompletedInvoiceNumber);
    }

    [Fact]
    public async Task SearchProducts_ReturnsResults_PopulatesSearchResults()
    {
        var (vm, handler, _) = CreateTestWorkspace();

        var products = new List<ProductPosDto>
        {
            new(Guid.NewGuid(), "SKU-SALT", "Tata Salt 1kg", "PCS", 2800, 1, "89012345", true, true),
            new(Guid.NewGuid(), "SKU-SUGAR", "Madhur Sugar 1kg", "PCS", 4500, 1, "89012346", true, true)
        };
        var pagedResult = new PagedResult<ProductPosDto>(products, 2, 1, 20);

        handler.Handler = _ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(ApiResponse.Ok(pagedResult))
        });

        await vm.SearchProductsAsync("grocery");

        Assert.Equal(2, vm.SearchResults.Count);
        Assert.True(vm.HasSearchResults);
        Assert.Equal("Tata Salt 1kg", vm.SearchResults[0].Name);
        Assert.Equal("Madhur Sugar 1kg", vm.SearchResults[1].Name);
    }

    [Fact]
    public void AddToCart_NewProduct_CreatesCartItemWithQuantityOne()
    {
        var (vm, _, _) = CreateTestWorkspace();

        var prodId = Guid.NewGuid();
        var product = new ProductPosDto(prodId, "SKU-001", "Amul Butter 500g", "PCS", 27500, 1, "8901001", true, true);

        vm.AddProductToCart(product);

        Assert.Single(vm.CartItems);
        var item = vm.CartItems[0];
        Assert.Equal(prodId, item.ProductId);
        Assert.Equal(1m, item.Quantity);
        Assert.Equal(27500, item.UnitSellingPricePaise);
        Assert.Equal(27500, item.LineGrossPaise);
        Assert.Equal(27500, vm.GrossSubtotalPaise);
        Assert.Equal(1, vm.ItemCount);
        Assert.True(vm.CanCheckout);
    }

    [Fact]
    public void AddToCart_ExistingProduct_IncrementsQuantity()
    {
        var (vm, _, _) = CreateTestWorkspace();

        var prodId = Guid.NewGuid();
        var product = new ProductPosDto(prodId, "SKU-001", "Amul Butter 500g", "PCS", 27500, 1, "8901001", true, true);

        vm.AddProductToCart(product);
        vm.AddProductToCart(product);

        Assert.Single(vm.CartItems);
        var item = vm.CartItems[0];
        Assert.Equal(2m, item.Quantity);
        Assert.Equal(55000, item.LineGrossPaise);
        Assert.Equal(55000, vm.GrossSubtotalPaise);
    }

    [Fact]
    public void UpdateQuantity_RecalculatesLineAndCartTotals()
    {
        var (vm, _, _) = CreateTestWorkspace();

        var product = new ProductPosDto(Guid.NewGuid(), "SKU-001", "Salt", "PCS", 2800, 1, "123", true, true);
        vm.AddProductToCart(product);

        var item = vm.CartItems[0];
        vm.IncrementQuantity(item);

        Assert.Equal(2m, item.Quantity);
        Assert.Equal(5600, item.LineGrossPaise);

        vm.DecrementQuantity(item);
        Assert.Equal(1m, item.Quantity);
        Assert.Equal(2800, item.LineGrossPaise);
    }

    [Fact]
    public void RemoveItem_RemovesLineFromCart()
    {
        var (vm, _, _) = CreateTestWorkspace();

        var product = new ProductPosDto(Guid.NewGuid(), "SKU-001", "Salt", "PCS", 2800, 1, "123", true, true);
        vm.AddProductToCart(product);
        Assert.Single(vm.CartItems);

        vm.RemoveItem(vm.CartItems[0]);

        Assert.Empty(vm.CartItems);
        Assert.Equal(0, vm.ItemCount);
        Assert.Equal(0, vm.GrossSubtotalPaise);
        Assert.False(vm.CanCheckout);
    }

    [Fact]
    public async Task BarcodeScanner_EventReceived_ResolvesProductAndAddsToCart()
    {
        var (vm, handler, _) = CreateTestWorkspace();

        var product = new ProductPosDto(Guid.NewGuid(), "SKU-BARCODE", "Biscuits", "PKT", 1000, 1, "8901234567890", true, true);

        handler.Handler = req =>
        {
            if (req.RequestUri!.ToString().Contains("/products/barcode/8901234567890", StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(ApiResponse.Ok(product))
                });
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        };

        await vm.ProcessScannedBarcodeAsync("8901234567890");

        Assert.Single(vm.CartItems);
        Assert.Equal("Biscuits", vm.CartItems[0].ProductName);
        Assert.Equal(1000, vm.GrossSubtotalPaise);
    }

    [Fact]
    public async Task BarcodeScanner_UnknownBarcode_SetsStatusError_DoesNotMutateCart()
    {
        var (vm, handler, _) = CreateTestWorkspace();

        handler.Handler = _ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)
        {
            Content = JsonContent.Create(ApiResponse.Fail<ProductPosDto>("Product not found.", "PRODUCT_NOT_FOUND"))
        });

        await vm.ProcessScannedBarcodeAsync("9999999999999");

        Assert.Empty(vm.CartItems);
        Assert.NotNull(vm.ErrorMessage);
        Assert.Contains("9999999999999", vm.ErrorMessage);
    }

    [Fact]
    public async Task CalculateCart_SynchronizesWithAuthoritativeServerTotals()
    {
        var (vm, handler, _) = CreateTestWorkspace();

        var product = new ProductPosDto(Guid.NewGuid(), "SKU-TEA", "Chai", "PKT", 20000, 1, "8901", true, true);
        vm.AddProductToCart(product);

        var lines = new List<SaleItemDto>
        {
            new(1, product.Id, product.Sku, product.Name, 1m, product.UnitCode, 20000, 20000, 0, 18000, 2000, 20000, new List<TaxComponentBreakdownDto>())
        };
        var calcDto = new CartCalculationDto(
            GrossSubtotalPaise: 20000,
            TotalLineDiscountsPaise: 0,
            InvoiceDiscountPaise: 0,
            TaxableSubtotalPaise: 18000,
            TotalTaxPaise: 2000,
            RoundingAdjustmentPaise: 0,
            FinalPayablePaise: 20000,
            Lines: lines,
            TaxComponents: new List<TaxComponentBreakdownDto>());

        handler.Handler = req =>
        {
            if (req.RequestUri!.ToString().Contains("/sales/calculate", StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(ApiResponse.Ok(calcDto))
                });
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        };

        await vm.TriggerCartCalculationAsync();

        Assert.Equal(20000, vm.GrossSubtotalPaise);
        Assert.Equal(18000, vm.TaxableSubtotalPaise);
        Assert.Equal(2000, vm.TotalTaxPaise);
        Assert.Equal(20000, vm.FinalPayablePaise);
    }

    [Fact]
    public void PaymentMethodChanged_TriggersAuthoritativeRecalculation_WithEstimatedPaymentMethod()
    {
        var (vm, _, _) = CreateTestWorkspace();

        vm.SelectPaymentMethod("Card");
        Assert.Equal(PaymentMethod.Card, vm.SelectedPaymentMethod);
        Assert.True(vm.IsCardSelected);
        Assert.False(vm.IsCashSelected);

        vm.SelectPaymentMethod("Cash");
        Assert.Equal(PaymentMethod.Cash, vm.SelectedPaymentMethod);
        Assert.True(vm.IsCashSelected);
        Assert.False(vm.IsCardSelected);
    }

    [Fact]
    public void CashPayment_CalculatesExactChangeDue_InIntegerPaise()
    {
        var (vm, _, _) = CreateTestWorkspace();

        vm.FinalPayablePaise = 46300; // ₹463.00
        vm.TenderedAmountRupees = "500";

        Assert.Equal(3700, vm.ChangeDuePaise); // ₹37.00
        Assert.Contains("37.00", vm.FormattedChangeDue);

        // Tendered less than payable
        vm.TenderedAmountRupees = "400";
        Assert.Equal(0, vm.ChangeDuePaise);
    }

    [Fact]
    public async Task Checkout_GeneratesUniqueClientTransactionId_CallsCreateSale()
    {
        var (vm, handler, _) = CreateTestWorkspace();

        var product = new ProductPosDto(Guid.NewGuid(), "SKU-1", "Rice", "KG", 6000, 1, "890", true, true);
        vm.AddProductToCart(product);
        vm.FinalPayablePaise = 6000;
        vm.TenderedAmountRupees = "100";

        var invoiceDto = new SaleInvoiceDto(
            Id: Guid.NewGuid(),
            InvoiceNumber: "INV-20260922-0001",
            ClientTransactionId: Guid.NewGuid(),
            TerminalId: Guid.NewGuid(),
            CashierId: Guid.NewGuid(),
            CashierName: "cashier1",
            CustomerId: null,
            Status: SaleStatus.Completed,
            CreatedAtUtc: DateTime.UtcNow,
            GrossSubtotalPaise: 6000,
            TotalLineDiscountsPaise: 0,
            InvoiceDiscountPaise: 0,
            TaxableSubtotalPaise: 6000,
            TotalTaxPaise: 0,
            RoundingAdjustmentPaise: 0,
            FinalPayablePaise: 6000,
            Items: new List<SaleItemDto>(),
            Payments: new List<PaymentRecordDto>());

        CreateSaleRequest? capturedRequest = null;
        handler.Handler = async req =>
        {
            if (req.Method == HttpMethod.Post && req.RequestUri!.ToString().EndsWith("/sales", StringComparison.OrdinalIgnoreCase))
            {
                capturedRequest = await req.Content!.ReadFromJsonAsync<CreateSaleRequest>(JsonOptions);
                return new HttpResponseMessage(HttpStatusCode.Created)
                {
                    Content = JsonContent.Create(ApiResponse.Ok(invoiceDto))
                };
            }
            return new HttpResponseMessage(HttpStatusCode.OK);
        };

        await vm.ExecuteCheckoutAsync();

        Assert.NotNull(capturedRequest);
        Assert.NotEqual(Guid.Empty, capturedRequest.ClientTransactionId);
        Assert.Equal("INV-20260922-0001", vm.LastCompletedInvoiceNumber);
        Assert.True(vm.IsCheckoutSuccess);
        Assert.Empty(vm.CartItems); // Cart resets on success
    }

    [Fact]
    public async Task Checkout_InsufficientStock_409Conflict_DisplaysErrorAndPreservesCart()
    {
        var (vm, handler, _) = CreateTestWorkspace();

        var product = new ProductPosDto(Guid.NewGuid(), "SKU-1", "Rice", "KG", 6000, 1, "890", true, true);
        vm.AddProductToCart(product);
        vm.FinalPayablePaise = 6000;
        vm.TenderedAmountRupees = "100";

        handler.Handler = _ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Conflict)
        {
            Content = JsonContent.Create(ApiResponse.Fail<SaleInvoiceDto>("Insufficient stock.", "INSUFFICIENT_STOCK"))
        });

        await vm.ExecuteCheckoutAsync();

        Assert.False(vm.IsCheckoutSuccess);
        Assert.NotNull(vm.ErrorMessage);
        Assert.Contains("Insufficient stock", vm.ErrorMessage);
        Assert.Single(vm.CartItems); // Preserved intact
    }

    [Fact]
    public void MoneyFormatting_NoDoubleFloatTypes_FormatsPaiseToRupee()
    {
        var item = new CartItemViewModel(Guid.NewGuid(), "SKU", "Test", "PCS", unitSellingPricePaise: 2800);
        item.LineTotalPaise = 125050; // ₹1,250.50

        Assert.Contains("28.00", item.FormattedUnitPrice);
        Assert.Contains("1,250.50", item.FormattedLineTotal);
    }

    [Fact]
    public void SessionLock_PreservesCartState()
    {
        var (vm, _, _) = CreateTestWorkspace();

        var login = new LoginResponse("tok", Guid.NewGuid(), "c1", "Cashier 1", RoleType.Cashier, Guid.NewGuid(), DateTime.UtcNow.AddHours(1));
        ClientSessionManager.Instance.SetActiveSession(login);

        var product = new ProductPosDto(Guid.NewGuid(), "SKU", "Item", "PCS", 5000, 1, "123", true, true);
        vm.AddProductToCart(product);
        Assert.Single(vm.CartItems);

        // Lock session
        ClientSessionManager.Instance.TransitionToLocked();

        Assert.True(vm.IsSessionLocked);
        Assert.Single(vm.CartItems); // Invariant: Cart is PRESERVED on lock!
        Assert.Equal(5000, vm.GrossSubtotalPaise);

        // Unlock session
        ClientSessionManager.Instance.TransitionToActive();

        Assert.False(vm.IsSessionLocked);
        Assert.Single(vm.CartItems); // Cart remains intact upon unlock!
    }

    [Fact]
    public void SessionLogout_ClearsCartState()
    {
        var (vm, _, _) = CreateTestWorkspace();

        var product = new ProductPosDto(Guid.NewGuid(), "SKU", "Item", "PCS", 5000, 1, "123", true, true);
        vm.AddProductToCart(product);
        Assert.Single(vm.CartItems);

        // Logout session
        ClientSessionManager.Instance.ClearSession();

        Assert.Empty(vm.CartItems); // Cart is CLEARED on logout
        Assert.Equal(0, vm.GrossSubtotalPaise);
        Assert.False(vm.CanCheckout);
    }

    [Fact]
    public void SessionExpiration_ClearsCartState()
    {
        var (vm, _, _) = CreateTestWorkspace();

        var product = new ProductPosDto(Guid.NewGuid(), "SKU", "Item", "PCS", 5000, 1, "123", true, true);
        vm.AddProductToCart(product);
        Assert.Single(vm.CartItems);

        // Expire session
        ClientSessionManager.Instance.ClearSession(ClientSessionState.Expired);

        Assert.Empty(vm.CartItems); // Cart is CLEARED on expiration
        Assert.Equal(0, vm.GrossSubtotalPaise);
        Assert.False(vm.CanCheckout);
    }
}
