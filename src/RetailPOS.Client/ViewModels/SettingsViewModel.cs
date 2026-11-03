using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RetailPOS.Client.Services;
using RetailPOS.Contracts.Backup;
using RetailPOS.Contracts.Common;
using RetailPOS.Contracts.Configuration;
using RetailPOS.Contracts.Hardware;
using RetailPOS.Contracts.Security;
using RetailPOS.Contracts.Terminals;
using RetailPOS.Contracts.Users;
using RetailPOS.Domain.Enums;

namespace RetailPOS.Client.ViewModels;

public sealed partial class SettingsViewModel : ObservableObject, IDisposable
{
    private readonly PosApiClient _posApiClient;
    private CancellationTokenSource? _activeCts;

    [ObservableProperty]
    public partial bool IsLoading { get; set; }

    [ObservableProperty]
    public partial bool IsSaving { get; set; }

    [ObservableProperty]
    public partial string ErrorMessage { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string StatusMessage { get; set; } = string.Empty;

    // --- Role & Identity Context ---
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsOwner))]
    [NotifyPropertyChangedFor(nameof(IsOwnerOrAdmin))]
    [NotifyPropertyChangedFor(nameof(IsAccessDenied))]
    [NotifyPropertyChangedFor(nameof(CanEditSecuritySettings))]
    [NotifyPropertyChangedFor(nameof(CanChangeUserRoles))]
    public partial RoleType CurrentRole { get; set; } = RoleType.Cashier;

    [ObservableProperty]
    public partial Guid CurrentTerminalId { get; set; }

    [ObservableProperty]
    public partial Guid CurrentUserId { get; set; }

    [ObservableProperty]
    public partial string CurrentUsername { get; set; } = string.Empty;

    public bool IsOwner => CurrentRole == RoleType.Owner;
    public bool IsOwnerOrAdmin => CurrentRole is RoleType.Owner or RoleType.Admin;
    public bool IsAccessDenied => !IsOwnerOrAdmin;
    public bool CanEditSecuritySettings => IsOwner;
    public bool CanChangeUserRoles => IsOwner;

    // --- Workspace Tabs ---
    // 0 = Store Settings, 1 = Security & Failsafe, 2 = Hardware Config & Diagnostics,
    // 3 = Terminals Management, 4 = Staff & Roles, 5 = Backups
    [ObservableProperty]
    public partial int SelectedTabIndex { get; set; } = 0;

    // ==========================================
    // Tab 0: Store Settings
    // ==========================================
    [ObservableProperty]
    public partial string StoreName { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string? StoreAddress { get; set; }

    [ObservableProperty]
    public partial string? StorePhone { get; set; }

    [ObservableProperty]
    public partial string? TaxRegistrationNumber { get; set; }

    [ObservableProperty]
    public partial string? ReceiptFooterMessage { get; set; }

    [ObservableProperty]
    public partial bool CashRoundingEnabled { get; set; }

    [ObservableProperty]
    public partial string TimeZoneId { get; set; } = "India Standard Time";

    [ObservableProperty]
    public partial PricingModel DefaultPricingModel { get; set; }

    [ObservableProperty]
    public partial NegativeStockPolicy NegativeStockPolicy { get; set; }

    [ObservableProperty]
    public partial bool IsStoreSettingsLoaded { get; set; }

    // ==========================================
    // Tab 1: Security Settings & Failsafe Status
    // ==========================================
    [ObservableProperty]
    public partial int IdleTimeoutMinutes { get; set; } = 15;

    [ObservableProperty]
    public partial int MaxFailedPinAttempts { get; set; } = 5;

    [ObservableProperty]
    public partial int PinLockoutMinutes { get; set; } = 15;

    [ObservableProperty]
    public partial decimal MaxCashierDiscountPercentage { get; set; } = 10.00m;

    [ObservableProperty]
    public partial bool RequireSupervisorForRefund { get; set; } = true;

    [ObservableProperty]
    public partial bool RequireSupervisorForVoid { get; set; } = true;

    [ObservableProperty]
    public partial bool RequireSupervisorForPriceOverride { get; set; } = true;

    [ObservableProperty]
    public partial bool IsSecuritySettingsLoaded { get; set; }

    // Live Failsafe Telemetry
    [ObservableProperty]
    public partial string FailsafeStatus { get; set; } = "UNKNOWN";

    [ObservableProperty]
    public partial string FailsafeBadgeColor { get; set; } = "#64748B";

    [ObservableProperty]
    public partial string? FailsafeEpisodeId { get; set; }

    [ObservableProperty]
    public partial string? FailsafeEnteredAt { get; set; }

    [ObservableProperty]
    public partial string? FailsafeReason { get; set; }

    [ObservableProperty]
    public partial bool HasPendingClear { get; set; }

    [ObservableProperty]
    public partial string? PendingClearOperationId { get; set; }

    // ==========================================
    // Tab 2: Hardware Configuration & Diagnostics
    // ==========================================
    [ObservableProperty]
    public partial Guid HardwareTargetTerminalId { get; set; }

    [ObservableProperty]
    public partial PrinterConnectionType PrinterConnectionType { get; set; } = PrinterConnectionType.Simulated;

    [ObservableProperty]
    public partial string PrinterNameOrAddress { get; set; } = "Default Receipt Printer";

    [ObservableProperty]
    public partial PaperWidth PaperWidth { get; set; } = PaperWidth.Width80mm;

    [ObservableProperty]
    public partial PrinterEncoding PrinterEncoding { get; set; } = PrinterEncoding.Ascii;

    [ObservableProperty]
    public partial int PrinterCodePage { get; set; } = 437;

    [ObservableProperty]
    public partial int PrinterTimeoutMs { get; set; } = 5000;

    [ObservableProperty]
    public partial bool AutoCut { get; set; } = true;

    [ObservableProperty]
    public partial bool OpenDrawerOnCashSale { get; set; } = true;

    [ObservableProperty]
    public partial DrawerConnectionType DrawerConnectionType { get; set; } = DrawerConnectionType.PrinterDriven;

    [ObservableProperty]
    public partial int DrawerKickPin { get; set; } = 0;

    [ObservableProperty]
    public partial int DrawerPulseOnMs { get; set; } = 50;

    [ObservableProperty]
    public partial int DrawerPulseOffMs { get; set; } = 250;

    [ObservableProperty]
    public partial int ScannerMaxInterKeyMs { get; set; } = 50;

    [ObservableProperty]
    public partial ScaleConnectionType ScaleConnectionType { get; set; } = ScaleConnectionType.SerialCom;

    [ObservableProperty]
    public partial string? ScalePortName { get; set; } = "COM1";

    [ObservableProperty]
    public partial int ScaleBaudRate { get; set; } = 9600;

    [ObservableProperty]
    public partial int ScaleDataBits { get; set; } = 8;

    [ObservableProperty]
    public partial int ScaleParity { get; set; } = 0;

    [ObservableProperty]
    public partial int ScaleStopBits { get; set; } = 1;

    [ObservableProperty]
    public partial ScaleProtocol ScaleProtocol { get; set; } = ScaleProtocol.CasToledo;

    [ObservableProperty]
    public partial int ScaleTimeoutMs { get; set; } = 2000;

    [ObservableProperty]
    public partial bool IsScaleEnabled { get; set; }

    [ObservableProperty]
    public partial bool IsHardwareConfigLoaded { get; set; }

    // Diagnostics Results (FAIL-CLOSED: only explicit user actions!)
    [ObservableProperty]
    public partial bool IsTestingPrinter { get; set; }

    [ObservableProperty]
    public partial string PrinterTestStatus { get; set; } = "Not tested";

    [ObservableProperty]
    public partial bool IsTestingDrawer { get; set; }

    [ObservableProperty]
    public partial string DrawerTestStatus { get; set; } = "Not tested";

    [ObservableProperty]
    public partial bool IsPollingScale { get; set; }

    [ObservableProperty]
    public partial string ScaleWeightDisplay { get; set; } = "-- kg";

    [ObservableProperty]
    public partial string ScaleReadingDetails { get; set; } = "Scale not polled";

    [ObservableProperty]
    public partial bool IsCheckingHardwareStatus { get; set; }

    [ObservableProperty]
    public partial string HardwareStatusSummary { get; set; } = "Hardware status unverified";

    // ==========================================
    // Tab 3: Terminals Management
    // ==========================================
    public ObservableCollection<TerminalItemViewModel> Terminals { get; } = new();

    [ObservableProperty]
    public partial TerminalItemViewModel? SelectedTerminal { get; set; }

    [ObservableProperty]
    public partial string GeneratedEnrollmentToken { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string GeneratedTokenPurpose { get; set; } = string.Empty;

    [ObservableProperty]
    public partial DateTimeOffset? GeneratedTokenExpiresAt { get; set; }

    [ObservableProperty]
    public partial bool HasGeneratedToken { get; set; }

    [ObservableProperty]
    public partial string DecommissionConfirmPhrase { get; set; } = string.Empty;

    // ==========================================
    // Tab 4: Staff & Roles Management
    // ==========================================
    public ObservableCollection<UserItemViewModel> Users { get; } = new();

    [ObservableProperty]
    public partial UserItemViewModel? SelectedUser { get; set; }

    // New User Form
    [ObservableProperty]
    public partial string NewUsername { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string NewFullName { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string NewPassword { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string NewPin { get; set; } = string.Empty;

    [ObservableProperty]
    public partial RoleType NewUserRole { get; set; } = RoleType.Cashier;

    // Reset Credentials Form
    [ObservableProperty]
    public partial string ResetPasswordText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string ResetPinText { get; set; } = string.Empty;

    // ==========================================
    // Tab 5: Backups
    // ==========================================
    public ObservableCollection<BackupItemViewModel> Backups { get; } = new();

    [ObservableProperty]
    public partial string NewBackupReason { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsCreatingBackup { get; set; }

    // ==========================================
    // Constructor & Initialization
    // ==========================================
    public SettingsViewModel(PosApiClient? posApiClient = null)
    {
        _posApiClient = posApiClient ?? new PosApiClient();

        var session = ClientSessionManager.Instance;
        CurrentRole = session.CurrentRole ?? RoleType.Cashier;
        CurrentTerminalId = session.CurrentTerminalId ?? Guid.Empty;
        CurrentUserId = session.CurrentUserId ?? Guid.Empty;
        CurrentUsername = session.CurrentUsername ?? string.Empty;
        HardwareTargetTerminalId = CurrentTerminalId;
    }

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        if (IsAccessDenied)
        {
            ErrorMessage = "Access Denied: Settings & Administration requires Owner or Administrator role.";
            return;
        }

        await LoadTabAsync(SelectedTabIndex, ct);
    }

    partial void OnSelectedTabIndexChanged(int value)
    {
        _ = LoadTabAsync(value);
    }

    public async Task LoadTabAsync(int tabIndex, CancellationToken ct = default)
    {
        if (IsAccessDenied) return;

        switch (tabIndex)
        {
            case 0:
                if (!IsStoreSettingsLoaded) await LoadStoreSettingsAsync(ct);
                break;
            case 1:
                await LoadSecuritySettingsAsync(ct);
                break;
            case 2:
                if (!IsHardwareConfigLoaded) await LoadHardwareConfigAsync(null, ct);
                break;
            case 3:
                await LoadTerminalsAsync(ct);
                break;
            case 4:
                await LoadUsersAsync(ct);
                break;
            case 5:
                await LoadBackupsAsync(ct);
                break;
        }
    }

    // ==========================================
    // Tab 0 Operations: Store Settings
    // ==========================================
    public async Task LoadStoreSettingsAsync(CancellationToken ct = default)
    {
        IsLoading = true;
        ErrorMessage = string.Empty;
        try
        {
            var res = await _posApiClient.GetStoreSettingsAsync(token: null, ct: ct);
            if (res.Success && res.Data != null)
            {
                var s = res.Data;
                StoreName = s.StoreName ?? string.Empty;
                StoreAddress = s.Address;
                StorePhone = s.Phone;
                TaxRegistrationNumber = s.TaxRegistrationNumber;
                ReceiptFooterMessage = s.ReceiptFooterMessage;
                CashRoundingEnabled = s.CashRoundingEnabled;
                TimeZoneId = s.TimeZoneId ?? "India Standard Time";
                DefaultPricingModel = s.DefaultPricingModel;
                NegativeStockPolicy = s.NegativeStockPolicy;
                IsStoreSettingsLoaded = true;
                StatusMessage = "Store settings loaded.";
            }
            else
            {
                ErrorMessage = res.ErrorMessage ?? "Failed to load store settings.";
            }
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Error loading store settings: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    public async Task<bool> SaveStoreSettingsAsync(CancellationToken ct = default)
    {
        if (!IsStoreSettingsLoaded)
        {
            ErrorMessage = "Store settings must be loaded before saving.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(StoreName))
        {
            ErrorMessage = "Store Name is required.";
            return false;
        }

        IsSaving = true;
        ErrorMessage = string.Empty;
        try
        {
            var req = new UpdateStoreSettingsRequest(
                StoreName: StoreName.Trim(),
                Address: StoreAddress?.Trim(),
                Phone: StorePhone?.Trim(),
                TaxRegistrationNumber: TaxRegistrationNumber?.Trim(),
                ReceiptFooterMessage: ReceiptFooterMessage?.Trim(),
                CashRoundingEnabled: CashRoundingEnabled,
                TimeZoneId: TimeZoneId?.Trim());

            var res = await _posApiClient.UpdateStoreSettingsAsync(req, token: null, ct: ct);
            if (res.Success)
            {
                StatusMessage = "Store settings saved successfully.";
                return true;
            }
            else
            {
                ErrorMessage = res.ErrorMessage ?? "Failed to save store settings.";
                return false;
            }
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Error saving store settings: {ex.Message}";
            return false;
        }
        finally
        {
            IsSaving = false;
        }
    }

    // ==========================================
    // Tab 1 Operations: Security Settings & Failsafe
    // ==========================================
    public async Task LoadSecuritySettingsAsync(CancellationToken ct = default)
    {
        if (!IsOwner)
        {
            // Admin role is forbidden from calling GET /api/v1/settings/security (403)
            // Neutral slate grey status badge, never fabricated green
            FailsafeStatus = "STATUS UNAVAILABLE TO THIS ROLE";
            FailsafeBadgeColor = "#64748B";
            FailsafeReason = "Security settings and live failsafe telemetry require Owner role credentials.";
            FailsafeEpisodeId = null;
            FailsafeEnteredAt = null;
            HasPendingClear = false;
            PendingClearOperationId = null;
            IsSecuritySettingsLoaded = false;
            return;
        }

        IsLoading = true;
        ErrorMessage = string.Empty;
        try
        {
            var res = await _posApiClient.GetSecuritySettingsAsync(token: null, ct: ct);
            if (res.Success && res.Data != null)
            {
                var envelope = res.Data;
                var sec = envelope.Settings;
                if (sec != null)
                {
                    IdleTimeoutMinutes = sec.IdleTimeoutMinutes;
                    MaxFailedPinAttempts = sec.MaxFailedPinAttempts;
                    PinLockoutMinutes = sec.PinLockoutMinutes;
                    MaxCashierDiscountPercentage = sec.MaxCashierDiscountPercentage;
                    RequireSupervisorForRefund = sec.RequireSupervisorForRefund;
                    RequireSupervisorForVoid = sec.RequireSupervisorForVoid;
                    RequireSupervisorForPriceOverride = sec.RequireSupervisorForPriceOverride;
                    IsSecuritySettingsLoaded = true;
                }

                var fs = envelope.Failsafe;
                if (fs != null)
                {
                    if (fs.IsFailsafeActive)
                    {
                        FailsafeStatus = "ACTIVE";
                        FailsafeBadgeColor = "#EF4444";
                    }
                    else
                    {
                        FailsafeStatus = "HEALTHY / INACTIVE";
                        FailsafeBadgeColor = "#10B981";
                    }
                    FailsafeEpisodeId = fs.FailsafeInstanceId?.ToString() ?? "N/A";
                    FailsafeEnteredAt = fs.EntryTimestampUtc?.ToString("yyyy-MM-dd HH:mm:ss UTC", CultureInfo.InvariantCulture) ?? "N/A";
                    FailsafeReason = fs.EntryReason ?? "Normal operation";
                    HasPendingClear = fs.HasPendingClear;
                    PendingClearOperationId = fs.PendingClearOperationId?.ToString();
                }
                StatusMessage = "Security settings loaded.";
            }
            else
            {
                FailsafeStatus = "STATUS INDETERMINATE";
                FailsafeBadgeColor = "#F59E0B";
                FailsafeReason = res.ErrorMessage ?? "Server response error";
                ErrorMessage = res.ErrorMessage ?? "Failed to load security settings.";
            }
        }
        catch (Exception ex)
        {
            FailsafeStatus = "STATUS INDETERMINATE";
            FailsafeBadgeColor = "#F59E0B";
            FailsafeReason = $"Telemetry/network error: {ex.Message}";
            ErrorMessage = $"Error loading security settings: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    public async Task<bool> SaveSecuritySettingsAsync(CancellationToken ct = default)
    {
        if (!IsOwner)
        {
            ErrorMessage = "Only the Owner can update security settings.";
            return false;
        }

        if (!IsSecuritySettingsLoaded)
        {
            ErrorMessage = "Security settings must be loaded before saving.";
            return false;
        }

        if (IdleTimeoutMinutes < 1 || IdleTimeoutMinutes > 1440)
        {
            ErrorMessage = "Idle timeout must be between 1 and 1440 minutes.";
            return false;
        }

        if (MaxFailedPinAttempts < 1 || MaxFailedPinAttempts > 20)
        {
            ErrorMessage = "Max failed PIN attempts must be between 1 and 20.";
            return false;
        }

        if (PinLockoutMinutes < 1 || PinLockoutMinutes > 1440)
        {
            ErrorMessage = "PIN lockout duration must be between 1 and 1440 minutes.";
            return false;
        }

        if (MaxCashierDiscountPercentage < 0 || MaxCashierDiscountPercentage > 100)
        {
            ErrorMessage = "Max discount percentage must be between 0% and 100%.";
            return false;
        }

        IsSaving = true;
        ErrorMessage = string.Empty;
        try
        {
            var req = new UpdateSecuritySettingsRequest(
                IdleTimeoutMinutes: IdleTimeoutMinutes,
                MaxFailedPinAttempts: MaxFailedPinAttempts,
                PinLockoutMinutes: PinLockoutMinutes,
                MaxCashierDiscountPercentage: MaxCashierDiscountPercentage,
                RequireSupervisorForRefund: RequireSupervisorForRefund,
                RequireSupervisorForVoid: RequireSupervisorForVoid,
                RequireSupervisorForPriceOverride: RequireSupervisorForPriceOverride);

            var res = await _posApiClient.UpdateSecuritySettingsAsync(req, token: null, ct: ct);
            if (res.Success)
            {
                StatusMessage = "Security settings updated successfully.";
                return true;
            }
            else
            {
                ErrorMessage = res.ErrorMessage ?? "Failed to update security settings.";
                return false;
            }
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Error updating security settings: {ex.Message}";
            return false;
        }
        finally
        {
            IsSaving = false;
        }
    }

    // ==========================================
    // Tab 2 Operations: Hardware Config & Diagnostics
    // ==========================================
    public async Task LoadHardwareConfigAsync(Guid? terminalId = null, CancellationToken ct = default)
    {
        var targetId = terminalId ?? (HardwareTargetTerminalId != Guid.Empty ? HardwareTargetTerminalId : CurrentTerminalId);
        if (targetId == Guid.Empty)
        {
            ErrorMessage = "No terminal selected for hardware configuration.";
            return;
        }

        HardwareTargetTerminalId = targetId;
        IsLoading = true;
        ErrorMessage = string.Empty;
        try
        {
            var res = await _posApiClient.GetTerminalHardwareConfigAsync(targetId, token: null, ct: ct);
            if (res.Success && res.Data != null)
            {
                var h = res.Data;
                PrinterConnectionType = h.PrinterConnectionType;
                PrinterNameOrAddress = h.PrinterNameOrAddress ?? string.Empty;
                PaperWidth = h.PaperWidth;
                PrinterEncoding = h.PrinterEncoding;
                PrinterCodePage = h.PrinterCodePage;
                PrinterTimeoutMs = h.PrinterTimeoutMs;
                AutoCut = h.AutoCut;
                OpenDrawerOnCashSale = h.OpenDrawerOnCashSale;
                DrawerConnectionType = h.DrawerConnectionType;
                DrawerKickPin = h.DrawerKickPin;
                DrawerPulseOnMs = h.DrawerPulseOnMs;
                DrawerPulseOffMs = h.DrawerPulseOffMs;
                ScannerMaxInterKeyMs = h.ScannerMaxInterKeyMs;
                ScaleConnectionType = h.ScaleConnectionType;
                ScalePortName = h.ScalePortName;
                ScaleBaudRate = h.ScaleBaudRate;
                ScaleDataBits = h.ScaleDataBits;
                ScaleParity = h.ScaleParity;
                ScaleStopBits = h.ScaleStopBits;
                ScaleProtocol = h.ScaleProtocol;
                ScaleTimeoutMs = h.ScaleTimeoutMs;
                IsScaleEnabled = h.IsScaleEnabled;
                IsHardwareConfigLoaded = true;
                StatusMessage = "Terminal hardware configuration loaded.";
            }
            else
            {
                ErrorMessage = res.ErrorMessage ?? "Failed to load hardware configuration.";
            }
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Error loading hardware configuration: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    public async Task<bool> SaveHardwareConfigAsync(CancellationToken ct = default)
    {
        if (HardwareTargetTerminalId == Guid.Empty)
        {
            ErrorMessage = "No terminal selected for hardware configuration.";
            return false;
        }

        if (!IsHardwareConfigLoaded)
        {
            ErrorMessage = "Hardware configuration must be loaded before saving.";
            return false;
        }

        IsSaving = true;
        ErrorMessage = string.Empty;
        try
        {
            var req = new UpdateTerminalHardwareConfigRequest(
                PrinterConnectionType: PrinterConnectionType,
                PrinterNameOrAddress: PrinterNameOrAddress?.Trim(),
                PaperWidth: PaperWidth,
                PrinterEncoding: PrinterEncoding,
                PrinterCodePage: PrinterCodePage,
                PrinterTimeoutMs: PrinterTimeoutMs,
                AutoCut: AutoCut,
                OpenDrawerOnCashSale: OpenDrawerOnCashSale,
                DrawerConnectionType: DrawerConnectionType,
                DrawerKickPin: DrawerKickPin,
                DrawerPulseOnMs: DrawerPulseOnMs,
                DrawerPulseOffMs: DrawerPulseOffMs,
                ScannerMaxInterKeyMs: ScannerMaxInterKeyMs,
                ScaleConnectionType: ScaleConnectionType,
                ScalePortName: ScalePortName?.Trim(),
                ScaleBaudRate: ScaleBaudRate,
                ScaleDataBits: ScaleDataBits,
                ScaleParity: ScaleParity,
                ScaleStopBits: ScaleStopBits,
                ScaleProtocol: ScaleProtocol,
                ScaleTimeoutMs: ScaleTimeoutMs,
                IsScaleEnabled: IsScaleEnabled);

            var res = await _posApiClient.UpdateTerminalHardwareConfigAsync(HardwareTargetTerminalId, req, token: null, ct: ct);
            if (res.Success)
            {
                StatusMessage = "Hardware configuration saved successfully.";
                return true;
            }
            else
            {
                ErrorMessage = res.ErrorMessage ?? "Failed to save hardware configuration.";
                return false;
            }
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Error saving hardware configuration: {ex.Message}";
            return false;
        }
        finally
        {
            IsSaving = false;
        }
    }

    // Fail-Closed Diagnostics Commands
    public async Task RunTestPrintAsync(CancellationToken ct = default)
    {
        IsTestingPrinter = true;
        PrinterTestStatus = "Printing sample test slip...";
        try
        {
            var req = new TestPrintRequestDto(
                PrinterName: PrinterNameOrAddress,
                ConnectionType: PrinterConnectionType,
                PaperWidth: PaperWidth,
                OpenDrawer: false,
                CustomHeader: "GROZET RetailPOS Hardware Test");

            var res = await _posApiClient.TestPrintAsync(req, token: null, ct: ct);
            if (res.Success && res.Data?.Success == true)
            {
                PrinterTestStatus = $"Success: Sent {res.Data.BytesSent} bytes to {res.Data.PrinterName} ({res.Data.TimestampUtc:HH:mm:ss} UTC)";
            }
            else
            {
                PrinterTestStatus = $"Failed: {res.Data?.ErrorMessage ?? res.ErrorMessage ?? "Test print failed"}";
            }
        }
        catch (Exception ex)
        {
            PrinterTestStatus = $"Error: {ex.Message}";
        }
        finally
        {
            IsTestingPrinter = false;
        }
    }

    public async Task RunTestDrawerKickAsync(CancellationToken ct = default)
    {
        IsTestingDrawer = true;
        DrawerTestStatus = "Triggering drawer kick...";
        try
        {
            var req = new DrawerKickRequestDto("Manual Hardware Diagnostic Test", HardwareTargetTerminalId != Guid.Empty ? HardwareTargetTerminalId : CurrentTerminalId);
            var res = await _posApiClient.TestDrawerKickAsync(req, token: null, ct: ct);
            if (res.Success && res.Data?.Success == true)
            {
                DrawerTestStatus = $"Success: Pulse signal sent ({res.Data.DrawerType}) at {res.Data.TimestampUtc:HH:mm:ss} UTC";
            }
            else
            {
                DrawerTestStatus = $"Failed: {res.Data?.ErrorMessage ?? res.ErrorMessage ?? "Drawer kick failed"}";
            }
        }
        catch (Exception ex)
        {
            DrawerTestStatus = $"Error: {ex.Message}";
        }
        finally
        {
            IsTestingDrawer = false;
        }
    }

    public async Task PollScaleWeightAsync(CancellationToken ct = default)
    {
        IsPollingScale = true;
        ScaleReadingDetails = "Polling scale...";
        try
        {
            var res = await _posApiClient.GetScaleWeightAsync(token: null, ct: ct);
            if (res.Success && res.Data != null)
            {
                var r = res.Data;
                ScaleWeightDisplay = $"{r.Weight.ToString("F3", CultureInfo.InvariantCulture)} {r.Unit}";
                ScaleReadingDetails = $"Stable: {r.IsStable} | Zero: {r.IsZero} | Tare: {r.IsTare} | Raw: {r.RawReading} ({r.TimestampUtc:HH:mm:ss} UTC)";
            }
            else
            {
                ScaleReadingDetails = $"Failed: {res.ErrorMessage ?? "Scale communication error"}";
            }
        }
        catch (Exception ex)
        {
            ScaleReadingDetails = $"Error: {ex.Message}";
        }
        finally
        {
            IsPollingScale = false;
        }
    }

    public async Task ZeroScaleAsync(CancellationToken ct = default)
    {
        IsPollingScale = true;
        ScaleReadingDetails = "Sending zero scale command...";
        try
        {
            var res = await _posApiClient.ZeroScaleAsync(token: null, ct: ct);
            if (res.Success && res.Data)
            {
                ScaleReadingDetails = "Scale successfully zeroed.";
                await PollScaleWeightAsync(ct);
            }
            else
            {
                ScaleReadingDetails = $"Zero failed: {res.ErrorMessage ?? "Scale rejected zero command"}";
            }
        }
        catch (Exception ex)
        {
            ScaleReadingDetails = $"Error zeroing scale: {ex.Message}";
        }
        finally
        {
            IsPollingScale = false;
        }
    }

    public async Task TareScaleAsync(CancellationToken ct = default)
    {
        IsPollingScale = true;
        ScaleReadingDetails = "Sending tare scale command...";
        try
        {
            var res = await _posApiClient.TareScaleAsync(token: null, ct: ct);
            if (res.Success && res.Data)
            {
                ScaleReadingDetails = "Scale successfully tared.";
                await PollScaleWeightAsync(ct);
            }
            else
            {
                ScaleReadingDetails = $"Tare failed: {res.ErrorMessage ?? "Scale rejected tare command"}";
            }
        }
        catch (Exception ex)
        {
            ScaleReadingDetails = $"Error taring scale: {ex.Message}";
        }
        finally
        {
            IsPollingScale = false;
        }
    }

    public async Task CheckHardwareStatusAsync(CancellationToken ct = default)
    {
        IsCheckingHardwareStatus = true;
        HardwareStatusSummary = "Querying device statuses...";
        try
        {
            var res = await _posApiClient.GetHardwareStatusAsync(token: null, ct: ct);
            if (res.Success && res.Data != null)
            {
                var s = res.Data;
                HardwareStatusSummary = $"Overall: {(s.IsOverallHealthy ? "HEALTHY" : "DEGRADED")} | Printer: {s.PrinterStatus} ({s.ActivePrinterName}) | Drawer: {s.DrawerStatus} | Scale: {s.ScaleStatus} | Scanner: {s.ScannerStatus}";
            }
            else
            {
                HardwareStatusSummary = $"Failed: {res.ErrorMessage ?? "Unable to retrieve hardware status"}";
            }
        }
        catch (Exception ex)
        {
            HardwareStatusSummary = $"Error: {ex.Message}";
        }
        finally
        {
            IsCheckingHardwareStatus = false;
        }
    }

    // ==========================================
    // Tab 3 Operations: Terminals Management
    // ==========================================
    public async Task LoadTerminalsAsync(CancellationToken ct = default)
    {
        IsLoading = true;
        ErrorMessage = string.Empty;
        try
        {
            var res = await _posApiClient.GetTerminalsAsync(token: null, ct: ct);
            if (res.Success && res.Data != null)
            {
                Terminals.Clear();
                foreach (var t in res.Data)
                {
                    Terminals.Add(new TerminalItemViewModel(t));
                }
                StatusMessage = $"Loaded {Terminals.Count} terminals.";
            }
            else
            {
                ErrorMessage = res.ErrorMessage ?? "Failed to load terminals.";
            }
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Error loading terminals: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    public async Task ToggleTerminalStatusAsync(TerminalItemViewModel terminal, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(terminal);
        var newStatus = !terminal.IsActive;
        IsLoading = true;
        ErrorMessage = string.Empty;
        try
        {
            var res = await _posApiClient.SetTerminalStatusAsync(terminal.Id, newStatus, token: null, ct: ct);
            if (res.Success && res.Data != null)
            {
                terminal.Update(res.Data);
                StatusMessage = $"Terminal {terminal.TerminalCode} {(newStatus ? "activated" : "suspended")}.";
            }
            else
            {
                ErrorMessage = res.ErrorMessage ?? "Failed to update terminal status.";
            }
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Error updating terminal status: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    public async Task GenerateEnrollmentTokenAsync(int ttlMinutes = 15, CancellationToken ct = default)
    {
        IsLoading = true;
        ErrorMessage = string.Empty;
        try
        {
            var req = new EnrollmentTokenRequestDto(EnrollmentTokenPurpose.NewEnrollment, null, ttlMinutes);
            var res = await _posApiClient.GenerateEnrollmentTokenAsync(req, token: null, ct: ct);
            if (res.Success && res.Data != null)
            {
                GeneratedEnrollmentToken = res.Data.Token;
                GeneratedTokenPurpose = "New Terminal Enrollment";
                GeneratedTokenExpiresAt = res.Data.ExpiresAtUtc;
                HasGeneratedToken = true;
                StatusMessage = "Enrollment token generated successfully.";
            }
            else
            {
                ErrorMessage = res.ErrorMessage ?? "Failed to generate enrollment token.";
            }
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Error generating enrollment token: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    public async Task GenerateRepairTokenAsync(Guid terminalId, int ttlMinutes = 15, CancellationToken ct = default)
    {
        if (terminalId == Guid.Empty)
        {
            ErrorMessage = "Select a terminal to generate a repair token.";
            return;
        }

        IsLoading = true;
        ErrorMessage = string.Empty;
        try
        {
            var req = new EnrollmentTokenRequestDto(EnrollmentTokenPurpose.ExistingTerminalRepair, terminalId, ttlMinutes);
            var res = await _posApiClient.GenerateRepairTokenAsync(req, token: null, ct: ct);
            if (res.Success && res.Data != null)
            {
                GeneratedEnrollmentToken = res.Data.Token;
                GeneratedTokenPurpose = $"Terminal Repair ({terminalId})";
                GeneratedTokenExpiresAt = res.Data.ExpiresAtUtc;
                HasGeneratedToken = true;
                StatusMessage = "Repair token generated successfully.";
            }
            else
            {
                ErrorMessage = res.ErrorMessage ?? "Failed to generate repair token.";
            }
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Error generating repair token: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    public async Task<bool> DecommissionTerminalAsync(TerminalItemViewModel terminal, string confirmationPhrase, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(terminal);
        if (!string.Equals(confirmationPhrase?.Trim(), "DECOMMISSION", StringComparison.Ordinal))
        {
            ErrorMessage = "Type 'DECOMMISSION' exactly to confirm terminal decommissioning.";
            return false;
        }

        IsLoading = true;
        ErrorMessage = string.Empty;
        try
        {
            var res = await _posApiClient.DecommissionTerminalAsync(terminal.Id, token: null, ct: ct);
            if (res.Success)
            {
                StatusMessage = $"Terminal {terminal.TerminalCode} decommissioned successfully.";
                DecommissionConfirmPhrase = string.Empty;
                await LoadTerminalsAsync(ct);
                return true;
            }
            else
            {
                ErrorMessage = res.ErrorMessage ?? "Failed to decommission terminal.";
                return false;
            }
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Error decommissioning terminal: {ex.Message}";
            return false;
        }
        finally
        {
            IsLoading = false;
        }
    }

    // ==========================================
    // Tab 4 Operations: Staff & Roles Management
    // ==========================================
    public async Task LoadUsersAsync(CancellationToken ct = default)
    {
        IsLoading = true;
        ErrorMessage = string.Empty;
        try
        {
            var res = await _posApiClient.GetUsersAsync(token: null, ct: ct);
            if (res.Success && res.Data != null)
            {
                Users.Clear();
                foreach (var u in res.Data)
                {
                    Users.Add(new UserItemViewModel(u));
                }
                StatusMessage = $"Loaded {Users.Count} staff members.";
            }
            else
            {
                ErrorMessage = res.ErrorMessage ?? "Failed to load staff list.";
            }
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Error loading staff: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    public async Task<bool> CreateUserAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(NewUsername))
        {
            ErrorMessage = "Username is required.";
            return false;
        }
        if (string.IsNullOrWhiteSpace(NewFullName))
        {
            ErrorMessage = "Full Name is required.";
            return false;
        }
        if (string.IsNullOrWhiteSpace(NewPassword) || NewPassword.Length < 6)
        {
            ErrorMessage = "Password must be at least 6 characters.";
            return false;
        }
        if (string.IsNullOrWhiteSpace(NewPin) || NewPin.Length < 4)
        {
            ErrorMessage = "PIN must be at least 4 digits.";
            return false;
        }
        if (!IsOwner && NewUserRole is RoleType.Owner or RoleType.Admin)
        {
            ErrorMessage = "Only an Owner can create Owner or Admin accounts.";
            return false;
        }

        IsSaving = true;
        ErrorMessage = string.Empty;
        try
        {
            var req = new CreateUserRequest(NewUsername.Trim(), NewFullName.Trim(), NewPassword, NewPin.Trim(), NewUserRole);
            var res = await _posApiClient.CreateUserAsync(req, token: null, ct: ct);
            if (res.Success && res.Data != null)
            {
                StatusMessage = $"User '{res.Data.Username}' created successfully.";
                NewUsername = string.Empty;
                NewFullName = string.Empty;
                NewPassword = string.Empty;
                NewPin = string.Empty;
                NewUserRole = RoleType.Cashier;
                await LoadUsersAsync(ct);
                return true;
            }
            else
            {
                ErrorMessage = res.ErrorMessage ?? "Failed to create user.";
                return false;
            }
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Error creating user: {ex.Message}";
            return false;
        }
        finally
        {
            IsSaving = false;
        }
    }

    public async Task ActivateUserAsync(UserItemViewModel user, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(user);
        IsLoading = true;
        ErrorMessage = string.Empty;
        try
        {
            var res = await _posApiClient.ActivateUserAsync(user.Id, token: null, ct: ct);
            if (res.Success)
            {
                await LoadUsersAsync(ct);
                StatusMessage = res.Data?.Message ?? $"User '{user.Username}' activated.";
            }
            else
            {
                ErrorMessage = res.ErrorMessage ?? "Failed to activate user.";
            }
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Error activating user: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    public async Task DeactivateUserAsync(UserItemViewModel user, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(user);
        if (user.Id == CurrentUserId)
        {
            ErrorMessage = "Cannot deactivate your own logged-in account.";
            return;
        }

        IsLoading = true;
        ErrorMessage = string.Empty;
        try
        {
            var res = await _posApiClient.DeactivateUserAsync(user.Id, token: null, ct: ct);
            if (res.Success)
            {
                await LoadUsersAsync(ct);
                StatusMessage = res.Data?.Message ?? $"User '{user.Username}' deactivated.";
            }
            else
            {
                ErrorMessage = res.ErrorMessage ?? "Failed to deactivate user.";
            }
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Error deactivating user: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    public async Task UnlockUserAsync(UserItemViewModel user, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(user);
        IsLoading = true;
        ErrorMessage = string.Empty;
        try
        {
            var res = await _posApiClient.UnlockUserAsync(user.Id, token: null, ct: ct);
            if (res.Success)
            {
                user.IsLocked = false;
                StatusMessage = res.Data?.Message ?? $"User '{user.Username}' unlocked.";
            }
            else
            {
                ErrorMessage = res.ErrorMessage ?? "Failed to unlock user.";
            }
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Error unlocking user: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    public async Task<bool> ResetUserCredentialsAsync(UserItemViewModel user, string? newPassword, string? newPin, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(user);
        if (string.IsNullOrWhiteSpace(newPassword) && string.IsNullOrWhiteSpace(newPin))
        {
            ErrorMessage = "Provide a new password, new PIN, or both.";
            return false;
        }

        IsSaving = true;
        ErrorMessage = string.Empty;
        try
        {
            var req = new ResetUserCredentialsRequest(
                string.IsNullOrWhiteSpace(newPassword) ? null : newPassword,
                string.IsNullOrWhiteSpace(newPin) ? null : newPin.Trim());

            var res = await _posApiClient.ResetUserCredentialsAsync(user.Id, req, token: null, ct: ct);
            if (res.Success)
            {
                StatusMessage = res.Data?.Message ?? $"Credentials reset for user '{user.Username}'.";
                ResetPasswordText = string.Empty;
                ResetPinText = string.Empty;
                return true;
            }
            else
            {
                ErrorMessage = res.ErrorMessage ?? "Failed to reset credentials.";
                return false;
            }
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Error resetting credentials: {ex.Message}";
            return false;
        }
        finally
        {
            IsSaving = false;
        }
    }

    public async Task<bool> ChangeUserRoleAsync(UserItemViewModel user, RoleType newRole, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(user);
        if (!IsOwner)
        {
            ErrorMessage = "Only the Owner can modify user roles.";
            return false;
        }

        if (user.Id == CurrentUserId)
        {
            ErrorMessage = "Cannot modify the role of your own logged-in account.";
            return false;
        }

        IsSaving = true;
        ErrorMessage = string.Empty;
        try
        {
            var req = new UpdateUserRoleRequest(newRole);
            var res = await _posApiClient.ChangeUserRoleAsync(user.Id, req, token: null, ct: ct);
            if (res.Success)
            {
                await LoadUsersAsync(ct);
                StatusMessage = res.Data?.Message ?? $"Role for user '{user.Username}' changed to {newRole}.";
                return true;
            }
            else
            {
                ErrorMessage = res.ErrorMessage ?? "Failed to change user role.";
                return false;
            }
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Error changing role: {ex.Message}";
            return false;
        }
        finally
        {
            IsSaving = false;
        }
    }

    // ==========================================
    // Tab 5 Operations: Backups
    // ==========================================
    public async Task LoadBackupsAsync(CancellationToken ct = default)
    {
        IsLoading = true;
        ErrorMessage = string.Empty;
        try
        {
            var res = await _posApiClient.GetBackupsAsync(token: null, ct: ct);
            if (res.Success && res.Data != null)
            {
                Backups.Clear();
                foreach (var b in res.Data)
                {
                    Backups.Add(new BackupItemViewModel(b));
                }
                StatusMessage = $"Loaded {Backups.Count} backup records.";
            }
            else
            {
                ErrorMessage = res.ErrorMessage ?? "Failed to load backups.";
            }
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Error loading backups: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    public async Task<bool> CreateBackupAsync(CancellationToken ct = default)
    {
        IsCreatingBackup = true;
        ErrorMessage = string.Empty;
        try
        {
            var req = new CreateBackupRequestDto
            {
                Reason = string.IsNullOrWhiteSpace(NewBackupReason) ? "Manual administrator snapshot" : NewBackupReason.Trim()
            };

            var res = await _posApiClient.CreateBackupAsync(req, token: null, ct: ct);
            if (res.Success && res.Data != null)
            {
                var createdFileName = res.Data.FileName;
                NewBackupReason = string.Empty;
                await LoadBackupsAsync(ct);
                StatusMessage = $"Backup '{createdFileName}' created successfully.";
                return true;
            }
            else
            {
                ErrorMessage = res.ErrorMessage ?? "Failed to create backup.";
                return false;
            }
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Error creating backup: {ex.Message}";
            return false;
        }
        finally
        {
            IsCreatingBackup = false;
        }
    }

    public void Dispose()
    {
        _activeCts?.Cancel();
        _activeCts?.Dispose();
        _activeCts = null;
    }
}

// ==========================================
// Helper Sub-ViewModels for Tab Collections
// ==========================================

public sealed partial class TerminalItemViewModel : ObservableObject
{
    public TerminalDto Dto { get; private set; }

    public Guid Id => Dto.Id;
    public string TerminalCode => Dto.TerminalCode;
    public string TerminalName => Dto.TerminalName;
    public string? ReceiptPrinterName => Dto.ReceiptPrinterName;

    [ObservableProperty]
    public partial bool IsActive { get; set; }

    public string StatusText => IsActive ? "Active" : "Suspended";
    public string StatusBadgeColor => IsActive ? "#10B981" : "#F59E0B";
    public string ToggleStatusButtonText => IsActive ? "Suspend" : "Activate";

    partial void OnIsActiveChanged(bool value)
    {
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(StatusBadgeColor));
        OnPropertyChanged(nameof(ToggleStatusButtonText));
    }

    public TerminalItemViewModel(TerminalDto dto)
    {
        Dto = dto ?? throw new ArgumentNullException(nameof(dto));
        IsActive = dto.IsActive;
    }

    public void Update(TerminalDto dto)
    {
        Dto = dto ?? throw new ArgumentNullException(nameof(dto));
        IsActive = dto.IsActive;
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(StatusBadgeColor));
        OnPropertyChanged(nameof(ToggleStatusButtonText));
    }
}

public sealed partial class UserItemViewModel : ObservableObject
{
    public UserDto Dto { get; private set; }

    public Guid Id => Dto.Id;
    public string Username => Dto.Username;
    public string FullName => Dto.FullName;

    [ObservableProperty]
    public partial RoleType Role { get; set; }

    [ObservableProperty]
    public partial bool IsActive { get; set; }

    [ObservableProperty]
    public partial bool IsLocked { get; set; }

    public string StatusText => !IsActive ? "Inactive" : (IsLocked ? "Locked Out" : "Active");
    public string StatusBadgeColor => !IsActive ? "#94A3B8" : (IsLocked ? "#EF4444" : "#10B981");
    public string ToggleActiveButtonText => IsActive ? "Deactivate" : "Activate";

    partial void OnIsActiveChanged(bool value)
    {
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(StatusBadgeColor));
        OnPropertyChanged(nameof(ToggleActiveButtonText));
    }

    partial void OnIsLockedChanged(bool value)
    {
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(StatusBadgeColor));
    }

    public UserItemViewModel(UserDto dto)
    {
        Dto = dto ?? throw new ArgumentNullException(nameof(dto));
        Role = dto.Role;
        IsActive = dto.IsActive;
        IsLocked = dto.IsLocked;
    }

    public void Update(UserDto dto)
    {
        Dto = dto ?? throw new ArgumentNullException(nameof(dto));
        Role = dto.Role;
        IsActive = dto.IsActive;
        IsLocked = dto.IsLocked;
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(StatusBadgeColor));
        OnPropertyChanged(nameof(ToggleActiveButtonText));
    }
}

public sealed partial class BackupItemViewModel : ObservableObject
{
    public BackupRecordDto Dto { get; }

    public Guid OperationId => Dto.BackupOperationId;
    public string FileName => Dto.FileName;
    public long SizeBytes => Dto.SizeBytes;
    public string Type => Dto.Type;
    public string Status => Dto.Status;
    public DateTime CreatedAtUtc => Dto.CreatedAtUtc;
    public string TriggeredBy => string.IsNullOrWhiteSpace(Dto.TriggeredByUserName) ? "System" : Dto.TriggeredByUserName;

    public string FormattedSize => SizeBytes switch
    {
        >= 1024 * 1024 => $"{(SizeBytes / (1024.0 * 1024.0)):F2} MB",
        >= 1024 => $"{(SizeBytes / 1024.0):F1} KB",
        _ => $"{SizeBytes} B"
    };

    public string FormattedCreatedAt => CreatedAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

    public BackupItemViewModel(BackupRecordDto dto)
    {
        Dto = dto ?? throw new ArgumentNullException(nameof(dto));
    }
}
