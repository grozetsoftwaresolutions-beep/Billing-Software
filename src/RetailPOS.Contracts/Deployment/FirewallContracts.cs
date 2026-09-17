using System;

namespace RetailPOS.Contracts.Deployment;

[Flags]
public enum FirewallProfiles
{
    None = 0,
    Domain = 1,
    Private = 2,
    Public = 4
}

public sealed class FirewallRuleConfig
{
    public string RuleName { get; set; } = "GrozetRetailPOS_LAN_Server";
    public int Port { get; set; } = 5001;
    public string Protocol { get; set; } = "TCP";
    public FirewallProfiles Profiles { get; set; } = FirewallProfiles.Domain | FirewallProfiles.Private;
}

public sealed class FirewallStatus
{
    public bool IsConfigured { get; set; }
    public string RuleName { get; set; } = string.Empty;
    public bool AllowsDomain { get; set; }
    public bool AllowsPrivate { get; set; }
    public bool AllowsPublic { get; set; }
}
