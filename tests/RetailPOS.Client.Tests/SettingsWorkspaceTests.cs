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
using RetailPOS.Contracts.Backup;
using RetailPOS.Contracts.Common;
using RetailPOS.Contracts.Configuration;
using RetailPOS.Contracts.Hardware;
using RetailPOS.Contracts.Security;
using RetailPOS.Contracts.Terminals;
using RetailPOS.Contracts.Users;
using RetailPOS.Domain.Enums;
using Xunit;

namespace RetailPOS.Client.Tests;

[Collection("ClientSessionTests")]
public sealed class SettingsWorkspaceTests
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private sealed class MockHttpHandler : HttpMessageHandler
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

    private static void SetupSession(RoleType role, Guid? userId = null, Guid? terminalId = null)
    {
        var session = ClientSessionManager.Instance;
        session.ClearSession();
        var login = new LoginResponse(
            "test-token-settings-123",
            userId ?? Guid.Parse("11111111-1111-1111-1111-111111111111"),
            "test_admin",
            "Test Admin Operator",
            role,
            terminalId ?? Guid.Parse("22222222-2222-2222-2222-222222222222"),
            DateTime.UtcNow.AddHours(8));
        session.SetActiveSession(login);
    }

    // ==========================================
    // 1. Role Isolation & Access Control Tests
    // ==========================================

    [Theory]
    [InlineData(RoleType.Cashier)]
    [InlineData(RoleType.Viewer)]
    public void NonAdminRoles_AreDeniedAccessToSettingsWorkspace(RoleType role)
    {
        SetupSession(role);
        using var vm = new SettingsViewModel();

        Assert.True(vm.IsAccessDenied);
        Assert.False(vm.IsOwnerOrAdmin);
        Assert.False(vm.IsOwner);
    }

    [Fact]
    public void AdminRole_HasAccessToWorkspace_ButCannotEditSecurityOrRoles()
    {
        SetupSession(RoleType.Admin);
        using var vm = new SettingsViewModel();

        Assert.False(vm.IsAccessDenied);
        Assert.True(vm.IsOwnerOrAdmin);
        Assert.False(vm.IsOwner);
        Assert.False(vm.CanEditSecuritySettings);
        Assert.False(vm.CanChangeUserRoles);
    }

    [Fact]
    public void OwnerRole_HasFullAccess_IncludingSecurityAndRoleModification()
    {
        SetupSession(RoleType.Owner);
        using var vm = new SettingsViewModel();

        Assert.False(vm.IsAccessDenied);
        Assert.True(vm.IsOwnerOrAdmin);
        Assert.True(vm.IsOwner);
        Assert.True(vm.CanEditSecuritySettings);
        Assert.True(vm.CanChangeUserRoles);
    }

    // ==========================================
    // 2. Security Settings & Failsafe Status Tests
    // ==========================================

    [Fact]
    public async Task AdminRole_LoadingSecuritySettings_DoesNotCallEndpoint_AndShowsNeutralUnavailableBadge()
    {
        SetupSession(RoleType.Admin);
        var mock = new MockHttpHandler();
        var client = new PosApiClient(new HttpClient(mock), "https://127.0.0.1:5001");
        using var vm = new SettingsViewModel(client);

        await vm.LoadSecuritySettingsAsync();

        // Must not call GET /api/v1/settings/security
        Assert.Empty(mock.CapturedRequests);
        Assert.Equal("STATUS UNAVAILABLE TO THIS ROLE", vm.FailsafeStatus);
        Assert.Equal("#64748B", vm.FailsafeBadgeColor); // Neutral slate grey
        Assert.False(vm.IsSecuritySettingsLoaded);
    }

    [Fact]
    public async Task AdminRole_AttemptingToSaveSecuritySettings_IsRejectedClientSide()
    {
        SetupSession(RoleType.Admin);
        var mock = new MockHttpHandler();
        var client = new PosApiClient(new HttpClient(mock), "https://127.0.0.1:5001");
        using var vm = new SettingsViewModel(client);

        var result = await vm.SaveSecuritySettingsAsync();

        Assert.False(result);
        Assert.Contains("Only the Owner", vm.ErrorMessage);
        Assert.Empty(mock.CapturedRequests);
    }

    [Fact]
    public async Task OwnerRole_LoadingSecuritySettings_MapsLiveFailsafeTelemetry()
    {
        SetupSession(RoleType.Owner);
        var episodeId = Guid.NewGuid();
        var mock = new MockHttpHandler
        {
            Handler = req =>
            {
                if (req.RequestUri!.ToString().Contains("/api/v1/settings/security"))
                {
                    var envelope = new SecuritySettingsEnvelopeDto(
                        Settings: new SecuritySettingsDto(
                            IdleTimeoutMinutes: 20,
                            MaxFailedPinAttempts: 3,
                            PinLockoutMinutes: 30,
                            MaxCashierDiscountPercentage: 15.5m,
                            RequireSupervisorForRefund: true,
                            RequireSupervisorForVoid: true,
                            RequireSupervisorForPriceOverride: true),
                        Failsafe: new FailsafeStatusResponse(
                            IsFailsafeActive: false,
                            FailsafeInstanceId: episodeId,
                            EntryTimestampUtc: DateTime.UtcNow.AddDays(-1),
                            EntryReason: "Normal baseline state",
                            HasPendingClear: false,
                            PendingClearOperationId: null));

                    var resp = ApiResponse.Ok(envelope);
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = JsonContent.Create(resp)
                    });
                }
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            }
        };

        var client = new PosApiClient(new HttpClient(mock), "https://127.0.0.1:5001");
        using var vm = new SettingsViewModel(client);

        await vm.LoadSecuritySettingsAsync();

        Assert.True(vm.IsSecuritySettingsLoaded);
        Assert.Equal(20, vm.IdleTimeoutMinutes);
        Assert.Equal(3, vm.MaxFailedPinAttempts);
        Assert.Equal(30, vm.PinLockoutMinutes);
        Assert.Equal(15.5m, vm.MaxCashierDiscountPercentage);
        Assert.Equal("HEALTHY / INACTIVE", vm.FailsafeStatus);
        Assert.Equal("#10B981", vm.FailsafeBadgeColor);
        Assert.Equal(episodeId.ToString(), vm.FailsafeEpisodeId);
    }

    [Fact]
    public async Task OwnerRole_WhenFailsafeActive_RendersActiveStatusAndRedBadge()
    {
        SetupSession(RoleType.Owner);
        var episodeId = Guid.NewGuid();
        var mock = new MockHttpHandler
        {
            Handler = req =>
            {
                var envelope = new SecuritySettingsEnvelopeDto(
                    Settings: new SecuritySettingsDto(),
                    Failsafe: new FailsafeStatusResponse(
                        IsFailsafeActive: true,
                        FailsafeInstanceId: episodeId,
                        EntryTimestampUtc: DateTime.UtcNow,
                        EntryReason: "Tamper detection breach",
                        HasPendingClear: false,
                        PendingClearOperationId: null));

                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(ApiResponse.Ok(envelope))
                });
            }
        };

        var client = new PosApiClient(new HttpClient(mock), "https://127.0.0.1:5001");
        using var vm = new SettingsViewModel(client);

        await vm.LoadSecuritySettingsAsync();

        Assert.Equal("ACTIVE", vm.FailsafeStatus);
        Assert.Equal("#EF4444", vm.FailsafeBadgeColor);
    }

    [Fact]
    public async Task OwnerRole_WhenNetworkFails_RendersStatusIndeterminateWithAmberBadge()
    {
        SetupSession(RoleType.Owner);
        var mock = new MockHttpHandler
        {
            Handler = _ => throw new HttpRequestException("Connection refused")
        };

        var client = new PosApiClient(new HttpClient(mock), "https://127.0.0.1:5001");
        using var vm = new SettingsViewModel(client);

        await vm.LoadSecuritySettingsAsync();

        Assert.Equal("STATUS INDETERMINATE", vm.FailsafeStatus);
        Assert.Equal("#F59E0B", vm.FailsafeBadgeColor);
        Assert.Contains("Telemetry/network error", vm.FailsafeReason);
    }

    [Fact]
    public async Task OwnerRole_SecuritySettingsValidation_RejectsInvalidRanges()
    {
        SetupSession(RoleType.Owner);
        using var vm = new SettingsViewModel();
        vm.IsSecuritySettingsLoaded = true;

        // Idle timeout < 1
        vm.IdleTimeoutMinutes = 0;
        Assert.False(await vm.SaveSecuritySettingsAsync());
        Assert.Contains("Idle timeout", vm.ErrorMessage);

        // Max failed attempts > 20
        vm.IdleTimeoutMinutes = 15;
        vm.MaxFailedPinAttempts = 25;
        Assert.False(await vm.SaveSecuritySettingsAsync());
        Assert.Contains("Max failed PIN attempts", vm.ErrorMessage);

        // Discount > 100%
        vm.MaxFailedPinAttempts = 5;
        vm.MaxCashierDiscountPercentage = 105.0m;
        Assert.False(await vm.SaveSecuritySettingsAsync());
        Assert.Contains("discount percentage", vm.ErrorMessage);
    }

    // ==========================================
    // 3. Store Settings Tests
    // ==========================================

    [Fact]
    public async Task StoreSettings_SaveBeforeLoad_IsBlocked()
    {
        SetupSession(RoleType.Admin);
        using var vm = new SettingsViewModel();
        vm.IsStoreSettingsLoaded = false;

        var result = await vm.SaveStoreSettingsAsync();

        Assert.False(result);
        Assert.Contains("must be loaded before saving", vm.ErrorMessage);
    }

    [Fact]
    public async Task StoreSettings_EmptyStoreName_IsBlockedByValidation()
    {
        SetupSession(RoleType.Admin);
        using var vm = new SettingsViewModel();
        vm.IsStoreSettingsLoaded = true;
        vm.StoreName = "   ";

        var result = await vm.SaveStoreSettingsAsync();

        Assert.False(result);
        Assert.Contains("Store Name is required", vm.ErrorMessage);
    }

    [Fact]
    public async Task StoreSettings_SuccessfulLoadAndSave_UpdatesState()
    {
        SetupSession(RoleType.Admin);
        var mock = new MockHttpHandler
        {
            Handler = req =>
            {
                if (req.Method == HttpMethod.Get && req.RequestUri!.ToString().Contains("/api/v1/store/settings"))
                {
                    var dto = new StoreSettingsDto(
                        Id: 1,
                        StoreName: "Grozet Retail",
                        Address: "Main Street, Chennai",
                        Phone: "+91 9876543210",
                        TaxRegistrationNumber: "33AAAAA0000A1Z5",
                        ReceiptFooterMessage: "Thank you!",
                        DefaultPricingModel: PricingModel.TaxInclusive,
                        NegativeStockPolicy: NegativeStockPolicy.Disallowed,
                        CashRoundingEnabled: true,
                        TimeZoneId: "India Standard Time");

                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = JsonContent.Create(ApiResponse.Ok(dto))
                    });
                }

                if (req.Method == HttpMethod.Put && req.RequestUri!.ToString().Contains("/api/v1/store/settings"))
                {
                    var updated = new StoreSettingsDto(
                        Id: 1,
                        StoreName: "Grozet Retail Updated",
                        Address: "Main Street, Chennai",
                        Phone: "+91 9876543210",
                        TaxRegistrationNumber: "33AAAAA0000A1Z5",
                        ReceiptFooterMessage: "Thank you!",
                        DefaultPricingModel: PricingModel.TaxInclusive,
                        NegativeStockPolicy: NegativeStockPolicy.Disallowed,
                        CashRoundingEnabled: true,
                        TimeZoneId: "India Standard Time");

                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = JsonContent.Create(ApiResponse.Ok(updated))
                    });
                }

                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            }
        };

        var client = new PosApiClient(new HttpClient(mock), "https://127.0.0.1:5001");
        using var vm = new SettingsViewModel(client);

        await vm.LoadStoreSettingsAsync();
        Assert.True(vm.IsStoreSettingsLoaded);
        Assert.Equal("Grozet Retail", vm.StoreName);
        Assert.True(vm.CashRoundingEnabled);

        vm.StoreName = "Grozet Retail Updated";
        var saveResult = await vm.SaveStoreSettingsAsync();
        Assert.True(saveResult);
        Assert.Contains("Store settings saved", vm.StatusMessage);
    }

    // ==========================================
    // 4. Hardware Configuration & Diagnostic Tests
    // ==========================================

    [Fact]
    public async Task HardwareConfig_GetAndUpdate_DoNotAttachTerminalHeaders()
    {
        SetupSession(RoleType.Admin);
        var terminalId = Guid.NewGuid();
        var mock = new MockHttpHandler
        {
            Handler = req =>
            {
                var dto = new TerminalHardwareConfigDto(
                    TerminalId: terminalId,
                    PrinterConnectionType: PrinterConnectionType.Simulated,
                    PrinterNameOrAddress: "SimPrinter",
                    PaperWidth: PaperWidth.Width80mm,
                    PrinterEncoding: PrinterEncoding.Ascii,
                    PrinterCodePage: 437,
                    PrinterTimeoutMs: 5000,
                    AutoCut: true,
                    OpenDrawerOnCashSale: true,
                    DrawerConnectionType: DrawerConnectionType.PrinterDriven,
                    DrawerKickPin: 0,
                    DrawerPulseOnMs: 50,
                    DrawerPulseOffMs: 250,
                    ScannerMaxInterKeyMs: 50,
                    ScaleConnectionType: ScaleConnectionType.SerialCom,
                    ScalePortName: "COM1",
                    ScaleBaudRate: 9600,
                    ScaleDataBits: 8,
                    ScaleParity: 0,
                    ScaleStopBits: 1,
                    ScaleProtocol: ScaleProtocol.CasToledo,
                    ScaleTimeoutMs: 2000,
                    IsScaleEnabled: true,
                    UpdatedAtUtc: DateTime.UtcNow);

                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(ApiResponse.Ok(dto))
                });
            }
        };

        var client = new PosApiClient(new HttpClient(mock), "https://127.0.0.1:5001");

        // Verify GET uses attachTerminalHeaders: false
        await client.GetTerminalHardwareConfigAsync(terminalId);
        var getReq = mock.CapturedRequests[^1];
        Assert.False(getReq.Headers.Contains("X-Terminal-Id"));

        // Verify PUT uses attachTerminalHeaders: false
        await client.UpdateTerminalHardwareConfigAsync(terminalId, new UpdateTerminalHardwareConfigRequest());
        var putReq = mock.CapturedRequests[^1];
        Assert.False(putReq.Headers.Contains("X-Terminal-Id"));
    }

    [Fact]
    public async Task HardwareDiagnostics_AttachTerminalHeadersStrictly()
    {
        SetupSession(RoleType.Admin);
        var mock = new MockHttpHandler
        {
            Handler = req =>
            {
                if (req.RequestUri!.ToString().Contains("/hardware/printer/test-print"))
                {
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = JsonContent.Create(ApiResponse.Ok(new TestPrintResponseDto(true, "SimPrinter", PaperWidth.Width80mm, 120, null, DateTime.UtcNow)))
                    });
                }
                if (req.RequestUri!.ToString().Contains("/hardware/drawer/test-kick"))
                {
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = JsonContent.Create(ApiResponse.Ok(new DrawerKickResponseDto(true, "PrinterDriven", null, DateTime.UtcNow)))
                    });
                }
                if (req.RequestUri!.ToString().Contains("/hardware/scale/current-weight"))
                {
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = JsonContent.Create(ApiResponse.Ok(new ScaleWeightReadingDto(1.250m, ScaleWeightUnit.Kilogram, true, false, false, "ST,GS,1.250kg", DateTime.UtcNow)))
                    });
                }
                if (req.RequestUri!.ToString().Contains("/hardware/status"))
                {
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = JsonContent.Create(ApiResponse.Ok(new HardwareStatusDto(true, "Online", "Online", "Online", "Online", "SimPrinter", PaperWidth.Width80mm)))
                    });
                }
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(ApiResponse.Ok(true))
                });
            }
        };

        var client = new PosApiClient(new HttpClient(mock), "https://127.0.0.1:5001");

        // Test Print
        await client.TestPrintAsync(new TestPrintRequestDto());
        Assert.True(mock.CapturedRequests[^1].Headers.Contains("X-Terminal-Id"));

        // Test Drawer Kick
        await client.TestDrawerKickAsync(new DrawerKickRequestDto("Test", Guid.NewGuid()));
        Assert.True(mock.CapturedRequests[^1].Headers.Contains("X-Terminal-Id"));

        // Scale Weight
        await client.GetScaleWeightAsync();
        Assert.True(mock.CapturedRequests[^1].Headers.Contains("X-Terminal-Id"));

        // Scale Zero & Tare
        await client.ZeroScaleAsync();
        Assert.True(mock.CapturedRequests[^1].Headers.Contains("X-Terminal-Id"));

        await client.TareScaleAsync();
        Assert.True(mock.CapturedRequests[^1].Headers.Contains("X-Terminal-Id"));

        // Hardware Status
        await client.GetHardwareStatusAsync();
        Assert.True(mock.CapturedRequests[^1].Headers.Contains("X-Terminal-Id"));
    }

    [Fact]
    public void HardwareDiagnostics_FailClosedRule_DoesNotExecuteOnInitialization()
    {
        SetupSession(RoleType.Admin);
        var mock = new MockHttpHandler();
        var client = new PosApiClient(new HttpClient(mock), "https://127.0.0.1:5001");
        using var vm = new SettingsViewModel(client);

        // Changing tab to Hardware (tab 2) must not run hardware diagnostics automatically
        vm.SelectedTabIndex = 2;

        Assert.Equal("Not tested", vm.PrinterTestStatus);
        Assert.Equal("Not tested", vm.DrawerTestStatus);
        Assert.Equal("-- kg", vm.ScaleWeightDisplay);
        Assert.DoesNotContain(mock.CapturedRequests, r => r.RequestUri!.ToString().Contains("/hardware/"));
    }

    // ==========================================
    // 5. Terminals Management Tests
    // ==========================================

    [Fact]
    public async Task TerminalsManagement_LoadAndToggleStatus()
    {
        SetupSession(RoleType.Admin);
        var terminalId = Guid.NewGuid();
        var mock = new MockHttpHandler
        {
            Handler = req =>
            {
                if (req.Method == HttpMethod.Get)
                {
                    var list = new List<TerminalDto>
                    {
                        new(terminalId, "POS-01", "Counter 1", "Epson TM-T88", true)
                    };
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = JsonContent.Create(ApiResponse.Ok<IReadOnlyList<TerminalDto>>(list))
                    });
                }
                if (req.Method == HttpMethod.Put)
                {
                    var updated = new TerminalDto(terminalId, "POS-01", "Counter 1", "Epson TM-T88", false);
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = JsonContent.Create(ApiResponse.Ok(updated))
                    });
                }
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            }
        };

        var client = new PosApiClient(new HttpClient(mock), "https://127.0.0.1:5001");
        using var vm = new SettingsViewModel(client);

        await vm.LoadTerminalsAsync();
        Assert.Single(vm.Terminals);
        var item = vm.Terminals[0];
        Assert.True(item.IsActive);
        Assert.Equal("Active", item.StatusText);

        await vm.ToggleTerminalStatusAsync(item);
        Assert.False(item.IsActive);
        Assert.Equal("Suspended", item.StatusText);
    }

    [Fact]
    public async Task DecommissionTerminal_RequiresExactConfirmationPhrase()
    {
        SetupSession(RoleType.Admin);
        var terminalId = Guid.NewGuid();
        var mock = new MockHttpHandler
        {
            Handler = _ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(ApiResponse.Ok<object>(new { Success = true }))
            })
        };

        var client = new PosApiClient(new HttpClient(mock), "https://127.0.0.1:5001");
        using var vm = new SettingsViewModel(client);
        var terminal = new TerminalItemViewModel(new TerminalDto(terminalId, "POS-02", "Counter 2", null, true));

        // Attempt with wrong phrase
        var res1 = await vm.DecommissionTerminalAsync(terminal, "wrong_phrase");
        Assert.False(res1);
        Assert.Contains("Type 'DECOMMISSION' exactly", vm.ErrorMessage);
        Assert.Empty(mock.CapturedRequests);

        // Attempt with exact phrase
        var res2 = await vm.DecommissionTerminalAsync(terminal, "DECOMMISSION");
        Assert.True(res2);
        Assert.NotEmpty(mock.CapturedRequests);
    }

    // ==========================================
    // 6. Staff & Roles Management Tests
    // ==========================================

    [Fact]
    public async Task StaffManagement_CreateUserValidation()
    {
        SetupSession(RoleType.Admin);
        using var vm = new SettingsViewModel();

        // Empty username
        vm.NewUsername = "";
        Assert.False(await vm.CreateUserAsync());
        Assert.Contains("Username is required", vm.ErrorMessage);

        // Short password
        vm.NewUsername = "cashier_99";
        vm.NewFullName = "Cashier Ninety-Nine";
        vm.NewPassword = "123";
        Assert.False(await vm.CreateUserAsync());
        Assert.Contains("Password must be at least 6 characters", vm.ErrorMessage);

        // Short PIN
        vm.NewPassword = "securePassword123";
        vm.NewPin = "12";
        Assert.False(await vm.CreateUserAsync());
        Assert.Contains("PIN must be at least 4 digits", vm.ErrorMessage);

        // Admin attempting to create Owner account
        vm.NewPin = "1234";
        vm.NewUserRole = RoleType.Owner;
        Assert.False(await vm.CreateUserAsync());
        Assert.Contains("Only an Owner can create Owner or Admin accounts", vm.ErrorMessage);
    }

    [Fact]
    public async Task StaffManagement_CannotDeactivateOwnAccount()
    {
        var myUserId = Guid.NewGuid();
        SetupSession(RoleType.Owner, userId: myUserId);
        using var vm = new SettingsViewModel();

        var self = new UserItemViewModel(new UserDto(myUserId, "owner_me", "Store Owner", RoleType.Owner, true, false));
        await vm.DeactivateUserAsync(self);

        Assert.Contains("Cannot deactivate your own logged-in account", vm.ErrorMessage);
    }

    [Fact]
    public async Task StaffManagement_RoleChange_RestrictedToOwner()
    {
        var targetUserId = Guid.NewGuid();
        var targetUser = new UserItemViewModel(new UserDto(targetUserId, "cashier_01", "Cashier One", RoleType.Cashier, true, false));

        // Admin tries to change role
        SetupSession(RoleType.Admin);
        using var vmAdmin = new SettingsViewModel();
        var resAdmin = await vmAdmin.ChangeUserRoleAsync(targetUser, RoleType.Manager);
        Assert.False(resAdmin);
        Assert.Contains("Only the Owner", vmAdmin.ErrorMessage);

        // Owner changes role using authoritative server contract { Message = "..." }
        SetupSession(RoleType.Owner);
        var mock = new MockHttpHandler
        {
            Handler = req =>
            {
                if (req.Method == HttpMethod.Put && req.RequestUri!.ToString().Contains("/role", StringComparison.OrdinalIgnoreCase))
                {
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = JsonContent.Create(ApiResponse.Ok<object>(new { Message = "Role of user cashier_01 updated to Manager." }))
                    });
                }

                if (req.Method == HttpMethod.Get && req.RequestUri!.ToString().EndsWith("/api/v1/users", StringComparison.OrdinalIgnoreCase))
                {
                    var reloadedUsers = new List<UserDto>
                    {
                        new(targetUserId, "cashier_01", "Cashier One", RoleType.Manager, true, false)
                    };
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = JsonContent.Create(ApiResponse.Ok<IReadOnlyList<UserDto>>(reloadedUsers))
                    });
                }

                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            }
        };
        var client = new PosApiClient(new HttpClient(mock), "https://127.0.0.1:5001");
        using var vmOwner = new SettingsViewModel(client);
        var resOwner = await vmOwner.ChangeUserRoleAsync(targetUser, RoleType.Manager);
        Assert.True(resOwner);
        Assert.Contains("Role of user cashier_01 updated to Manager.", vmOwner.StatusMessage, StringComparison.Ordinal);
        Assert.Single(vmOwner.Users);
        Assert.Equal(RoleType.Manager, vmOwner.Users[0].Role);
    }

    [Fact]
    public async Task StaffManagement_ActivateAndDeactivate_UseServerMessageContract_AndReloadAuthoritativeData()
    {
        var targetUserId = Guid.NewGuid();
        var targetUser = new UserItemViewModel(new UserDto(targetUserId, "cashier_02", "Cashier Two", RoleType.Cashier, true, false));
        SetupSession(RoleType.Owner);

        bool isUserActiveOnServer = true;
        var mock = new MockHttpHandler
        {
            Handler = req =>
            {
                var url = req.RequestUri!.ToString();
                if (req.Method == HttpMethod.Put && url.EndsWith("/deactivate", StringComparison.OrdinalIgnoreCase))
                {
                    isUserActiveOnServer = false;
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = JsonContent.Create(ApiResponse.Ok<object>(new { Message = "User cashier_02 has been deactivated." }))
                    });
                }

                if (req.Method == HttpMethod.Put && url.EndsWith("/activate", StringComparison.OrdinalIgnoreCase))
                {
                    isUserActiveOnServer = true;
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = JsonContent.Create(ApiResponse.Ok<object>(new { Message = "User cashier_02 has been activated." }))
                    });
                }

                if (req.Method == HttpMethod.Get && url.EndsWith("/api/v1/users", StringComparison.OrdinalIgnoreCase))
                {
                    var list = new List<UserDto>
                    {
                        new(targetUserId, "cashier_02", "Cashier Two", RoleType.Cashier, isUserActiveOnServer, false)
                    };
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = JsonContent.Create(ApiResponse.Ok<IReadOnlyList<UserDto>>(list))
                    });
                }

                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            }
        };

        var client = new PosApiClient(new HttpClient(mock), "https://127.0.0.1:5001");
        using var vm = new SettingsViewModel(client);

        // Deactivate
        await vm.DeactivateUserAsync(targetUser);
        Assert.Contains("User cashier_02 has been deactivated.", vm.StatusMessage, StringComparison.Ordinal);
        Assert.Single(vm.Users);
        Assert.False(vm.Users[0].IsActive);

        // Activate
        await vm.ActivateUserAsync(vm.Users[0]);
        Assert.Contains("User cashier_02 has been activated.", vm.StatusMessage, StringComparison.Ordinal);
        Assert.Single(vm.Users);
        Assert.True(vm.Users[0].IsActive);
    }

    [Fact]
    public async Task StaffManagement_UnlockUser_UsesServerMessageContract()
    {
        var targetUserId = Guid.NewGuid();
        var targetUser = new UserItemViewModel(new UserDto(targetUserId, "cashier_03", "Cashier Three", RoleType.Cashier, true, IsLocked: true));
        SetupSession(RoleType.Owner);

        var mock = new MockHttpHandler
        {
            Handler = req =>
            {
                if (req.Method == HttpMethod.Post && req.RequestUri!.ToString().EndsWith("/unlock", StringComparison.OrdinalIgnoreCase))
                {
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = JsonContent.Create(ApiResponse.Ok<object>(new { Message = "Account for user cashier_03 has been unlocked." }))
                    });
                }
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            }
        };

        var client = new PosApiClient(new HttpClient(mock), "https://127.0.0.1:5001");
        using var vm = new SettingsViewModel(client);

        await vm.UnlockUserAsync(targetUser);
        Assert.False(targetUser.IsLocked);
        Assert.Contains("Account for user cashier_03 has been unlocked.", vm.StatusMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StaffManagement_ResetCredentials_UsesServerMessageContract()
    {
        var targetUserId = Guid.NewGuid();
        var targetUser = new UserItemViewModel(new UserDto(targetUserId, "cashier_04", "Cashier Four", RoleType.Cashier, true, false));
        SetupSession(RoleType.Owner);

        var mock = new MockHttpHandler
        {
            Handler = req =>
            {
                if (req.Method == HttpMethod.Put && req.RequestUri!.ToString().EndsWith("/credentials", StringComparison.OrdinalIgnoreCase))
                {
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = JsonContent.Create(ApiResponse.Ok<object>(new { Message = "Credentials reset for user cashier_04." }))
                    });
                }
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            }
        };

        var client = new PosApiClient(new HttpClient(mock), "https://127.0.0.1:5001");
        using var vm = new SettingsViewModel(client);

        var success = await vm.ResetUserCredentialsAsync(targetUser, "NewStrongPassword1!", "5678");
        Assert.True(success);
        Assert.Contains("Credentials reset for user cashier_04.", vm.StatusMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StaffManagement_FailedOrAmbiguousResponse_IsNotReportedAsSuccess_AndNeverRetried()
    {
        var targetUserId = Guid.NewGuid();
        var targetUser = new UserItemViewModel(new UserDto(targetUserId, "cashier_05", "Cashier Five", RoleType.Cashier, true, false));
        SetupSession(RoleType.Owner);

        int requestCount = 0;
        var mock = new MockHttpHandler
        {
            Handler = req =>
            {
                requestCount++;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest)
                {
                    Content = JsonContent.Create(ApiResponse.Fail<object>("Target user was not found.", "USER_NOT_FOUND"))
                });
            }
        };

        var client = new PosApiClient(new HttpClient(mock), "https://127.0.0.1:5001");
        using var vm = new SettingsViewModel(client);

        var success = await vm.ChangeUserRoleAsync(targetUser, RoleType.Manager);
        Assert.False(success);
        Assert.Contains("Target user was not found.", vm.ErrorMessage, StringComparison.Ordinal);
        Assert.NotEqual("Role for user 'cashier_05' changed to Manager.", vm.StatusMessage);
        Assert.Equal(1, requestCount); // Exactly 1 request: no automated retry
    }

    [Fact]
    public async Task PosApiClient_StaffMethods_DirectDeserializationMatchesServerContract()
    {
        var targetUserId = Guid.NewGuid();
        var mock = new MockHttpHandler
        {
            Handler = req =>
            {
                var url = req.RequestUri!.ToString();
                if (url.Contains("/activate", StringComparison.OrdinalIgnoreCase) ||
                    url.Contains("/deactivate", StringComparison.OrdinalIgnoreCase) ||
                    url.Contains("/role", StringComparison.OrdinalIgnoreCase) ||
                    url.Contains("/unlock", StringComparison.OrdinalIgnoreCase) ||
                    url.Contains("/credentials", StringComparison.OrdinalIgnoreCase))
                {
                    var json = "{\"success\":true,\"data\":{\"message\":\"Operation executed successfully.\"},\"errorMessage\":null,\"errorCode\":null,\"correlationId\":null,\"timestampUtc\":\"2026-10-23T10:00:00Z\"}";
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
                    });
                }
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            }
        };

        var client = new PosApiClient(new HttpClient(mock), "https://127.0.0.1:5001");

        var actRes = await client.ActivateUserAsync(targetUserId);
        Assert.True(actRes.Success);
        Assert.Equal("Operation executed successfully.", actRes.Data?.Message);

        var deactRes = await client.DeactivateUserAsync(targetUserId);
        Assert.True(deactRes.Success);
        Assert.Equal("Operation executed successfully.", deactRes.Data?.Message);

        var unlockRes = await client.UnlockUserAsync(targetUserId);
        Assert.True(unlockRes.Success);
        Assert.Equal("Operation executed successfully.", unlockRes.Data?.Message);

        var credRes = await client.ResetUserCredentialsAsync(targetUserId, new ResetUserCredentialsRequest("pass", "1234"));
        Assert.True(credRes.Success);
        Assert.Equal("Operation executed successfully.", credRes.Data?.Message);

        var roleRes = await client.ChangeUserRoleAsync(targetUserId, new UpdateUserRoleRequest(RoleType.Admin));
        Assert.True(roleRes.Success);
        Assert.Equal("Operation executed successfully.", roleRes.Data?.Message);
    }

    // ==========================================
    // 7. Backups Tests
    // ==========================================

    [Fact]
    public async Task BackupsManagement_CreateAndLoadBackups()
    {
        SetupSession(RoleType.Admin);
        var backupId = Guid.NewGuid();
        var mock = new MockHttpHandler
        {
            Handler = req =>
            {
                if (req.Method == HttpMethod.Get)
                {
                    var list = new List<BackupRecordDto>
                    {
                        new()
                        {
                            BackupOperationId = backupId,
                            FileName = "backup-2026-10-19.bak",
                            SizeBytes = 15728640, // 15 MB
                            Status = "Completed",
                            CreatedAtUtc = DateTime.UtcNow,
                            TriggeredByUserName = "admin"
                        }
                    };
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = JsonContent.Create(ApiResponse.Ok<IReadOnlyList<BackupRecordDto>>(list))
                    });
                }

                if (req.Method == HttpMethod.Post)
                {
                    var created = new BackupRecordDto
                    {
                        BackupOperationId = Guid.NewGuid(),
                        FileName = "backup-new.bak",
                        SizeBytes = 1048576,
                        Status = "Completed",
                        CreatedAtUtc = DateTime.UtcNow,
                        TriggeredByUserName = "admin"
                    };
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = JsonContent.Create(ApiResponse.Ok(created))
                    });
                }

                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            }
        };

        var client = new PosApiClient(new HttpClient(mock), "https://127.0.0.1:5001");
        using var vm = new SettingsViewModel(client);

        await vm.LoadBackupsAsync();
        Assert.Single(vm.Backups);
        Assert.Equal("15.00 MB", vm.Backups[0].FormattedSize);
        Assert.Equal("Completed", vm.Backups[0].Status);

        vm.NewBackupReason = "Manual test snapshot";
        var createRes = await vm.CreateBackupAsync();
        Assert.True(createRes);
        Assert.Contains("backup-new.bak", vm.StatusMessage);
    }
}
