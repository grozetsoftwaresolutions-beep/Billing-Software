namespace RetailPOS.Domain.Enums;

public enum ServerState
{
    Available = 0,
    FirstTimeSetupRequired = 1,
    IdentityRecoveryRequired = 2,
    StartupIntegrityFailure = 3
}
