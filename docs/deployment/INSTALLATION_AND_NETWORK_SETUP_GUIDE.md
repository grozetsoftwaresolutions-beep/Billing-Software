# Installation & Network Setup Guide

> [!NOTE]
> **Documentation Version Alignment**: Product: `1.0.0` | Protocol: `1` | Schema: `15` | Target OS: `Windows 10/11 x64`

This guide provides field engineers, deployment technicians, and IT administrators with the authoritative procedures for installing, configuring, hardening, and verifying **GROZET RetailPOS** on Windows 10 and 11.

---

## 1. System & Hardware Prerequisites

### Operating System Requirements
- **Supported OS**: Windows 10 (Version 1809 / Build 17763 or newer, 64-bit) or Windows 11 (64-bit).
- **Architecture**: `win-x64` (64-bit Intel/AMD processor).
- **Privileges**: Local Administrator privileges required during initial software installation, service registration, firewall rule creation, and NTFS ACL provisioning.
- **Runtime**: Self-contained runtime included in the installer. No separate .NET runtime installation required.

### Hardware Specifications

| Component | Dedicated Server PC (Recommended) | Counter Terminal PC (Minimum) |
|---|---|---|
| **Processor** | Intel Core i5 / AMD Ryzen 5 (4+ cores, 2.5+ GHz) | Intel Core i3 / Celeron / Pentium (2+ cores, 2.0+ GHz) |
| **Memory (RAM)** | 8 GB DDR4 or higher | 4 GB DDR4 or higher |
| **Storage** | 256 GB NVMe SSD (minimum 50 GB free space) | 128 GB SSD (minimum 20 GB free space) |
| **Network Adapter** | 1 Gbps Gigabit Ethernet PCI-e / Onboard NIC | 100 Mbps or 1 Gbps Ethernet NIC |
| **USB Ports** | 4+ USB 2.0/3.0 ports (for backup drives & peripherals) | 4+ USB ports (scanner, printer, drawer, scale) |
| **Power Supply** | Dedicated Online UPS (minimum 600VA / 360W) | Shared or lane UPS recommended |

---

## 2. Installer Packages & Roles

The packaging pipeline produces three distinct Inno Setup installer executables located in the release distribution (`artifacts/releases/v1.0.0/`):

1. **Unified Single-Station Installer (`GrozetRetailPOS_Setup.exe`)**:
   - **Target Environment**: Single-counter retail shops where the same physical PC acts as both the billing counter and the store database server.
   - **Installed Components**: Installs both `GrozetPOSServer` Windows Service and `RetailPOS.Client.exe` desktop UI.
   - **Default Path**: `C:\Program Files\GROZET\RetailPOS\`

2. **Dedicated Server Installer (`GrozetRetailPOS_Server_Setup.exe`)**:
   - **Target Environment**: Back-office Server PC in multi-counter stores.
   - **Installed Components**: Installs only the headless `GrozetPOSServer` Windows Service and administrative scripts. Does not install the counter UI.
   - **Default Path**: `C:\Program Files\GROZET\RetailPOS\Server\`

3. **Dedicated Counter Installer (`GrozetRetailPOS_Client_Setup.exe`)**:
   - **Target Environment**: Checkout counter lanes in multi-counter stores.
   - **Installed Components**: Installs only the `RetailPOS.Client.exe` desktop UI. Does not install the server or database service.
   - **Default Path**: `C:\Program Files\GROZET\RetailPOS\Client\`

---

## 3. Windows Service Architecture & Registration

In production, the backend server must run as a managed Windows Service rather than an interactive console application.

### Service Identification
- **Canonical Service Name**: `GrozetPOSServer`
- **Display Name**: `Grozet Retail POS Server`
- **Description**: `Authoritative local server for Grozet Retail POS billing, inventory, and counter terminal synchronization.`
- **Virtual Service Account**: `NT SERVICE\GrozetPOSServer`
  - Auto-provisioned by Windows SCM during service creation.
  - Requires no password and zero credential maintenance.
  - Operates under least-privilege security boundaries (no local Administrator rights).

### Automated Service Installation Script
To register and configure the service with authoritative recovery parameters, execute the deployment script from an elevated PowerShell terminal:

```powershell
# Navigate to deployment scripts directory
cd "C:\Program Files\GROZET\RetailPOS\Server\scripts\deployment"

# Register, harden, and start the Windows Service
.\Deploy-WindowsService.ps1 -Action Install -BinaryPath "C:\Program Files\GROZET\RetailPOS\Server\RetailPOS.Server.exe"
```

### SCM Failure Recovery Policy
The deployment script configures Windows SCM failure recovery actions via `sc.exe failure`:
- **Reset Period**: 86,400 seconds (24 hours).
- **Restart Delay**: 60,000 milliseconds (60 seconds).
- **Actions**: Automatically restarts the service after 60 seconds on the 1st, 2nd, and subsequent unexpected process terminations:
  ```cmd
  sc.exe failure GrozetPOSServer reset= 86400 actions= restart/60000/restart/60000/restart/60000
  ```

---

## 4. Directory Layout & NTFS ACL Hardening

RetailPOS enforces complete physical separation between immutable application binaries and mutable operational state.

### Immutable Binaries (`Program Files`)
- **Server Path**: `C:\Program Files\GROZET\RetailPOS\Server\`
- **Client Path**: `C:\Program Files\GROZET\RetailPOS\Client\`
- **Permissions**:
  - `BUILTIN\Administrators`: Full Control `(OI)(CI)F`
  - `NT AUTHORITY\SYSTEM`: Full Control `(OI)(CI)F`
  - `BUILTIN\Users`: Read & Execute `(OI)(CI)RX`
  - `NT SERVICE\GrozetPOSServer`: Read & Execute `(OI)(CI)RX` (strictly **NO** Write, Modify, or Delete permissions)

### Mutable Operational State (`ProgramData`)
All mutable database files, cryptographic keys, backups, and logs reside under:
`%ProgramData%\GROZET\RetailPOS\`

To prevent unauthorized tampering, inheritance is stripped (`/inheritance:r`) on the operational directory tree, and ordinary Windows user accounts (`BUILTIN\Users`) are denied all access:

```text
%ProgramData%\GROZET\RetailPOS\
├── Database\            # Authoritative grozet_pos.db, WAL, and SHM files (Service Modify; Users Denied)
├── Backups\             # Local B1/B2/C backup files and HMAC manifests (Service Modify; Users Denied)
├── Security\            # DPAPI store_identity.dat, server_identity.key (Service Modify; Users Denied)
├── Logs\                # Serilog rolling diagnostic logs (Service Modify; Users Denied)
└── Staging\             # Migration journals, quarantine, and recovery state (Service Modify; Users Denied)
```

The deployment script executes `icacls.exe` to enforce this exact permissions matrix automatically.

---

## 5. Windows Defender Firewall Configuration

The server host must permit inbound LAN communication from authorized counter terminals while strictly blocking exposure to public networks.

### Authoritative Inbound Rules

| Rule Name | Protocol | Port | Description | Approved Profiles |
|---|---|---|---|---|
| **`Grozet-POS-Server-HTTP-In`** | TCP | `5100` | Counter terminal HTTP REST API traffic | `Domain,Private` only |
| **`Grozet-POS-Server-HTTPS-In`** | TCP | `5101` | Optional TLS-encrypted REST API traffic | `Domain,Private` only |
| **`Grozet-POS-Server-Discovery-In`** | UDP | `5105` | Counter terminal automated server discovery | `Domain,Private` only |

> [!WARNING]
> **Strict Firewall Profile Rule**:
> Inbound firewall rules must **NEVER** be bound to the `Public` network profile.
> If Windows prompts that the network is "Public", open Windows Network Settings and change the network profile to **Private**.

### Manual Firewall Rule Creation (Reference)
```powershell
# Create Inbound HTTP Rule (TCP 5100)
New-NetFirewallRule -Name "Grozet-POS-Server-HTTP-In" `
    -DisplayName "Grozet POS Server HTTP (Port 5100)" `
    -Direction Inbound -Protocol TCP -LocalPort 5100 `
    -Profile Domain,Private -Action Allow

# Create Inbound HTTPS Rule (TCP 5101)
New-NetFirewallRule -Name "Grozet-POS-Server-HTTPS-In" `
    -DisplayName "Grozet POS Server HTTPS (Port 5101)" `
    -Direction Inbound -Protocol TCP -LocalPort 5101 `
    -Profile Domain,Private -Action Allow

# Create Inbound UDP Discovery Rule (UDP 5105)
New-NetFirewallRule -Name "Grozet-POS-Server-Discovery-In" `
    -DisplayName "Grozet POS Server Discovery (Port 5105)" `
    -Direction Inbound -Protocol UDP -LocalPort 5105 `
    -Profile Domain,Private -Action Allow
```

---

## 6. Physical LAN & Switch Setup Guidelines

For maximum billing reliability, follow these physical infrastructure standards:

1. **Dedicated Wired Ethernet Switch**:
   Connect the Server PC and all Counter Terminal PCs to a dedicated 8-port or 16-port unmanaged/managed Gigabit Ethernet switch (e.g. Netgear, TP-Link, Cisco).
2. **Strictly No Wi-Fi Billing Counters**:
   Checkout counters must **never** operate over Wi-Fi. Microwave ovens, metal shelving, and store customer smartphones cause radio frequency interference, leading to packet drops and transaction latency.
3. **Static IP Configuration or DHCP Reservations**:
   - Assign the Server PC a static IP address on the local subnet (e.g. `192.168.1.10`, Subnet Mask `255.255.255.0`, Gateway `192.168.1.1`).
   - Alternatively, configure a DHCP reservation on the store router matching the Server PC's MAC address.
4. **Subnet Isolation**:
   Place POS equipment on a dedicated POS VLAN or subnet isolated from guest Wi-Fi or public office computers.

---

## 7. Post-Installation Verification Checklist

Before leaving the customer site, the installation technician must execute and pass the following verification tests:

### Step 1: Verify Windows Service Status
```powershell
Get-Service GrozetPOSServer | Format-List Name, Status, StartType
# Expected output: Status: Running, StartType: Automatic
```

### Step 2: Verify Active Port Listeners
```powershell
Get-NetTCPConnection -LocalPort 5100, 5101 -State Listen | Select-Object LocalAddress, LocalPort, State
# Expected output: Listening on 0.0.0.0 or 127.0.0.1 on ports 5100 and 5101
```

### Step 3: Test Server Health Endpoint
```powershell
Invoke-RestMethod -Uri "http://127.0.0.1:5100/api/v1/health" -Method Get
# Expected output: status: Healthy, isDatabaseInitialized: true
```

### Step 4: Test Counter Connectivity from a Counter PC
From a counter terminal PC on the same LAN:
```powershell
Test-NetConnection -ComputerName 192.168.1.10 -Port 5100
# Expected output: TcpTestSucceeded : True
```

### Step 5: Proceed to First-Run Setup
Once connectivity is confirmed, open `RetailPOS.Client.exe` or navigate to the store provisioning screen to complete **[First-Run & Store Initialization](file:///docs/operations/FIRST_RUN_AND_STORE_INITIALIZATION_GUIDE.md)**.
