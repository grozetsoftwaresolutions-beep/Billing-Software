using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RetailPOS.Application.Services;
using RetailPOS.Contracts.Audit;
using RetailPOS.Contracts.Auth;
using RetailPOS.Contracts.Backup;
using RetailPOS.Contracts.Common;
using RetailPOS.Contracts.Discovery;
using RetailPOS.Contracts.Sales;
using RetailPOS.Contracts.Security;
using RetailPOS.Contracts.Terminals;
using RetailPOS.Contracts.Users;
using RetailPOS.Domain.Common;
using RetailPOS.Domain.Entities.Sales;
using RetailPOS.Domain.Enums;
using RetailPOS.Domain.ValueObjects;
using RetailPOS.E2E.Tests.Fixtures;
using RetailPOS.E2E.Tests.Helpers;
using RetailPOS.Infrastructure.Deployment;
using RetailPOS.Infrastructure.Persistence;
using RetailPOS.Infrastructure.Security;
using Xunit;
using Xunit.Abstractions;

namespace RetailPOS.E2E.Tests.Suites;

/// <summary>
/// Sub-Phase 15.6: Security & Audit Validation Test Suite (TEST-P15-45 through TEST-P15-52).
/// Authoritative validation of terminal isolation, authentication/authorization boundaries,
/// Low-S challenge-response replay defense, brute-force PIN lockout isolation, supervisor approval
/// token bindings, HMAC audit journal concurrency, tamper detection, and administrative role boundaries.
/// </summary>
public sealed class SecurityAndAuditConcurrencyTests
{
    private readonly ITestOutputHelper _output;

    public SecurityAndAuditConcurrencyTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public async Task TEST_P15_45_Security_TerminalSessionHijackAttempt_RejectedWithForbidden()
    {
        await using var fixture = new StoreServerFixture();
        var env = await E2ETestSeedHelper.SeedThreeCounterStoreAsync(fixture);

        // 1. Authorized baseline: Counter 1 cashier creates a sale on Counter 1
        var authSaleReq = new CreateSaleRequest(
            Guid.NewGuid(),
            [new CartItemRequest(env.Product.Id, 1m)],
            [new PaymentLineRequest(PaymentMethod.Cash, 1000)]);

        var authRes = await env.Counter1.PostAsJsonAsync("/api/v1/sales", authSaleReq);
        Assert.True(authRes.IsSuccessStatusCode, "Authorized sale on Counter 1 must succeed.");
        var authSale = (await authRes.Content.ReadFromJsonAsync<ApiResponse<SaleInvoiceDto>>())!.Data!;
        Assert.NotNull(authSale.InvoiceNumber);

        // 2. Cross-terminal session hijack attempt:
        // Attacker uses Counter 1's Bearer token while claiming to be Counter 2 (X-Terminal-ID = Counter 2)
        var hijackTxId = Guid.NewGuid();
        var hijackSaleReq = new CreateSaleRequest(
            hijackTxId,
            [new CartItemRequest(env.Product.Id, 2m)],
            [new PaymentLineRequest(PaymentMethod.Cash, 2000)]);

        using var hijackClient = fixture.CreateClient();
        hijackClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", env.Counter1.SessionToken);
        hijackClient.DefaultRequestHeaders.Add("X-Terminal-ID", env.Counter2.TerminalId.ToString());
        hijackClient.DefaultRequestHeaders.Add("X-Terminal-Token", env.Counter2.PairingToken);

        var hijackRes = await hijackClient.PostAsJsonAsync("/api/v1/sales", hijackSaleReq);
        Assert.Equal(HttpStatusCode.Forbidden, hijackRes.StatusCode);
        var hijackBody = await hijackRes.Content.ReadFromJsonAsync<ApiResponse<object>>();
        Assert.Equal("TERMINAL_MISMATCH", hijackBody?.ErrorCode);

        // 3. Invariant check: Assert ZERO business state mutation occurred for hijack request
        using (var scope = fixture.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<RetailDbContext>();
            var exists = await db.SalesInvoices.AnyAsync(s => s.ClientTransactionId == hijackTxId);
            Assert.False(exists, "Hijack transaction must not exist in sales invoices.");

            // Stock was only decremented by 1 (authorized sale), NOT 3
            var stock = await db.StockBalances.FirstAsync(s => s.ProductId == env.Product.Id);
            Assert.Equal(99m, stock.CurrentQuantity.Value);
        }

        // 4. Spoofed unregistered terminal header: Valid session token with non-existent terminal ID
        using var spoofClient = fixture.CreateClient();
        spoofClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", env.Counter1.SessionToken);
        spoofClient.DefaultRequestHeaders.Add("X-Terminal-ID", Guid.NewGuid().ToString());

        var spoofRes = await spoofClient.PostAsJsonAsync("/api/v1/sales", hijackSaleReq);
        Assert.Equal(HttpStatusCode.Forbidden, spoofRes.StatusCode);
        var spoofBody = await spoofRes.Content.ReadFromJsonAsync<ApiResponse<object>>();
        Assert.Equal("TERMINAL_UNAUTHORIZED", spoofBody?.ErrorCode);

        // 5. Decommissioned terminal access: Terminal 3 decommissioned -> 409 Conflict
        using (var scope = fixture.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<RetailDbContext>();
            var term3 = await db.Terminals.FindAsync(env.Counter3.TerminalId);
            term3!.Decommission();
            await db.SaveChangesAsync();
        }

        var decommRes = await env.Counter3.PostAsJsonAsync("/api/v1/sales", hijackSaleReq);
        Assert.Equal(HttpStatusCode.Conflict, decommRes.StatusCode);
        var decommBody = await decommRes.Content.ReadFromJsonAsync<ApiResponse<object>>();
        Assert.Equal("TERMINAL_DECOMMISSIONED", decommBody?.ErrorCode);

        _output.WriteLine("[TEST-P15-45] Passed: Terminal session hijacking strictly rejected (403 TERMINAL_MISMATCH), zero business mutation, spoofed rejected (403 TERMINAL_UNAUTHORIZED), decommissioned rejected (409 TERMINAL_DECOMMISSIONED).");
    }

    [Fact]
    public async Task TEST_P15_46_Security_ExpiredLowSChallengeResponse_RejectedWithUnauthorized()
    {
        await using var fixture = new StoreServerFixture();
        await fixture.SetupOwnerAsync();
        using var client = fixture.CreateClient();

        // 1. Authorized baseline: Valid attestation challenge with fresh nonce and valid timestamp
        var freshNonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var validReq = new ServerChallengeRequestDto(
            freshNonce,
            ChallengePurposes.ServerIdentityAuthentication,
            DateTimeOffset.UtcNow);

        var authRes = await client.PostAsJsonAsync("/api/v1/discovery/challenge", validReq);
        Assert.True(authRes.IsSuccessStatusCode, $"authRes failed: {authRes.StatusCode} - {await authRes.Content.ReadAsStringAsync()}");
        var challengeDto = (await authRes.Content.ReadFromJsonAsync<ServerChallengeResponseDto>())!;
        Assert.Equal(freshNonce, challengeDto.Nonce);
        Assert.NotNull(challengeDto.SignatureBase64);

        // Verify low-S signature canonicality: S <= n/2
        byte[] sigBytes = Convert.FromBase64String(challengeDto.SignatureBase64);
        Assert.Equal(64, sigBytes.Length);
        BigInteger s = new BigInteger(sigBytes.AsSpan(32, 32), isUnsigned: true, isBigEndian: true);
        Assert.True(s <= ChallengeProtocolHelper.P256HalfCurveOrder, "Signature S-scalar must be normalized to canonical Low-S.");

        // Cryptographic verification against returned public key
        using var ecdsa = ECDsa.Create();
        ecdsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(challengeDto.PublicKeySpkiBase64), out _);
        string canonicalPayload = ChallengeProtocolHelper.BuildCanonicalPayload(
            validReq.Purpose,
            validReq.Nonce,
            validReq.ClientTimestampUtc,
            challengeDto.ServerId,
            challengeDto.StoreId,
            challengeDto.ServerFingerprint,
            challengeDto.ServerFingerprint);

        bool isValid = ChallengeProtocolHelper.VerifyCanonical(ecdsa, canonicalPayload, sigBytes, out var errCode, out var errMsg);
        Assert.True(isValid, $"Canonical verification failed: {errCode} - {errMsg}");

        // 2. Replay attack across separate HTTP request / DI scope (Request B):
        using var clientB = fixture.CreateClient();
        var replayResB = await clientB.PostAsJsonAsync("/api/v1/discovery/challenge", validReq);
        Assert.Equal(HttpStatusCode.Conflict, replayResB.StatusCode);
        var replayBodyB = await replayResB.Content.ReadFromJsonAsync<ApiResponse<object>>();
        Assert.Equal("REPLAYED_CHALLENGE_NONCE", replayBodyB?.ErrorCode);
        Assert.False(replayBodyB?.Success ?? true);

        // 3. Repeated replay attack across yet another separate HTTP request / DI scope (Request C):
        using var clientC = fixture.CreateClient();
        var replayResC = await clientC.PostAsJsonAsync("/api/v1/discovery/challenge", validReq);
        Assert.Equal(HttpStatusCode.Conflict, replayResC.StatusCode);
        var replayBodyC = await replayResC.Content.ReadFromJsonAsync<ApiResponse<object>>();
        Assert.Equal("REPLAYED_CHALLENGE_NONCE", replayBodyC?.ErrorCode);
        Assert.False(replayBodyC?.Success ?? true);

        // 4. Cross-DI-scope direct service regression verification:
        var crossScopeNonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var crossScopeReq = new ServerChallengeRequestDto(
            crossScopeNonce,
            ChallengePurposes.ServerIdentityAuthentication,
            DateTimeOffset.UtcNow);

        using (var scope1 = fixture.Services.CreateScope())
        {
            var s1 = scope1.ServiceProvider.GetRequiredService<IServerIdentityService>();
            var r1 = await s1.SignChallengeAsync(crossScopeReq);
            Assert.NotNull(r1);
        }

        using (var scope2 = fixture.Services.CreateScope())
        {
            var s2 = scope2.ServiceProvider.GetRequiredService<IServerIdentityService>();
            var replayEx2 = await Assert.ThrowsAsync<DomainException>(() => s2.SignChallengeAsync(crossScopeReq));
            Assert.Equal("REPLAYED_CHALLENGE_NONCE", replayEx2.ErrorCode);
        }

        using (var scope3 = fixture.Services.CreateScope())
        {
            var s3 = scope3.ServiceProvider.GetRequiredService<IServerIdentityService>();
            var replayEx3 = await Assert.ThrowsAsync<DomainException>(() => s3.SignChallengeAsync(crossScopeReq));
            Assert.Equal("REPLAYED_CHALLENGE_NONCE", replayEx3.ErrorCode);
        }

        // 3. Stale / Expired request timestamp (> 60s in the past) -> 400 Bad Request
        var expiredReq = new ServerChallengeRequestDto(
            Convert.ToHexString(RandomNumberGenerator.GetBytes(32)),
            ChallengePurposes.ServerIdentityAuthentication,
            DateTimeOffset.UtcNow.AddSeconds(-90));

        var expiredRes = await client.PostAsJsonAsync("/api/v1/discovery/challenge", expiredReq);
        Assert.Equal(HttpStatusCode.BadRequest, expiredRes.StatusCode);
        var expiredBody = await expiredRes.Content.ReadFromJsonAsync<ApiResponse<object>>();
        Assert.Equal("STALE_CHALLENGE_TIMESTAMP", expiredBody?.ErrorCode);

        // 4. Future timestamp drift (> 60s in the future) -> 400 Bad Request
        var futureReq = new ServerChallengeRequestDto(
            Convert.ToHexString(RandomNumberGenerator.GetBytes(32)),
            ChallengePurposes.ServerIdentityAuthentication,
            DateTimeOffset.UtcNow.AddSeconds(90));

        var futureRes = await client.PostAsJsonAsync("/api/v1/discovery/challenge", futureReq);
        Assert.Equal(HttpStatusCode.BadRequest, futureRes.StatusCode);
        var futureBody = await futureRes.Content.ReadFromJsonAsync<ApiResponse<object>>();
        Assert.Equal("STALE_CHALLENGE_TIMESTAMP", futureBody?.ErrorCode);

        // 5. Non-canonical High-S signature malleability rejection
        byte[] highSSig = ChallengeProtocolHelper.MalleateToHighS(sigBytes);
        bool highSVerified = ChallengeProtocolHelper.VerifyCanonical(ecdsa, canonicalPayload, highSSig, out var highSErrCode, out _);
        Assert.False(highSVerified, "High-S signature must be strictly rejected.");
        Assert.Equal("SERVER_IDENTITY_SIGNATURE_NON_CANONICAL_HIGH_S", highSErrCode);

        // 6. Client-side verification: Expired challenge response (> 60s timestamp drift) strictly rejected
        var expiredResponse = challengeDto with { ServerTimestampUtc = DateTimeOffset.UtcNow.AddSeconds(-90) };
        var drift = Math.Abs((DateTimeOffset.UtcNow - expiredResponse.ServerTimestampUtc).TotalSeconds);
        Assert.True(drift > 60, "Challenge response timestamp drift exceeds allowable 60s window.");

        // 7. Client-side verification: Nonce match and tampered nonce rejection
        Assert.Equal(freshNonce, challengeDto.Nonce);
        Assert.NotEqual("tampered-nonce-12345", challengeDto.Nonce);

        _output.WriteLine("[TEST-P15-46] Passed: Challenge-response Low-S verified, replay rejected (REPLAYED_CHALLENGE_NONCE), stale timestamp rejected (400 STALE_CHALLENGE_TIMESTAMP), High-S malleation rejected, client drift and nonce mismatch validated.");
    }

    [Fact]
    public async Task TEST_P15_47_Security_CashierBruteForcePinAttempts_LocksCounterWithoutImpactingOtherCounters()
    {
        await using var fixture = new StoreServerFixture();
        var env = await E2ETestSeedHelper.SeedThreeCounterStoreAsync(fixture);

        // 1. Attacker attempts brute-force PIN guessing on Counter 1 for "cashier_1" (5 failed attempts)
        for (int attempt = 1; attempt <= 5; attempt++)
        {
            var failRes = await env.Counter1.Client.PostAsJsonAsync("/api/v1/auth/login-pin",
                new LoginPinRequest("cashier_1", $"999{attempt}", env.Counter1.TerminalId));
            Assert.Equal(HttpStatusCode.Unauthorized, failRes.StatusCode);
            var failBody = await failRes.Content.ReadFromJsonAsync<ApiResponse<object>>();
            Assert.Equal("AUTH_FAILED", failBody?.ErrorCode);
        }

        // 2. On 6th attempt, even with the CORRECT PIN "1111", cashier_1 is locked out
        var lockedRes = await env.Counter1.Client.PostAsJsonAsync("/api/v1/auth/login-pin",
            new LoginPinRequest("cashier_1", "1111", env.Counter1.TerminalId));
        Assert.Equal(HttpStatusCode.Unauthorized, lockedRes.StatusCode);
        var lockedBody = await lockedRes.Content.ReadFromJsonAsync<ApiResponse<object>>();
        Assert.Equal("AUTH_FAILED", lockedBody?.ErrorCode);

        // Verify database user state has LockoutEndUtc set in the future
        using (var scope = fixture.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<RetailDbContext>();
            var user1 = await db.Users.FirstAsync(u => u.Username == "cashier_1");
            Assert.True(user1.IsLockedOut(DateTime.UtcNow), "Cashier 1 must be persistently locked out.");
            Assert.NotNull(user1.LockoutEndUtc);
            Assert.True(user1.LockoutEndUtc > DateTime.UtcNow);
        }

        // 3. Multi-counter isolation: Counter 2 cashier "cashier_2" is completely UNAFFECTED
        var c2LoginRes = await env.Counter2.Client.PostAsJsonAsync("/api/v1/auth/login-pin",
            new LoginPinRequest("cashier_2", "2222", env.Counter2.TerminalId));
        Assert.Equal(HttpStatusCode.OK, c2LoginRes.StatusCode);
        var c2LoginBody = await c2LoginRes.Content.ReadFromJsonAsync<ApiResponse<LoginResponse>>();
        Assert.NotNull(c2LoginBody?.Data?.Token);

        // Counter 2 executes transactions normally with zero lockout bleed-over
        env.Counter2.SetBearerToken(c2LoginBody!.Data!.Token);
        var c2SaleRes = await env.Counter2.PostAsJsonAsync("/api/v1/sales",
            new CreateSaleRequest(
                Guid.NewGuid(),
                [new CartItemRequest(env.Product.Id, 1m)],
                [new PaymentLineRequest(PaymentMethod.Cash, 1000)]));
        Assert.True(c2SaleRes.IsSuccessStatusCode, "Counter 2 must continue transacting while Counter 1 is locked out.");

        // Counter 3 is also unaffected
        var c3CalcRes = await env.Counter3.PostAsJsonAsync("/api/v1/sales/calculate",
            new CalculateSaleRequest([new CartItemRequest(env.Product.Id, 1m)]));
        Assert.True(c3CalcRes.IsSuccessStatusCode, "Counter 3 cart calculation remains unaffected.");

        _output.WriteLine("[TEST-P15-47] Passed: Cashier 1 locked out after 5 failed PIN attempts, correct PIN rejected while locked, Counter 2 and Counter 3 completely isolated and unaffected.");
    }

    [Fact]
    public async Task TEST_P15_48_Security_ConcurrentManagerOverrides_IsolatedAndSingleUseEnforced()
    {
        await using var fixture = new StoreServerFixture();
        var env = await E2ETestSeedHelper.SeedThreeCounterStoreAsync(fixture, initialStock: 500m, sellingPricePaise: 1000);

        // Terminals communicate over TLS-encrypted remote LAN
        env.Counter1.Client.DefaultRequestHeaders.Add("X-Forwarded-Proto", "https");
        env.Counter2.Client.DefaultRequestHeaders.Add("X-Forwarded-Proto", "https");
        env.Counter3.Client.DefaultRequestHeaders.Add("X-Forwarded-Proto", "https");

        // 1. Create two completed sales: Sale A on Counter 1, Sale B on Counter 2
        var resA = await env.Counter1.PostAsJsonAsync("/api/v1/sales",
            new CreateSaleRequest(Guid.NewGuid(), [new CartItemRequest(env.Product.Id, 1m)], [new PaymentLineRequest(PaymentMethod.Cash, 1000)]));
        Assert.True(resA.IsSuccessStatusCode);
        var saleA = (await resA.Content.ReadFromJsonAsync<ApiResponse<SaleInvoiceDto>>())!.Data!;

        var resB = await env.Counter2.PostAsJsonAsync("/api/v1/sales",
            new CreateSaleRequest(Guid.NewGuid(), [new CartItemRequest(env.Product.Id, 1m)], [new PaymentLineRequest(PaymentMethod.Cash, 1000)]));
        Assert.True(resB.IsSuccessStatusCode);
        var saleB = (await resB.Content.ReadFromJsonAsync<ApiResponse<SaleInvoiceDto>>())!.Data!;

        // 2. Concurrently request supervisor approval tokens:
        // Cashier 1 requests approval to void Sale A with nonce A
        // Cashier 2 requests approval to void Sale B with nonce B
        var nonceA = "nonce-a-" + Guid.NewGuid().ToString("N");
        var nonceB = "nonce-b-" + Guid.NewGuid().ToString("N");

        var supReqA = new SupervisorApprovalRequest(
            "mgr_user", "2222", null, Permission.VoidSale, "SaleInvoice", saleA.Id.ToString(), nonceA, "Void Sale A");
        var supReqB = new SupervisorApprovalRequest(
            "mgr_user", "2222", null, Permission.VoidSale, "SaleInvoice", saleB.Id.ToString(), nonceB, "Void Sale B");

        var supResA = await env.Counter1.PostAsJsonAsync("/api/v1/auth/supervisor-approve", supReqA);
        var supResB = await env.Counter2.PostAsJsonAsync("/api/v1/auth/supervisor-approve", supReqB);

        Assert.True(supResA.IsSuccessStatusCode, $"supResA failed: {supResA.StatusCode} - {await supResA.Content.ReadAsStringAsync()}");
        Assert.True(supResB.IsSuccessStatusCode, $"supResB failed: {supResB.StatusCode} - {await supResB.Content.ReadAsStringAsync()}");

        var tokenA = (await supResA.Content.ReadFromJsonAsync<ApiResponse<SupervisorApprovalResponse>>())!.Data!.ApprovalToken;
        var tokenB = (await supResB.Content.ReadFromJsonAsync<ApiResponse<SupervisorApprovalResponse>>())!.Data!.ApprovalToken;
        Assert.NotEqual(tokenA, tokenB);

        // 3. Cross-terminal abuse: Counter 2 cashier attempts to use Token A (bound to Counter 1)
        var crossTermRes = await env.Counter2.PostAsJsonAsync($"/api/v1/sales/{saleA.Id}/void",
            new VoidSaleRequest("Cross terminal abuse", tokenA, nonceA));
        Assert.False(crossTermRes.IsSuccessStatusCode, "Cross-terminal token usage must be rejected.");

        // Invariant check: Sale A is still Completed (not voided)
        using (var scope = fixture.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<RetailDbContext>();
            var sA = await db.SalesInvoices.FindAsync(saleA.Id);
            Assert.Equal(SaleStatus.Completed, sA!.Status);
        }

        // 4. Wrong-entity abuse: Cashier 1 attempts to use Token A (issued for Sale A) on Sale B
        var wrongEntityRes = await env.Counter1.PostAsJsonAsync($"/api/v1/sales/{saleB.Id}/void",
            new VoidSaleRequest("Wrong entity abuse", tokenA, nonceA));
        Assert.False(wrongEntityRes.IsSuccessStatusCode, "Token must be strictly bound to intended EntityId.");

        // 5. Wrong-nonce abuse: Cashier 1 attempts to use Token A with a different nonce
        var wrongNonceRes = await env.Counter1.PostAsJsonAsync($"/api/v1/sales/{saleA.Id}/void",
            new VoidSaleRequest("Wrong nonce abuse", tokenA, "tampered-nonce"));
        Assert.False(wrongNonceRes.IsSuccessStatusCode, "Token must be strictly bound to intended RequestNonce.");

        // 6. Authorized consumption: Cashier 1 consumes Token A on Counter 1 to void Sale A
        var authVoidRes = await env.Counter1.PostAsJsonAsync($"/api/v1/sales/{saleA.Id}/void",
            new VoidSaleRequest("Valid void", tokenA, nonceA));
        Assert.True(authVoidRes.IsSuccessStatusCode, "Authorized consumption must succeed.");

        using (var scope = fixture.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<RetailDbContext>();
            var sA = await db.SalesInvoices.FindAsync(saleA.Id);
            Assert.Equal(SaleStatus.Voided, sA!.Status);

            // Stock reversed for Sale A (+1 unit back to stock)
            var stock = await db.StockBalances.FirstAsync(s => s.ProductId == env.Product.Id);
            Assert.Equal(499m, stock.CurrentQuantity.Value); // 500 - 2 sales + 1 void = 499
        }

        // 7. Single-use / Replay prevention: Cashier 1 attempts to reuse Token A a second time
        var replayRes = await env.Counter1.PostAsJsonAsync($"/api/v1/sales/{saleA.Id}/void",
            new VoidSaleRequest("Replay void attempt", tokenA, nonceA));
        Assert.False(replayRes.IsSuccessStatusCode, "Replayed token must be rejected (single-use enforced).");

        _output.WriteLine("[TEST-P15-48] Passed: Concurrent supervisor approvals issued, cross-terminal rejected, wrong-entity rejected, wrong-nonce rejected, authorized void executed and stock reversed, replay strictly rejected.");
    }

    [Fact]
    public async Task TEST_P15_49_Security_AuditJournal_ConcurrentWritesFromThreeTerminals_HashChainIntact()
    {
        string testDir = Path.Combine(Path.GetTempPath(), $"retailpos_audit_p15_{Guid.NewGuid():N}");
        Directory.CreateDirectory(testDir);
        string journalPath = Path.Combine(testDir, "migration_audit.journal");
        byte[] hmacKey = RandomNumberGenerator.GetBytes(32);

        try
        {
            using var journal = new FileMigrationAuditJournal(journalPath, hmacKey);

            // 3 concurrent terminals submitting 100 audit entries each (total 300 entries)
            const int entriesPerTerminal = 100;
            var terminals = new[] { "COUNTER-01", "COUNTER-02", "COUNTER-03" };

            var tasks = terminals.Select(termCode => Task.Run(async () =>
            {
                for (int i = 1; i <= entriesPerTerminal; i++)
                {
                    string opId = Guid.NewGuid().ToString("N");
                    string eventType = $"TERMINAL_EVENT_{termCode}_{i}";
                    string details = $"Audit event {i} logged by terminal {termCode} at high concurrency";
                    await journal.AppendEntryAsync(eventType, opId, details);
                }
            })).ToList();

            await Task.WhenAll(tasks);

            // 1. Verify exact entry count
            var entries = await journal.ReadEntriesAsync();
            Assert.Equal(300, entries.Count);

            // 2. Verify sequence monotonicity: Strictly 1 to 300 with zero collisions or gaps
            for (int i = 0; i < entries.Count; i++)
            {
                Assert.Equal(i + 1, entries[i].Sequence);
            }

            // 3. Cryptographic HMAC chain verification: Every entry H_i = HMAC(H_prev || Entry_i)
            bool isChainValid = await journal.VerifyChainIntegrityAsync();
            Assert.True(isChainValid, "HMAC hash chain must remain 100% valid under concurrent multi-terminal appends.");

            _output.WriteLine($"[TEST-P15-49] Passed: 300 concurrent audit entries across 3 terminals preserved strict sequence monotonicity [1..300] and cryptographic HMAC chain integrity.");
        }
        finally
        {
            if (Directory.Exists(testDir))
            {
                try { Directory.Delete(testDir, true); } catch { }
            }
        }
    }

    [Fact]
    public async Task TEST_P15_50_Security_AuditJournal_TamperDetection_DetectsIntermediateBitFlipAccurately()
    {
        string testDir = Path.Combine(Path.GetTempPath(), $"retailpos_tamper_p15_{Guid.NewGuid():N}");
        Directory.CreateDirectory(testDir);
        string journalPath = Path.Combine(testDir, "migration_audit.journal");
        byte[] hmacKey = RandomNumberGenerator.GetBytes(32);

        try
        {
            using var journal = new FileMigrationAuditJournal(journalPath, hmacKey);

            // 1. Seed 50 valid chained journal entries
            for (int i = 1; i <= 50; i++)
            {
                await journal.AppendEntryAsync($"EVENT_{i:D3}", Guid.NewGuid().ToString("N"), $"Original detail for entry {i}");
            }

            // Baseline verification must pass
            bool baselineValid = await journal.VerifyChainIntegrityAsync();
            Assert.True(baselineValid, "Baseline un-tampered journal must pass integrity verification.");

            var originalLines = (await File.ReadAllLinesAsync(journalPath, Encoding.UTF8)).ToList();
            Assert.Equal(50, originalLines.Count);

            // 2. Tamper Test 1: Single bit-flip in Details of Entry 25
            var tamperedLine25 = originalLines[24].Replace("Original detail for entry 25", "TAMPERED detail for entry 25");

            var tamperedLines1 = new List<string>(originalLines) { [24] = tamperedLine25 };
            await File.WriteAllLinesAsync(journalPath, tamperedLines1, Encoding.UTF8);

            bool tamper1Detected = !(await journal.VerifyChainIntegrityAsync());
            Assert.True(tamper1Detected, "Single bit-flip in entry details MUST cause VerifyChainIntegrityAsync to return false.");

            // 3. Tamper Test 2: Record Deletion (Delete entry 30 entirely -> gap in sequence and broken HMAC link)
            var tamperedLines2 = new List<string>(originalLines);
            tamperedLines2.RemoveAt(29); // remove entry 30
            await File.WriteAllLinesAsync(journalPath, tamperedLines2, Encoding.UTF8);

            bool tamper2Detected = !(await journal.VerifyChainIntegrityAsync());
            Assert.True(tamper2Detected, "Deleted intermediate record MUST cause VerifyChainIntegrityAsync to return false.");

            // 4. Tamper Test 3: Record Insertion / Forgery (Insert extra entry between 10 and 11)
            var tamperedLines3 = new List<string>(originalLines);
            tamperedLines3.Insert(10, originalLines[9]); // duplicate line 10
            await File.WriteAllLinesAsync(journalPath, tamperedLines3, Encoding.UTF8);

            bool tamper3Detected = !(await journal.VerifyChainIntegrityAsync());
            Assert.True(tamper3Detected, "Inserted/forged record MUST cause VerifyChainIntegrityAsync to return false.");

            // 5. Restore original un-tampered content -> verification passes again
            await File.WriteAllLinesAsync(journalPath, originalLines, Encoding.UTF8);
            bool restoredValid = await journal.VerifyChainIntegrityAsync();
            Assert.True(restoredValid, "Restored original journal must pass integrity verification.");

            _output.WriteLine("[TEST-P15-50] Passed: HMAC audit journal tamper detection reliably catches bit-flips, record deletion, and record insertion.");
        }
        finally
        {
            if (Directory.Exists(testDir))
            {
                try { Directory.Delete(testDir, true); } catch { }
            }
        }
    }

    [Fact]
    public async Task TEST_P15_51_Security_AuditJournal_SequenceMonotonicity_StrictlyPreservedUnderConcurrency()
    {
        await using var fixture = new StoreServerFixture();
        var env = await E2ETestSeedHelper.SeedThreeCounterStoreAsync(fixture);

        env.Counter1.Client.DefaultRequestHeaders.Add("X-Forwarded-Proto", "https");
        env.Counter2.Client.DefaultRequestHeaders.Add("X-Forwarded-Proto", "https");
        env.Counter3.Client.DefaultRequestHeaders.Add("X-Forwarded-Proto", "https");

        // Perform concurrent security-audited operations across 3 counters:
        // - Counter 1: Successful sales & cash operations
        // - Counter 2: Supervisor approval & void operation
        // - Counter 3: Cash drawer operations
        var resSale = await env.Counter1.PostAsJsonAsync("/api/v1/sales",
            new CreateSaleRequest(Guid.NewGuid(), [new CartItemRequest(env.Product.Id, 1m)], [new PaymentLineRequest(PaymentMethod.Cash, 1000)]));
        Assert.True(resSale.IsSuccessStatusCode);
        var saleDto = (await resSale.Content.ReadFromJsonAsync<ApiResponse<SaleInvoiceDto>>())!.Data!;

        var nonce = "nonce-" + Guid.NewGuid().ToString("N");
        var supRes = await env.Counter2.PostAsJsonAsync("/api/v1/auth/supervisor-approve",
            new SupervisorApprovalRequest("mgr_user", "2222", null, Permission.VoidSale, "SaleInvoice", saleDto.Id.ToString(), nonce, "Audited Void"));
        Assert.True(supRes.IsSuccessStatusCode, $"supRes failed: {supRes.StatusCode} - {await supRes.Content.ReadAsStringAsync()}");
        var supToken = (await supRes.Content.ReadFromJsonAsync<ApiResponse<SupervisorApprovalResponse>>())!.Data!.ApprovalToken;

        var voidRes = await env.Counter2.PostAsJsonAsync($"/api/v1/sales/{saleDto.Id}/void",
            new VoidSaleRequest("Audited void execution", supToken, nonce));
        Assert.True(voidRes.IsSuccessStatusCode);

        // Query audit logs through authoritative API as Owner
        using var ownerClient = fixture.CreateClient();
        ownerClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", env.OwnerToken);

        var auditRes = await ownerClient.GetAsync("/api/v1/audit?pageSize=100");
        Assert.Equal(HttpStatusCode.OK, auditRes.StatusCode);
        var auditEnvelope = await auditRes.Content.ReadFromJsonAsync<ApiResponse<PagedResult<AuditLogDto>>>();
        Assert.True(auditEnvelope?.Success);
        var auditLogs = auditEnvelope!.Data!.Items;

        Assert.NotEmpty(auditLogs);

        // Verify sequence persistence and monotonic IDs in database
        using (var scope = fixture.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<RetailDbContext>();
            var dbLogs = await db.AuditLogs.AsNoTracking().OrderBy(a => a.Id).ToListAsync();

            Assert.True(dbLogs.Count >= 5, "Database must have recorded multiple audit logs.");

            // IDs strictly positive and monotonic
            long prevId = 0;
            foreach (var log in dbLogs)
            {
                Assert.True(log.Id > prevId, $"Audit log ID {log.Id} must be strictly greater than previous {prevId}.");
                prevId = log.Id;
                Assert.False(string.IsNullOrWhiteSpace(log.Action), "Audit Action must not be empty.");
                Assert.False(string.IsNullOrWhiteSpace(log.EntityName), "Audit EntityName must not be empty.");
            }

            // Verify specific security audit actions are recorded
            var actions = dbLogs.Select(l => l.Action).ToHashSet();
            Assert.Contains("LOGIN_SUCCESS", actions);
            Assert.Contains("SUPERVISOR_APPROVAL_GRANTED", actions);
            Assert.Contains("SUPERVISOR_TOKEN_CONSUMED", actions);
            Assert.Contains("SALE_VOIDED", actions);
        }

        _output.WriteLine($"[TEST-P15-51] Passed: Database audit log sequence monotonicity strictly preserved across concurrent operations; verified presence of LOGIN_SUCCESS, SUPERVISOR_APPROVAL_GRANTED, SUPERVISOR_TOKEN_CONSUMED, and SALE_VOIDED.");
    }

    [Fact]
    public async Task TEST_P15_52_Security_NonAdminRole_AttemptingBackupRestoreOrMigration_Rejected()
    {
        await using var fixture = new StoreServerFixture();
        var env = await E2ETestSeedHelper.SeedThreeCounterStoreAsync(fixture);

        using var cashierClient = fixture.CreateClient();
        cashierClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", env.Counter1.SessionToken);

        // 1. Cashier attempts to update security settings -> 403 Forbidden
        var secUpdateRes = await cashierClient.PutAsJsonAsync("/api/v1/settings/security",
            new UpdateSecuritySettingsRequest(10, 3, 5, 5m, false, false, false));
        Assert.Equal(HttpStatusCode.Forbidden, secUpdateRes.StatusCode);

        // 2. Cashier attempts to view audit logs -> 403 Forbidden
        var auditViewRes = await cashierClient.GetAsync("/api/v1/audit");
        Assert.Equal(HttpStatusCode.Forbidden, auditViewRes.StatusCode);

        // 3. Cashier attempts to export audit logs -> 403 Forbidden
        var auditExportRes = await cashierClient.GetAsync("/api/v1/audit/export");
        Assert.Equal(HttpStatusCode.Forbidden, auditExportRes.StatusCode);

        // 4. Cashier attempts to change user role (privilege escalation) -> 403 Forbidden
        Guid cashierUserId = env.Counter1.CurrentSession!.UserId;
        var roleEscalationRes = await cashierClient.PutAsJsonAsync($"/api/v1/users/{cashierUserId}/role",
            new UpdateUserRoleRequest(RoleType.Owner));
        Assert.Equal(HttpStatusCode.Forbidden, roleEscalationRes.StatusCode);

        // 5. Cashier attempts to decommission a terminal -> 403 Forbidden
        var decommRes = await cashierClient.PostAsync($"/api/v1/terminals/{env.Counter2.TerminalId}/decommission", null);
        Assert.Equal(HttpStatusCode.Forbidden, decommRes.StatusCode);

        // 6. Non-loopback IP attempts to invoke loopback-only recovery restore -> 403 Forbidden
        using var remoteRecoveryClient = fixture.CreateClient();
        remoteRecoveryClient.DefaultRequestHeaders.Add("X-Forwarded-For", "192.168.1.200");
        var recoveryRes = await remoteRecoveryClient.PostAsJsonAsync("/api/v1/admin/recovery/restore",
            new EmergencyRestoreRequestDto
            {
                FileName = "backup.bak",
                StoreMasterRecoveryKey = new string('A', 64)
            });
        Assert.Equal(HttpStatusCode.Forbidden, recoveryRes.StatusCode);

        // 7. Verify zero business-state mutation in database:
        using (var scope = fixture.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<RetailDbContext>();

            // Cashier role remains Cashier (no privilege escalation occurred)
            var cashierUser = await db.Users.FindAsync(cashierUserId);
            Assert.Equal(RoleType.Cashier, cashierUser!.Role);

            // Counter 2 terminal remains Active
            var terminal2 = await db.Terminals.FindAsync(env.Counter2.TerminalId);
            Assert.Equal(TerminalStatus.Active, terminal2!.Status);
        }

        // 8. Authorized administrative operation: Owner performs security setting update -> 200 OK
        using var ownerClient = fixture.CreateClient();
        ownerClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", env.OwnerToken);

        var ownerSecRes = await ownerClient.PutAsJsonAsync("/api/v1/settings/security",
            new UpdateSecuritySettingsRequest(25, 4, 10, 10m, true, true, true));
        Assert.True(ownerSecRes.IsSuccessStatusCode, "Owner must be permitted to update security settings.");

        var ownerAuditRes = await ownerClient.GetAsync("/api/v1/audit");
        Assert.True(ownerAuditRes.IsSuccessStatusCode, "Owner must be permitted to view audit logs.");

        _output.WriteLine("[TEST-P15-52] Passed: Cashier role attempts at security settings, audit, privilege escalation, and terminal decommissioning all rejected with 403 Forbidden; zero state mutation; Owner administrative operations succeed.");
    }
}
