using System.Diagnostics.CodeAnalysis;

namespace RetailPOS.Domain.Enums;

[SuppressMessage("Naming", "CA1711:Identifiers should not have incorrect suffix", Justification = "Canonical domain permission enum name")]
public enum Permission
{
    ProcessSale = 1,
    SearchSales = 2,
    ReprintReceipt = 3,
    VoidSale = 4,
    RefundSale = 5,
    PriceOverride = 6,
    ElevatedDiscount = 7,
    SupervisorOverride = 8,
    ViewStock = 9,
    AdjustStock = 10,
    ManageCatalog = 11,
    ViewPurchases = 12,
    CreatePurchase = 13,
    CancelPurchase = 14,
    ViewSuppliers = 15,
    ManageSuppliers = 16,
    ViewCustomers = 17,
    ManageCustomers = 18,
    OpenCashSession = 19,
    CloseCashSession = 20,
    ViewCashSessions = 21,
    ViewReports = 22,
    ExportReports = 23,
    ManageUsers = 24,
    ChangeUserRole = 25,
    ResetUserCredentials = 26,
    ManageStoreSettings = 27,
    ManageTerminals = 28,
    CreateBackup = 29,
    RestoreDatabase = 30,
    ViewAuditLogs = 31,
    ExportAuditLogs = 32
}
