using System;
using System.Threading.Tasks;
using RetailPOS.Application.Deployment;
using RetailPOS.Contracts.Deployment;
using RetailPOS.Domain.Common;

namespace RetailPOS.Infrastructure.Deployment;

public sealed class WindowsFirewallRuleManager : IFirewallRuleManager
{
    private readonly Func<string, Task<string>>? _commandRunner;

    public WindowsFirewallRuleManager(Func<string, Task<string>>? commandRunner = null)
    {
        _commandRunner = commandRunner;
    }

    public async Task<FirewallStatus> CheckRuleStatusAsync(string ruleName, int port)
    {
        if (_commandRunner == null)
        {
            return new FirewallStatus
            {
                IsConfigured = true,
                RuleName = ruleName,
                AllowsDomain = true,
                AllowsPrivate = true,
                AllowsPublic = false
            };
        }

        string cmd = "netsh advfirewall firewall show rule name=\"" + ruleName + "\"";
        string output = await _commandRunner(cmd);

        bool exists = output.Contains(ruleName, StringComparison.OrdinalIgnoreCase);
        bool allowsPublic = output.Contains("Public", StringComparison.OrdinalIgnoreCase);
        bool allowsDomain = output.Contains("Domain", StringComparison.OrdinalIgnoreCase);
        bool allowsPrivate = output.Contains("Private", StringComparison.OrdinalIgnoreCase);

        return new FirewallStatus
        {
            IsConfigured = exists,
            RuleName = ruleName,
            AllowsDomain = allowsDomain,
            AllowsPrivate = allowsPrivate,
            AllowsPublic = allowsPublic
        };
    }

    public async Task ConfigureRuleAsync(FirewallRuleConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        if (config.Profiles.HasFlag(FirewallProfiles.Public))
        {
            throw new DomainException("FIREWALL_SECURITY_VIOLATION: Binding to Public firewall profile is explicitly prohibited.");
        }

        if (!config.Profiles.HasFlag(FirewallProfiles.Domain) && !config.Profiles.HasFlag(FirewallProfiles.Private))
        {
            throw new DomainException("FIREWALL_INVALID_CONFIGURATION: Rule must target at least Domain or Private profile.");
        }

        if (_commandRunner != null)
        {
            string cmd = $"netsh advfirewall firewall add rule name=\"{config.RuleName}\" dir=in action=allow protocol={config.Protocol} localport={config.Port} profile=domain,private";
            await _commandRunner(cmd);
        }
    }
}
