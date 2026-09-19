using RetailPOS.Contracts.Common;
using RetailPOS.Contracts.Audit;
using RetailPOS.Domain.Entities.Audit;
using RetailPOS.Application.Persistence;
using RetailPOS.Application.Security;
using RetailPOS.Domain.Entities.Catalog;
using RetailPOS.Domain.Entities.Configuration;
using RetailPOS.Domain.Entities.Inventory;
using RetailPOS.Domain.Entities.Taxes;
using RetailPOS.Domain.Entities.Terminals;
using RetailPOS.Domain.Entities.Users;
using RetailPOS.Domain.Enums;

namespace RetailPOS.Application.Tests.Fakes;

public sealed class FakeTransactionScope : IDbTransactionScope
{
    public Task CommitAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task RollbackAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

public sealed class FakeUnitOfWork : IUnitOfWork
{
    public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default) => Task.FromResult(1);
    public Task<IDbTransactionScope> BeginSerializableTransactionAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IDbTransactionScope>(new FakeTransactionScope());
    public bool IsTransient(Exception ex) => false;
    public void ResetChangeTracker() { }
}

public sealed class FakeUserRepository : IUserRepository
{
    public List<User> Users { get; } = [];

    public Task<User?> GetByUsernameAsync(string username, CancellationToken cancellationToken = default)
    {
        var normalized = username.Trim().ToLowerInvariant();
        return Task.FromResult(Users.FirstOrDefault(u => u.Username == normalized));
    }

    public Task<User?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        Task.FromResult(Users.FirstOrDefault(u => u.Id == id));

    public Task<IReadOnlyList<User>> GetAllAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<User>>(Users);

    public Task<bool> HasAnyOwnerAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(Users.Any(u => u.Role == RoleType.Owner && u.IsActive));

    public Task AddAsync(User user, CancellationToken cancellationToken = default)
    {
        Users.Add(user);
        return Task.CompletedTask;
    }
}

public sealed class FakeTerminalRepository : ITerminalRepository
{
    public List<Terminal> Terminals { get; } = [];

    public Task<Terminal?> GetByCodeAsync(string terminalCode, CancellationToken cancellationToken = default)
    {
        var normalized = terminalCode.Trim().ToUpperInvariant();
        return Task.FromResult(Terminals.FirstOrDefault(t => t.TerminalCode == normalized));
    }

    public Task<Terminal?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        Task.FromResult(Terminals.FirstOrDefault(t => t.Id == id));

    public Task<IReadOnlyList<Terminal>> GetAllAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<Terminal>>(Terminals);

    public Task AddAsync(Terminal terminal, CancellationToken cancellationToken = default)
    {
        Terminals.Add(terminal);
        return Task.CompletedTask;
    }
}

public sealed class FakeAuditLogRepository : IAuditLogRepository
{
    public List<(string Entity, string Action, string User, string Details)> Logs { get; } = [];

    public Task RecordAsync(string entityName, string action, string performedBy, string details, CancellationToken cancellationToken = default)
    {
        Logs.Add((entityName, action, performedBy, details));
        return Task.CompletedTask;
    }

    public Task RecordAsync(AuditLog log, CancellationToken cancellationToken = default)
    {
        Logs.Add((log.EntityName, log.Action, log.UserName ?? string.Empty, log.Details ?? string.Empty));
        return Task.CompletedTask;
    }

    public Task<PagedResult<AuditLog>> GetAuditLogsAsync(AuditLogQueryFilter filter, CancellationToken cancellationToken = default) =>
        Task.FromResult(new PagedResult<AuditLog>([], 0, 1, 50));

    public Task<IReadOnlyList<AuditLog>> GetAllForExportAsync(AuditLogQueryFilter filter, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<AuditLog>>([]);

    public Task<AuditLog?> GetFailsafeClearAuditAsync(Guid failsafeInstanceId, Guid clearOperationId, CancellationToken cancellationToken = default) =>
        Task.FromResult<AuditLog?>(null);
}

public sealed class FakeStoreRepository : IStoreRepository
{
    public Store? CurrentStore { get; set; }

    public Task<Store?> GetStoreAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(CurrentStore);

    public Task UpdateStoreNameAsync(string storeName, CancellationToken cancellationToken = default)
    {
        if (CurrentStore == null)
            CurrentStore = new Store(1, storeName);
        else
            CurrentStore.UpdateStoreName(storeName);

        return Task.CompletedTask;
    }

    public Task UpdateStoreSettingsAsync(
        string? storeName,
        string? address,
        string? phone,
        string? taxRegistrationNumber,
        string? receiptFooterMessage,
        bool? cashRoundingEnabled,
        string? timeZoneId,
        CancellationToken cancellationToken = default)
    {
        if (CurrentStore == null)
        {
            CurrentStore = new Store(1, storeName ?? "Default Store", address, phone, taxRegistrationNumber, receiptFooterMessage, cashRoundingEnabled: cashRoundingEnabled ?? true, timeZoneId: timeZoneId ?? "India Standard Time");
        }
        else
        {
            if (!string.IsNullOrWhiteSpace(storeName))
                CurrentStore.UpdateStoreName(storeName);
            CurrentStore.UpdateDetails(address, phone, taxRegistrationNumber, receiptFooterMessage);
            if (cashRoundingEnabled.HasValue || !string.IsNullOrWhiteSpace(timeZoneId))
            {
                CurrentStore.UpdateBillingSettings(cashRoundingEnabled ?? CurrentStore.CashRoundingEnabled, timeZoneId);
            }
        }

        return Task.CompletedTask;
    }

    public Task InitializeStoreSecurityAsync(Guid storeGuid, string storeName, string validatorHash, string wrappedKey, CancellationToken cancellationToken = default)
    {
        if (CurrentStore == null)
        {
            CurrentStore = new Store(1, storeName);
        }
        CurrentStore.InitializeSecurityRoot(storeGuid, validatorHash, wrappedKey);
        return Task.CompletedTask;
    }
}

public sealed class FakeSessionManager : ISessionManager
{
    public Dictionary<string, (Guid UserId, string Username, RoleType Role, Guid? TerminalId)> Sessions { get; } = [];

    public string CreateSession(Guid userId, string username, string fullName, RoleType role, Guid? terminalId, TimeSpan duration)
    {
        var token = "tok_" + Guid.NewGuid().ToString("N");
        Sessions[token] = (userId, username, role, terminalId);
        return token;
    }

    public Task<SessionValidationResult> ValidateSessionAsync(string sessionToken, CancellationToken cancellationToken = default)
    {
        if (Sessions.TryGetValue(sessionToken, out var s))
            return Task.FromResult(new SessionValidationResult(true, s.UserId, s.Username, s.Username, s.Role, s.TerminalId));

        return Task.FromResult(new SessionValidationResult(false, ErrorMessage: "Invalid token."));
    }

    public void RevokeSession(string sessionToken)
    {
        Sessions.Remove(sessionToken);
    }

    public void TouchSession(string sessionToken) { }
    public void LockSession(string sessionToken) { }
    public Task<bool> UnlockSessionAsync(string sessionToken, string pin, CancellationToken cancellationToken = default) => Task.FromResult(true);
    public void RevokeUserSessions(Guid userId) { }
    public void RevokeTerminalSessions(Guid terminalId) { }
}

public sealed class FakeProductRepository : IProductRepository
{
    public List<Product> Products { get; } = [];
    public HashSet<Guid> ProductsWithSales { get; } = [];
    public HashSet<Guid> ProductsWithPurchases { get; } = [];

    public Task<Product?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        Task.FromResult(Products.FirstOrDefault(p => p.Id == id));

    public Task<Product?> GetBySkuAsync(string sku, CancellationToken cancellationToken = default)
    {
        var normalized = sku.Trim().ToUpperInvariant();
        return Task.FromResult(Products.FirstOrDefault(p => p.SKU == normalized));
    }

    public Task<Product?> GetByBarcodeAsync(string barcode, CancellationToken cancellationToken = default)
    {
        var normalized = barcode.Trim();
        return Task.FromResult(Products.FirstOrDefault(p => p.Barcodes.Any(b => b.Barcode == normalized && b.IsActive)));
    }

    public Task<(IReadOnlyList<Product> Items, int TotalCount)> SearchPosAsync(string query, int page, int pageSize, CancellationToken cancellationToken = default)
    {
        var q = query.Trim().ToUpperInvariant();
        var matches = Products.Where(p => p.IsActive && (p.SKU.Contains(q, StringComparison.OrdinalIgnoreCase) || p.Name.Contains(q, StringComparison.OrdinalIgnoreCase) || p.Barcodes.Any(b => b.Barcode.Value.Contains(q, StringComparison.OrdinalIgnoreCase) && b.IsActive))).ToList();
        var paged = matches.Skip((page - 1) * pageSize).Take(pageSize).ToList();
        return Task.FromResult(((IReadOnlyList<Product>)paged, matches.Count));
    }

    public Task<(IReadOnlyList<Product> Items, int TotalCount)> GetAllManagementAsync(int? categoryId, int? brandId, bool? activeOnly, int page, int pageSize, CancellationToken cancellationToken = default)
    {
        var matches = Products.AsEnumerable();
        if (categoryId.HasValue) matches = matches.Where(p => p.CategoryId == categoryId.Value);
        if (brandId.HasValue) matches = matches.Where(p => p.BrandId == brandId.Value);
        if (activeOnly.HasValue) matches = matches.Where(p => p.IsActive == activeOnly.Value);
        var list = matches.ToList();
        var paged = list.Skip((page - 1) * pageSize).Take(pageSize).ToList();
        return Task.FromResult(((IReadOnlyList<Product>)paged, list.Count));
    }

    public Task AddAsync(Product product, CancellationToken cancellationToken = default)
    {
        Products.Add(product);
        return Task.CompletedTask;
    }

    public Task<bool> HasBarcodeAsync(string barcode, CancellationToken cancellationToken = default)
    {
        var normalized = barcode.Trim();
        return Task.FromResult(Products.Any(p => p.Barcodes.Any(b => b.Barcode == normalized && b.IsActive)));
    }

    public Task<bool> HasSalesHistoryAsync(Guid productId, CancellationToken cancellationToken = default) =>
        Task.FromResult(ProductsWithSales.Contains(productId));

    public Task<bool> HasPurchaseHistoryAsync(Guid productId, CancellationToken cancellationToken = default) =>
        Task.FromResult(ProductsWithPurchases.Contains(productId));
}

public sealed class FakeInventoryRepository : IInventoryRepository
{
    public List<StockBalance> Balances { get; } = [];
    public List<StockMovement> Movements { get; } = [];

    public Task<StockBalance?> GetStockBalanceAsync(Guid productId, CancellationToken cancellationToken = default) =>
        Task.FromResult(Balances.FirstOrDefault(b => b.ProductId == productId));

    public Task<StockBalance?> GetStockBalanceTrackedAsync(Guid productId, CancellationToken cancellationToken = default) =>
        Task.FromResult(Balances.FirstOrDefault(b => b.ProductId == productId));

    public Task AddStockBalanceAsync(StockBalance balance, CancellationToken cancellationToken = default)
    {
        Balances.Add(balance);
        return Task.CompletedTask;
    }

    public Task<(IReadOnlyList<StockBalance> Items, int TotalCount)> GetLowStockAsync(int page, int pageSize, CancellationToken cancellationToken = default)
    {
        var matches = Balances.Where(b => b.IsLowStock).ToList();
        var paged = matches.Skip((page - 1) * pageSize).Take(pageSize).ToList();
        return Task.FromResult(((IReadOnlyList<StockBalance>)paged, matches.Count));
    }

    public Task AddStockMovementAsync(StockMovement movement, CancellationToken cancellationToken = default)
    {
        Movements.Add(movement);
        return Task.CompletedTask;
    }

    public Task<(IReadOnlyList<StockMovement> Items, int TotalCount)> GetStockMovementsAsync(Guid? productId, int page, int pageSize, CancellationToken cancellationToken = default)
    {
        var matches = Movements.AsEnumerable();
        if (productId.HasValue) matches = matches.Where(m => m.ProductId == productId.Value);
        var list = matches.OrderByDescending(m => m.CreatedAtUtc).ToList();
        var paged = list.Skip((page - 1) * pageSize).Take(pageSize).ToList();
        return Task.FromResult(((IReadOnlyList<StockMovement>)paged, list.Count));
    }

    public Task<bool> HasMovementsAsync(Guid productId, CancellationToken cancellationToken = default) =>
        Task.FromResult(Movements.Any(m => m.ProductId == productId));
}

public sealed class FakeCategoryRepository : ICategoryRepository
{
    public List<Category> Categories { get; } = [];
    public List<Brand> Brands { get; } = [];
    public HashSet<int> ValidTaxCategoryIds { get; } = [1, 2, 3, 4];

    public Task<IReadOnlyList<Category>> GetAllCategoriesAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<Category>>(Categories);

    public Task<Category?> GetCategoryByIdAsync(int id, CancellationToken cancellationToken = default) =>
        Task.FromResult(Categories.FirstOrDefault(c => c.Id == id));

    public Task<Category?> GetCategoryByNameAsync(string name, CancellationToken cancellationToken = default) =>
        Task.FromResult(Categories.FirstOrDefault(c => string.Equals(c.Name, name.Trim(), StringComparison.OrdinalIgnoreCase)));

    public Task AddCategoryAsync(Category category, CancellationToken cancellationToken = default)
    {
        Categories.Add(category);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<Brand>> GetAllBrandsAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<Brand>>(Brands);

    public Task<Brand?> GetBrandByIdAsync(int id, CancellationToken cancellationToken = default) =>
        Task.FromResult(Brands.FirstOrDefault(b => b.Id == id));

    public Task<Brand?> GetBrandByNameAsync(string name, CancellationToken cancellationToken = default) =>
        Task.FromResult(Brands.FirstOrDefault(b => string.Equals(b.Name, name.Trim(), StringComparison.OrdinalIgnoreCase)));

    public Task AddBrandAsync(Brand brand, CancellationToken cancellationToken = default)
    {
        Brands.Add(brand);
        return Task.CompletedTask;
    }

    public Task<bool> TaxCategoryExistsAsync(int taxCategoryId, CancellationToken cancellationToken = default) =>
        Task.FromResult(ValidTaxCategoryIds.Contains(taxCategoryId));

    public Task<TaxCategory?> GetTaxCategoryByIdAsync(int id, CancellationToken cancellationToken = default)
    {
        if (!ValidTaxCategoryIds.Contains(id))
            return Task.FromResult<TaxCategory?>(null);

        var tc = new TaxCategory(id, $"GST_{id}", $"GST {id * 6}%", false, [new TaxComponent("CGST", (decimal)(id * 3)), new TaxComponent("SGST", (decimal)(id * 3))]);
        return Task.FromResult<TaxCategory?>(tc);
    }
}