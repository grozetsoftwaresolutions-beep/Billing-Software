using System.Threading.Tasks;
using RetailPOS.Contracts.Deployment;

namespace RetailPOS.Application.Deployment;

public interface IFirewallRuleManager
{
    Task<FirewallStatus> CheckRuleStatusAsync(string ruleName, int port);
    Task ConfigureRuleAsync(FirewallRuleConfig config);
}
