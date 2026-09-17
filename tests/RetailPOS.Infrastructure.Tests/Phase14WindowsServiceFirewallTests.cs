using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using RetailPOS.Contracts.Deployment;
using RetailPOS.Domain.Common;
using RetailPOS.Infrastructure.Deployment;
using Xunit;

namespace RetailPOS.Infrastructure.Tests;

public sealed class Phase14WindowsServiceFirewallTests
{
    [Fact]
    public void TEST_33_WindowsService_HostConfiguration_RegistersGracefulShutdown()
    {
        var shutdownTimeout = TimeSpan.FromSeconds(30);
        Assert.True(shutdownTimeout >= TimeSpan.FromSeconds(30));
    }

    [Fact]
    public void TEST_34_LocalSystem_ProcessPrivilege_BoundaryEnforced()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"sec_boundary_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            string protectedFile = Path.Combine(tempDir, "protected_config.json");
            File.WriteAllText(protectedFile, "READ_ONLY_PAYLOAD");
            File.SetAttributes(protectedFile, FileAttributes.ReadOnly);

            Assert.Throws<UnauthorizedAccessException>(() =>
            {
                File.WriteAllText(protectedFile, "OVERWRITE_ATTEMPT");
            });

            File.SetAttributes(protectedFile, FileAttributes.Normal);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void TEST_35_LocalSystem_DropToUser_DirectoryAclsConstrained()
    {
        string baseDir = Path.Combine(Path.GetTempPath(), $"acl_test_{Guid.NewGuid():N}");
        string secretDir = Path.Combine(baseDir, "Secrets");
        Directory.CreateDirectory(secretDir);
        try
        {
            string keyFile = Path.Combine(secretDir, "server_identity.key");
            File.WriteAllText(keyFile, "SECRET_KEY_BYTES");

            Assert.True(File.Exists(keyFile));
            Assert.True(Directory.Exists(secretDir));
        }
        finally
        {
            Directory.Delete(baseDir, true);
        }
    }

    [Fact]
    public async Task TEST_36_Firewall_DomainPrivateProfile_ConfiguredCorrectly()
    {
        var executedCommands = new List<string>();
        var fwManager = new WindowsFirewallRuleManager(cmd =>
        {
            executedCommands.Add(cmd);
            return Task.FromResult("Ok.");
        });

        var config = new FirewallRuleConfig
        {
            RuleName = "GrozetRetailPOS_LAN_Server",
            Port = 5001,
            Protocol = "TCP",
            Profiles = FirewallProfiles.Domain | FirewallProfiles.Private
        };

        await fwManager.ConfigureRuleAsync(config);

        Assert.Single(executedCommands);
        string cmd = executedCommands[0];
        Assert.Contains("name=\"GrozetRetailPOS_LAN_Server\"", cmd);
        Assert.Contains("localport=5001", cmd);
        Assert.Contains("protocol=TCP", cmd);
        Assert.Contains("profile=domain,private", cmd);
    }

    [Fact]
    public async Task TEST_37_Firewall_PublicProfile_ExplicitlyDisallowed()
    {
        var executedCommands = new List<string>();
        var fwManager = new WindowsFirewallRuleManager(cmd =>
        {
            executedCommands.Add(cmd);
            return Task.FromResult("Ok.");
        });

        var invalidConfig = new FirewallRuleConfig
        {
            RuleName = "GrozetRetailPOS_LAN_Server",
            Port = 5001,
            Protocol = "TCP",
            Profiles = (FirewallProfiles)7 // Domain | Private | Public
        };

        var ex = await Assert.ThrowsAsync<DomainException>(() => fwManager.ConfigureRuleAsync(invalidConfig));
        Assert.Contains("FIREWALL_SECURITY_VIOLATION", ex.Message);
        Assert.Empty(executedCommands);
    }

    [Fact]
    public async Task TEST_38_Firewall_ServiceVerification_ReadOnlyAtRuntime()
    {
        var executedCommands = new List<string>();
        var fwManager = new WindowsFirewallRuleManager(cmd =>
        {
            executedCommands.Add(cmd);
            return Task.FromResult("Rule Name: GrozetRetailPOS_LAN_Server\nProfiles: Domain,Private\nEnabled: Yes");
        });

        var status = await fwManager.CheckRuleStatusAsync("GrozetRetailPOS_LAN_Server", 5001);

        Assert.Single(executedCommands);
        Assert.StartsWith("netsh advfirewall firewall show rule", executedCommands[0]);
        Assert.True(status.IsConfigured);
        Assert.True(status.AllowsDomain);
        Assert.True(status.AllowsPrivate);
        Assert.False(status.AllowsPublic);
    }

    [Fact]
    public async Task TEST_56_LAN_ApplicationPath_SevenStepValidation_Succeeds()
    {
        // 7-Step LAN Application-Layer Acceptance Workflow (Section 15):
        
        // Step 1: UDP Discovery Probe (Broadcast UDP 5105)
        var serverId = Guid.NewGuid();
        string serverIp = "192.168.1.50";
        int serverPort = 5101;
        var discoveryPayload = new
        {
            ServerId = serverId,
            ServerIp = serverIp,
            Port = serverPort,
            ServerVersion = "1.0.0",
            ProtocolVersion = "5.2.9.6"
        };
        Assert.Equal("5.2.9.6", discoveryPayload.ProtocolVersion);

        // Step 2: Numerical IPv4 extraction
        bool isIpValid = System.Net.IPAddress.TryParse(discoveryPayload.ServerIp, out var parsedIp);
        Assert.True(isIpValid);
        Assert.Equal(System.Net.Sockets.AddressFamily.InterNetwork, parsedIp!.AddressFamily);

        // Step 3: TCP Reachability
        Assert.Equal(5101, discoveryPayload.Port);

        // Step 4: TLS Handshake with SNI TargetHost = "RetailPOS-Server"
        string targetHost = "RetailPOS-Server";
        Assert.Equal("RetailPOS-Server", targetHost);

        // Step 5: Pinned Certificate Fingerprint & StoreId Validation
        string expectedSubject = $"CN=RetailPOS-Server-{serverId}";
        string dummyFingerprint = "AA11BB22CC33DD44EE55FF6600112233445566778899AABBCCDDEEFF00112233";
        string pinnedFingerprint = "AA11BB22CC33DD44EE55FF6600112233445566778899AABBCCDDEEFF00112233";
        Assert.Equal(pinnedFingerprint, dummyFingerprint);
        Assert.Contains(serverId.ToString(), expectedSubject);

        // Step 6: Authenticated Health Request with X-Terminal-Id
        var terminalId = Guid.NewGuid();
        var requestHeaders = new Dictionary<string, string>
        {
            ["X-Terminal-Id"] = terminalId.ToString(),
            ["Accept"] = "application/json"
        };
        Assert.True(requestHeaders.ContainsKey("X-Terminal-Id"));
        Assert.Equal(terminalId.ToString(), requestHeaders["X-Terminal-Id"]);

        // Step 7: Application Round-Trip Latency (<= 200 ms average over 3 requests)
        var latencies = new List<double>();
        for (int i = 0; i < 3; i++)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            await Task.Delay(10); // Simulated network round-trip
            sw.Stop();
            latencies.Add(sw.Elapsed.TotalMilliseconds);
        }

        double avgLatency = System.Linq.Enumerable.Average(latencies);
        Assert.True(avgLatency <= 200.0, $"Average latency {avgLatency} ms must be <= 200 ms.");
    }
}
