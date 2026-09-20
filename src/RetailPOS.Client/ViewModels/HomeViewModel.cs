using System;
using CommunityToolkit.Mvvm.ComponentModel;

namespace RetailPOS.Client.ViewModels;

public sealed partial class HomeViewModel : ObservableObject
{
    [ObservableProperty]
    public partial string WelcomeHeading { get; set; } = "Welcome to GROZET RetailPOS";

    [ObservableProperty]
    public partial string WelcomeSubheading { get; set; } = "Enterprise Presentation & Terminal Client • WinUI 3 Desktop Foundation";

    [ObservableProperty]
    public partial string SystemStatus { get; set; } = "Ready";

    [ObservableProperty]
    public partial string MachineName { get; set; } = Environment.MachineName;

    [ObservableProperty]
    public partial string Architecture { get; set; } = Environment.Is64BitProcess ? "x64" : "x86";

    [ObservableProperty]
    public partial string RuntimeTarget { get; set; } = ".NET 10 (Windows 10/11 x64)";

    [ObservableProperty]
    public partial string DataAuthorityNotice { get; set; } = "Authoritative SQLite database is maintained on the server. The client communicates via authenticated LAN APIs without direct database access.";

    [ObservableProperty]
    public partial string PhaseNotice { get; set; } = "Phase 17.2 Active: Application Shell & Navigation Foundation established. Business modules (POS, Products, Reports, Settings) are staged as foundation placeholders.";
}
