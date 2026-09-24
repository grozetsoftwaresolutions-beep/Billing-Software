using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using RetailPOS.Client.Services;
using RetailPOS.Client.ViewModels;
using RetailPOS.Contracts.Auth;
using RetailPOS.Contracts.Catalog;
using RetailPOS.Contracts.Common;
using RetailPOS.Contracts.Inventory;
using RetailPOS.Domain.Enums;
using Xunit;

namespace RetailPOS.Client.Tests;

[Collection("ClientSessionTests")]
public sealed class ProductsManagementWorkspaceTests
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private sealed class MockHttpHandler : HttpMessageHandler
    {
        public Func<HttpRequestMessage, Task<HttpResponseMessage>>? Handler { get; set; }
        public List<HttpRequestMessage> CapturedRequests { get; } = [];

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

    private static void SetupAuthenticatedSession(RoleType role = RoleType.Manager)
    {
        var session = ClientSessionManager.Instance;
        session.ClearSession();
        var login = new LoginResponse(
            "test-mgmt-token-xyz",
            Guid.NewGuid(),
            "mgr1",
            "Manager Test",
            role,
            Guid.NewGuid(),
            DateTime.UtcNow.AddHours(8));
        session.SetActiveSession(login);
    }

    // =========================================================================
    // 1. PRODUCT EDIT VIEWMODEL VALIDATION TESTS
    // =========================================================================

    [Theory]
    [InlineData("SKU-001", true)]
    [InlineData("COFFEE_1KG", true)]
    [InlineData("ABC-123_XYZ", true)]
    [InlineData("MILK", true)]
    [InlineData("sku-lowercase", false)] // Uppercase only in server regex
    [InlineData("SKU WITH SPACE", false)]
    [InlineData("SKU@SPECIAL!", false)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    public void ProductEditViewModel_SkuValidation_EnforcesAuthoritativeRegex(string sku, bool expectedValid)
    {
        var vm = new ProductEditViewModel();
        vm.InitializeForCreate();
        vm.Sku = sku;
        vm.Name = "Test Product";
        vm.SelectedUnitCode = "PC";
        vm.CostPriceText = "10.00";
        vm.SellingPriceText = "15.00";
        vm.ReorderLevelText = "5";
        vm.InitialStockText = "10";

        var isValid = vm.Validate(out var error);

        Assert.Equal(expectedValid, isValid);
        if (!expectedValid)
        {
            Assert.NotEmpty(error);
        }
    }

    [Fact]
    public void ProductEditViewModel_SkuLength_Exceeding50Characters_IsRejected()
    {
        var vm = new ProductEditViewModel();
        vm.InitializeForCreate();
        vm.Sku = new string('A', 51);
        vm.Name = "Test Product";
        vm.SelectedUnitCode = "PC";
        vm.CostPriceText = "10.00";
        vm.SellingPriceText = "15.00";
        vm.ReorderLevelText = "5";
        vm.InitialStockText = "10";

        var isValid = vm.Validate(out var error);

        Assert.False(isValid);
        Assert.Contains("50 characters", error);
    }

    [Theory]
    [InlineData("PC", "10", true)]
    [InlineData("PC", "10.5", false)]
    [InlineData("G", "250", true)]
    [InlineData("G", "250.25", false)]
    [InlineData("ML", "500", true)]
    [InlineData("ML", "500.1", false)]
    [InlineData("KG", "2.125", true)]
    [InlineData("KG", "2.1256", false)] // Max 3 decimals
    [InlineData("KG", "15", true)]
    [InlineData("L", "1.25", true)]
    [InlineData("L", "1.250", true)]
    [InlineData("L", "1.2505", false)] // Max 3 decimals
    public void ProductEditViewModel_UnitPrecision_ValidatesAccordingToUnitRules(string unitCode, string qtyText, bool expectedValid)
    {
        var vm = new ProductEditViewModel();
        vm.InitializeForCreate();
        vm.Sku = "VALID-SKU";
        vm.Name = "Precision Test Item";
        vm.SelectedUnitCode = unitCode;
        vm.CostPriceText = "5.00";
        vm.SellingPriceText = "10.00";
        vm.ReorderLevelText = qtyText;
        vm.InitialStockText = qtyText;

        var isValid = vm.Validate(out var error);

        Assert.Equal(expectedValid, isValid);
        if (!expectedValid)
        {
            Assert.Contains("precision", error, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void ProductEditViewModel_DecimalPriceConversion_ConvertsToPaiseAccurately()
    {
        var vm = new ProductEditViewModel();
        vm.InitializeForCreate();
        vm.Sku = "PRICING-TEST";
        vm.Name = "Pricing Test Item";
        vm.SelectedUnitCode = "PC";
        vm.CostPriceText = "19.99";
        vm.SellingPriceText = "49.50";
        vm.ReorderLevelText = "5";
        vm.InitialStockText = "10";

        var isValid = vm.Validate(out var error);
        Assert.True(isValid, error);

        var req = vm.ToCreateRequest();
        Assert.Equal(1999L, req.CostPricePaise);
        Assert.Equal(4950L, req.SellingPricePaise);
    }

    [Fact]
    public void ProductEditViewModel_NegativeOrMalformedPrice_IsRejected()
    {
        var vm = new ProductEditViewModel();
        vm.InitializeForCreate();
        vm.Sku = "INVALID-PRICE";
        vm.Name = "Bad Price Item";
        vm.SelectedUnitCode = "PC";
        vm.CostPriceText = "-5.00";
        vm.SellingPriceText = "10.00";

        var isValid = vm.Validate(out var error);
        Assert.False(isValid);
        Assert.Contains("negative", error, StringComparison.OrdinalIgnoreCase);

        vm.CostPriceText = "10.005"; // More than 2 decimals
        isValid = vm.Validate(out error);
        Assert.False(isValid);
        Assert.Contains("2 decimal places", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ProductEditViewModel_UnitImmutabilityLock_LocksWhenStockExists()
    {
        var vm = new ProductEditViewModel();
        var dtoWithStock = new ProductManagementDto(
            Guid.NewGuid(), "EXISTING-SKU", "Item With Stock", 1, "Category 1", 1, "Brand 1", "KG",
            1000, 2000, 1, true, null, [], CurrentStock: 15.5m, ReorderLevel: 5m, IsLowStock: false);

        vm.InitializeForEdit(dtoWithStock);

        Assert.False(vm.IsCreateMode);
        Assert.False(vm.IsUnitEditable);
        Assert.NotEmpty(vm.UnitImmutabilityReason);
        Assert.Equal("KG", vm.SelectedUnitCode);

        // When stock is 0, unit is editable
        var dtoZeroStock = new ProductManagementDto(
            Guid.NewGuid(), "ZERO-STOCK-SKU", "Item Without Stock", null, null, null, null, "PC",
            500, 1000, 1, true, null, [], CurrentStock: 0m, ReorderLevel: 2m, IsLowStock: false);

        vm.InitializeForEdit(dtoZeroStock);
        Assert.True(vm.IsUnitEditable);
        Assert.Empty(vm.UnitImmutabilityReason);
    }

    // =========================================================================
    // 2. PRESENTATION FORMATTING TESTS
    // =========================================================================

    [Theory]
    [InlineData(15.000, "PC", "15 PC")]
    [InlineData(100.000, "G", "100 G")]
    [InlineData(50.000, "ML", "50 ML")]
    [InlineData(15.125, "KG", "15.125 KG")]
    [InlineData(1.000, "KG", "1 KG")]
    [InlineData(1.250, "KG", "1.25 KG")]
    [InlineData(1.250, "L", "1.25 L")] // STRICT: Never 1.250 L
    [InlineData(1.000, "L", "1 L")]
    [InlineData(2.500, "L", "2.5 L")]
    public void ProductItemViewModel_FormatQuantity_OmitsUnnecessaryTrailingZeroes(decimal qty, string unit, string expected)
    {
        var result = ProductItemViewModel.FormatQuantity(qty, unit);
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData(2800L, "₹28.00")]
    [InlineData(1999L, "₹19.99")]
    [InlineData(0L, "₹0.00")]
    public void ProductItemViewModel_FormatCurrency_FormatsInrProperly(long paise, string expected)
    {
        var result = ProductItemViewModel.FormatCurrency(paise);
        Assert.Equal(expected, result);
    }

    [Fact]
    public void StockMovementViewModel_FormatsQuantitiesAndTimestampsProperly()
    {
        var dto = new StockMovementDto(
            101, Guid.NewGuid(), "Amul Butter 500g", StockMovementType.Purchase,
            5.000m, 25.000m, "PC", "PO-9988", "Initial stock intake",
            DateTime.UtcNow, Guid.NewGuid(), Guid.NewGuid());

        var vm = new StockMovementViewModel(dto);

        Assert.Equal("+5 PC", vm.FormattedQuantityChanged);
        Assert.Equal("25 PC", vm.FormattedResultingQuantity);
        Assert.Equal("Purchase", vm.MovementTypeName);
        Assert.NotEmpty(vm.FormattedTimestamp);
    }

    // =========================================================================
    // 3. INVENTORY VIEWMODEL & STOCK ADJUSTMENT TESTS
    // =========================================================================

    [Fact]
    public async Task InventoryViewModel_LoadsStock_MapsAuthoritativeLowStockCount()
    {
        SetupAuthenticatedSession();

        var handler = new MockHttpHandler();
        handler.Handler = req =>
        {
            if (req.RequestUri!.PathAndQuery.Contains("/api/v1/inventory/low-stock"))
            {
                var payload = ApiResponse.Ok(new PagedResult<StockBalanceDto>(
                    [
                        new StockBalanceDto(Guid.NewGuid(), "TEA-01", "Wagh Bakri Tea", 2.0m, 5.0m, "PC", true),
                        new StockBalanceDto(Guid.NewGuid(), "OIL-01", "Sunflower Oil", 0.5m, 2.0m, "L", true)
                    ],
                    Page: 1,
                    PageSize: 20,
                    TotalCount: 42)); // 42 total low stock items in DB

                var resp = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(payload)
                };
                return Task.FromResult(resp);
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        };

        var client = new InventoryApiClient(new HttpClient(handler), "https://localhost:5101");
        var vm = new InventoryViewModel(client);

        await vm.LoadStockAsync();

        Assert.Equal(42, vm.AuthoritativeLowStockCount);
        Assert.Equal(2, vm.StockItems.Count);
        Assert.False(vm.IsLoading);
        Assert.Empty(vm.ErrorMessage);
    }

    [Fact]
    public void InventoryViewModel_StockAdjustment_ProjectedStockCalculation_AndNegativeGuard()
    {
        var client = new InventoryApiClient(new HttpClient(new MockHttpHandler()), "https://localhost:5101");
        var vm = new InventoryViewModel(client);

        var item = new StockBalanceDto(Guid.NewGuid(), "RICE-BASMATI", "Basmati Rice 5kg", 10.000m, 2.000m, "KG", false);
        vm.PrepareAdjustment(item);

        Assert.Equal("10 KG", vm.FormattedCurrentStock);
        Assert.Equal("1", vm.AdjustmentQuantityText);
        Assert.Equal(ManualStockAdjustmentType.Increase, vm.SelectedAdjustmentType);

        // Increase by 1 -> Projected 11 KG
        Assert.Equal("11 KG", vm.FormattedProjectedStock);

        // Switch to Decrease
        vm.SelectedAdjustmentType = ManualStockAdjustmentType.Decrease;
        vm.AdjustmentQuantityText = "2.5";
        Assert.Equal("7.5 KG", vm.FormattedProjectedStock);

        // Attempt Decrease exceeding current stock (10 KG - 15 KG < 0)
        vm.AdjustmentQuantityText = "15";
        vm.AdjustmentReasonText = "Spoilage";
        var isValid = vm.ValidateAdjustment(out var error);

        Assert.False(isValid);
        Assert.Contains("Negative stock policy: Disallowed", error);
    }

    [Fact]
    public void InventoryViewModel_StockAdjustment_ReasonValidation()
    {
        var client = new InventoryApiClient(new HttpClient(new MockHttpHandler()), "https://localhost:5101");
        var vm = new InventoryViewModel(client);

        var item = new StockBalanceDto(Guid.NewGuid(), "BISCUIT-01", "Parle-G", 50m, 10m, "PC", false);
        vm.PrepareAdjustment(item);

        // Empty reason rejected
        vm.AdjustmentQuantityText = "5";
        vm.AdjustmentReasonText = "";
        var isValid = vm.ValidateAdjustment(out var error);
        Assert.False(isValid);
        Assert.Contains("mandatory", error, StringComparison.OrdinalIgnoreCase);

        // Reason > 250 characters rejected
        vm.AdjustmentReasonText = new string('x', 251);
        isValid = vm.ValidateAdjustment(out error);
        Assert.False(isValid);
        Assert.Contains("250 characters", error);

        // Valid reason accepted
        vm.AdjustmentReasonText = "Stock recount verification";
        isValid = vm.ValidateAdjustment(out error);
        Assert.True(isValid, error);
    }

    // =========================================================================
    // 4. API CLIENT PROTOCOL & SECURITY HEADER TESTS
    // =========================================================================

    [Fact]
    public async Task ProductApiClient_AttachesRequiredProtocolAndSecurityHeaders()
    {
        SetupAuthenticatedSession();

        var handler = new MockHttpHandler();
        handler.Handler = req =>
        {
            var paged = new PagedResult<ProductManagementDto>([], 1, 20, 0);
            var resp = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(ApiResponse.Ok(paged))
            };
            return Task.FromResult(resp);
        };

        var client = new ProductApiClient(new HttpClient(handler), "https://localhost:5101");
        var result = await client.GetProductsManagementAsync();

        Assert.True(result.Success);
        Assert.Single(handler.CapturedRequests);

        var req = handler.CapturedRequests[0];
        Assert.True(req.Headers.Contains("X-Client-Protocol-Version"));
        Assert.Equal("1", string.Join("", req.Headers.GetValues("X-Client-Protocol-Version")));
        Assert.NotNull(req.Headers.Authorization);
        Assert.Equal("Bearer test-mgmt-token-xyz", req.Headers.Authorization.ToString());
    }

    [Fact]
    public void ProductApiClient_PlainHttp_ThrowsInvalidOperationException()
    {
        Assert.Throws<InvalidOperationException>(() =>
            new ProductApiClient(new HttpClient(), "http://localhost:5100"));
    }

    [Fact]
    public void InventoryApiClient_PlainHttp_ThrowsInvalidOperationException()
    {
        Assert.Throws<InvalidOperationException>(() =>
            new InventoryApiClient(new HttpClient(), "http://localhost:5100"));
    }

    // =========================================================================
    // 5. AUTHORITATIVE SERVER RE-FETCH TESTS
    // =========================================================================

    [Fact]
    public async Task ProductsViewModel_SaveProductAsync_PerformsAuthoritativeServerRefetch()
    {
        SetupAuthenticatedSession();

        var requestsMade = new List<string>();

        var handler = new MockHttpHandler();
        handler.Handler = req =>
        {
            requestsMade.Add($"{req.Method} {req.RequestUri!.PathAndQuery}");

            if (req.Method == HttpMethod.Post && req.RequestUri.AbsolutePath == "/api/v1/products")
            {
                var createdDto = new ProductManagementDto(
                    Guid.NewGuid(), "NEW-ITEM", "Created Product", null, null, null, null, "PC",
                    1000, 2000, 1, true, null, [], CurrentStock: 0m, ReorderLevel: 5m, IsLowStock: false);

                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Created)
                {
                    Content = JsonContent.Create(ApiResponse.Ok(createdDto))
                });
            }

            if (req.Method == HttpMethod.Get && req.RequestUri.AbsolutePath == "/api/v1/products")
            {
                var paged = new PagedResult<ProductManagementDto>([], 1, 20, 0);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(ApiResponse.Ok(paged))
                });
            }

            if (req.Method == HttpMethod.Get && req.RequestUri.AbsolutePath.Contains("/api/v1/inventory/low-stock"))
            {
                var paged = new PagedResult<StockBalanceDto>([], 1, 20, 0);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(ApiResponse.Ok(paged))
                });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        };

        var prodClient = new ProductApiClient(new HttpClient(handler), "https://localhost:5101");
        var invClient = new InventoryApiClient(new HttpClient(handler), "https://localhost:5101");
        var vm = new ProductsViewModel(prodClient, invClient);

        vm.EditViewModel.InitializeForCreate();
        vm.EditViewModel.Sku = "NEW-ITEM";
        vm.EditViewModel.Name = "Created Product";
        vm.EditViewModel.SelectedUnitCode = "PC";
        vm.EditViewModel.CostPriceText = "10.00";
        vm.EditViewModel.SellingPriceText = "20.00";
        vm.EditViewModel.ReorderLevelText = "5";
        vm.EditViewModel.InitialStockText = "0";

        var saved = await vm.SaveProductAsync();

        Assert.True(saved);
        // Verify that after POST /api/v1/products, GET /api/v1/products and GET /api/v1/inventory/low-stock were called
        Assert.Contains(requestsMade, r => r.StartsWith("POST /api/v1/products", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(requestsMade, r => r.StartsWith("GET /api/v1/products", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(requestsMade, r => r.Contains("/api/v1/inventory/low-stock"));
    }

    [Fact]
    public async Task InventoryViewModel_ExecuteAdjustmentAsync_PerformsAuthoritativeServerRefetch()
    {
        SetupAuthenticatedSession();

        var requestsMade = new List<string>();

        var handler = new MockHttpHandler();
        handler.Handler = req =>
        {
            requestsMade.Add($"{req.Method} {req.RequestUri!.PathAndQuery}");

            if (req.Method == HttpMethod.Post && req.RequestUri.AbsolutePath == "/api/v1/inventory/adjustments")
            {
                var movementDto = new StockMovementDto(
                    501, Guid.NewGuid(), "Adjusted Item", StockMovementType.AdjustmentIncrease,
                    5m, 15m, "PC", "ADJ-TEST", "Recount adjustment",
                    DateTime.UtcNow, Guid.NewGuid(), null);

                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(ApiResponse.Ok(movementDto))
                });
            }

            if (req.Method == HttpMethod.Get && req.RequestUri.AbsolutePath.Contains("/api/v1/inventory/low-stock"))
            {
                var paged = new PagedResult<StockBalanceDto>([], 1, 20, 0);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(ApiResponse.Ok(paged))
                });
            }

            if (req.Method == HttpMethod.Get && req.RequestUri.AbsolutePath.Contains("/api/v1/inventory/movements"))
            {
                var paged = new PagedResult<StockMovementDto>([], 1, 20, 0);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(ApiResponse.Ok(paged))
                });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        };

        var invClient = new InventoryApiClient(new HttpClient(handler), "https://localhost:5101");
        var vm = new InventoryViewModel(invClient);

        var item = new StockBalanceDto(Guid.NewGuid(), "ADJ-SKU", "Adjusted Item", 10m, 2m, "PC", false);
        vm.PrepareAdjustment(item);
        vm.AdjustmentQuantityText = "5";
        vm.AdjustmentReasonText = "Recount adjustment";

        var executed = await vm.ExecuteAdjustmentAsync();

        Assert.True(executed);
        // Verify POST /api/v1/inventory/adjustments followed by re-fetch GETs
        Assert.Contains(requestsMade, r => r.StartsWith("POST /api/v1/inventory/adjustments", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(requestsMade, r => r.Contains("/api/v1/inventory/low-stock"));
        Assert.Contains(requestsMade, r => r.Contains("/api/v1/inventory/movements"));
    }

    [Fact]
    public void DemoProductsFlag_StrictlyWrappedInDebugPreProcessor()
    {
        var current = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
        while (current != null && !System.IO.File.Exists(System.IO.Path.Combine(current.FullName, "RetailPOS.sln")))
        {
            current = current.Parent;
        }
        Assert.NotNull(current);

        var appCs = System.IO.Path.Combine(current.FullName, "src", "RetailPOS.Client", "App.xaml.cs");
        Assert.True(System.IO.File.Exists(appCs));

        var appSource = System.IO.File.ReadAllText(appCs);
        var debugIndex = appSource.IndexOf("#if DEBUG", StringComparison.Ordinal);
        var endifIndex = appSource.IndexOf("#endif", StringComparison.Ordinal);

        Assert.True(debugIndex >= 0 && endifIndex > debugIndex);

        var debugBlock = appSource.Substring(debugIndex, endifIndex - debugIndex);
        Assert.Contains("--demo-products", debugBlock, StringComparison.OrdinalIgnoreCase);

        var beforeDebug = appSource.Substring(0, debugIndex);
        var afterDebug = appSource.Substring(endifIndex);
        Assert.DoesNotContain("--demo-products", beforeDebug, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("--demo-products", afterDebug, StringComparison.OrdinalIgnoreCase);
    }
}
