using System.Collections.Frozen;
using RetailPOS.Domain.Enums;

namespace RetailPOS.Domain.Security;

/// <summary>
/// Authoritative domain evaluation engine for the canonical 32-permission matrix.
/// Exact permission counts:
/// Owner: 32 True (100%)
/// Admin: 29 True, 3 False (False: RestoreDatabase, ChangeUserRole, ManageStoreSettings)
/// Manager: 22 True, 10 False
/// Cashier: 7 True, 25 False
/// Viewer: 8 True, 24 False
/// </summary>
public static class RolePermissions
{
    private static readonly Dictionary<Permission, string> PermissionCodes = new()
    {
        [Permission.ProcessSale] = "pos:sale:create",
        [Permission.SearchSales] = "pos:sale:read",
        [Permission.ReprintReceipt] = "pos:sale:reprint",
        [Permission.VoidSale] = "pos:sale:void",
        [Permission.RefundSale] = "pos:sale:refund",
        [Permission.PriceOverride] = "pos:sale:price-override",
        [Permission.ElevatedDiscount] = "pos:sale:elevated-discount",
        [Permission.SupervisorOverride] = "security:supervisor-override",
        [Permission.ViewStock] = "inventory:stock:view",
        [Permission.AdjustStock] = "inventory:stock:adjust",
        [Permission.ManageCatalog] = "catalog:product:manage",
        [Permission.ViewPurchases] = "purchase:invoice:view",
        [Permission.CreatePurchase] = "purchase:invoice:create",
        [Permission.CancelPurchase] = "purchase:invoice:cancel",
        [Permission.ViewSuppliers] = "supplier:profile:view",
        [Permission.ManageSuppliers] = "supplier:profile:manage",
        [Permission.ViewCustomers] = "customer:profile:view",
        [Permission.ManageCustomers] = "customer:profile:manage",
        [Permission.OpenCashSession] = "cash:session:open",
        [Permission.CloseCashSession] = "cash:session:close",
        [Permission.ViewCashSessions] = "cash:session:view",
        [Permission.ViewReports] = "reports:general:view",
        [Permission.ExportReports] = "reports:general:export",
        [Permission.ManageUsers] = "admin:users:manage",
        [Permission.ChangeUserRole] = "admin:users:role",
        [Permission.ResetUserCredentials] = "admin:users:credentials",
        [Permission.ManageStoreSettings] = "admin:settings:manage",
        [Permission.ManageTerminals] = "admin:terminals:manage",
        [Permission.CreateBackup] = "admin:backup:create",
        [Permission.RestoreDatabase] = "admin:backup:restore",
        [Permission.ViewAuditLogs] = "admin:audit:view",
        [Permission.ExportAuditLogs] = "admin:audit:export"
    };

    private static readonly Dictionary<string, Permission> CodeToPermission =
        PermissionCodes.ToDictionary(kvp => kvp.Value, kvp => kvp.Key, StringComparer.OrdinalIgnoreCase);

    public static readonly IReadOnlyList<Permission> AllPermissions =
        Enum.GetValues<Permission>().OrderBy(p => (int)p).ToArray();

    private static readonly FrozenDictionary<RoleType, FrozenSet<Permission>> RolePermissionMap;

    static RolePermissions()
    {
        var map = new Dictionary<RoleType, HashSet<Permission>>();

        // Owner: All 32 permissions
        map[RoleType.Owner] = new HashSet<Permission>(AllPermissions);

        // Admin: 29 permissions (All except RestoreDatabase, ChangeUserRole, ManageStoreSettings)
        map[RoleType.Admin] = new HashSet<Permission>(AllPermissions.Where(p =>
            p != Permission.RestoreDatabase &&
            p != Permission.ChangeUserRole &&
            p != Permission.ManageStoreSettings));

        // Manager: 22 permissions
        map[RoleType.Manager] = new HashSet<Permission>(AllPermissions.Where(p =>
            p != Permission.CancelPurchase &&
            p != Permission.ManageUsers &&
            p != Permission.ChangeUserRole &&
            p != Permission.ResetUserCredentials &&
            p != Permission.ManageStoreSettings &&
            p != Permission.ManageTerminals &&
            p != Permission.CreateBackup &&
            p != Permission.RestoreDatabase &&
            p != Permission.ViewAuditLogs &&
            p != Permission.ExportAuditLogs));

        // Cashier: 7 permissions
        map[RoleType.Cashier] = new HashSet<Permission>
        {
            Permission.ProcessSale,
            Permission.SearchSales,
            Permission.ReprintReceipt,
            Permission.ViewCustomers,
            Permission.ManageCustomers,
            Permission.OpenCashSession,
            Permission.CloseCashSession
        };

        // Viewer: 8 permissions
        map[RoleType.Viewer] = new HashSet<Permission>
        {
            Permission.SearchSales,
            Permission.ViewStock,
            Permission.ViewPurchases,
            Permission.ViewSuppliers,
            Permission.ViewCustomers,
            Permission.ViewCashSessions,
            Permission.ViewReports,
            Permission.ExportReports
        };

        RolePermissionMap = map.ToDictionary(
            kvp => kvp.Key,
            kvp => kvp.Value.ToFrozenSet()
        ).ToFrozenDictionary();
    }

    public static bool HasPermission(RoleType role, Permission permission)
    {
        return RolePermissionMap.TryGetValue(role, out var perms) && perms.Contains(permission);
    }

    public static IReadOnlySet<Permission> GetPermissions(RoleType role)
    {
        return RolePermissionMap.TryGetValue(role, out var perms)
            ? perms
            : FrozenSet<Permission>.Empty;
    }

    public static string GetPermissionCode(Permission permission)
    {
        return PermissionCodes.TryGetValue(permission, out var code) ? code : permission.ToString();
    }

    public static bool TryParsePermissionCode(string code, out Permission permission)
    {
        return CodeToPermission.TryGetValue(code, out permission);
    }
}
