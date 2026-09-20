# Security & Access Control Guide

> [!NOTE]
> **Documentation Version Alignment**: Product: `1.0.0` | Protocol: `1` | Schema: `15` | Target OS: `Windows 10/11 x64`

This guide specifies the security architecture, Role-Based Access Control (RBAC) permissions matrix, authentication policies, supervisor authorization workflows, audit logging, and cryptographic identity model for **GROZET RetailPOS**.

---

## 1. Security Architecture Principles

GROZET RetailPOS is designed to protect commercial retail operations from internal employee fraud, supply-chain tampering, and local network threats:

1. **Zero-Trust LAN Model**:
   While POS devices operate on an isolated store LAN, the server treats all connected counter terminals as untrusted clients. All prices, discounts, tax rates, inventory balances, and invoice numbers are recalculated and committed exclusively by the authoritative server.
2. **Least Privilege Service Identity**:
   The background server process runs under the virtual account `NT SERVICE\GrozetPOSServer` without local Windows Administrator privileges.
3. **Immutable Application Binaries**:
   Executable binaries in `C:\Program Files\GROZET\RetailPOS\` are protected with Read & Execute permissions. The service account and standard users have zero write, modify, or delete privileges over application code.
4. **Isolated Operational State**:
   Mutable databases, logs, and cryptographic keys in `%ProgramData%\GROZET\RetailPOS\` have inheritance stripped (`/inheritance:r`), granting access exclusively to Administrators, SYSTEM, and `NT SERVICE\GrozetPOSServer`. Standard Windows users have zero access.

---

## 2. Role-Based Access Control (RBAC) Matrix

RetailPOS evaluates access control using a frozen 32-permission domain matrix (`RolePermissions.cs`) across five standard roles:

### Summary of Standard Roles
- **Owner**: Complete system authority (32 permissions / 100%). Reserved for the store proprietor.
- **Admin**: Full store management authority (29 permissions / 90.6%). Excludes database restore, user role changes, and store identity settings.
- **Manager**: Operational store supervisor (22 permissions / 68.8%). Manages cashiers, stock, safe drops, and supervisor overrides.
- **Cashier**: Lane billing operator (7 permissions / 21.9%). Restricted to sales, product search, receipt reprinting, and shift cash handling.
- **Viewer**: Read-only business analyst or external accountant (8 permissions / 25.0%). Restricted to report viewing and exporting.

### The Canonical 32-Permission Evaluation Matrix

| # | Permission Code | Description | Owner | Admin | Manager | Cashier | Viewer |
|---|---|---|:---:|:---:|:---:|:---:|:---:|
| 1 | `pos:sale:create` | Create and commit sales | **YES** | **YES** | **YES** | **YES** | NO |
| 2 | `pos:sale:read` | Search and view sales history | **YES** | **YES** | **YES** | **YES** | **YES** |
| 3 | `pos:sale:reprint` | Reprint sales receipts | **YES** | **YES** | **YES** | **YES** | NO |
| 4 | `pos:sale:void` | Void an incomplete sale | **YES** | **YES** | **YES** | NO | NO |
| 5 | `pos:sale:refund` | Process return and refund | **YES** | **YES** | **YES** | NO | NO |
| 6 | `pos:sale:price-override` | Override product unit price | **YES** | **YES** | **YES** | NO | NO |
| 7 | `pos:sale:elevated-discount`| Apply discount exceeding 10% | **YES** | **YES** | **YES** | NO | NO |
| 8 | `security:supervisor-override` | Authorize cashier exceptions | **YES** | **YES** | **YES** | NO | NO |
| 9 | `inventory:stock:view` | View real-time stock balances | **YES** | **YES** | **YES** | **YES** | **YES** |
| 10 | `inventory:stock:adjust` | Write off damaged/expired stock| **YES** | **YES** | **YES** | NO | NO |
| 11 | `catalog:product:manage` | Add or edit product catalog | **YES** | **YES** | **YES** | NO | NO |
| 12 | `purchase:invoice:view` | View supplier purchase bills | **YES** | **YES** | **YES** | NO | **YES** |
| 13 | `purchase:invoice:create` | Record new supplier purchase | **YES** | **YES** | **YES** | NO | NO |
| 14 | `purchase:invoice:cancel` | Cancel purchase invoice | **YES** | **YES** | NO | NO | NO |
| 15 | `supplier:profile:view` | View supplier profiles | **YES** | **YES** | **YES** | NO | **YES** |
| 16 | `supplier:profile:manage` | Add or edit supplier profiles | **YES** | **YES** | **YES** | NO | NO |
| 17 | `customer:profile:view` | View customer credit profiles | **YES** | **YES** | **YES** | **YES** | **YES** |
| 18 | `customer:profile:manage` | Add or edit customer profiles | **YES** | **YES** | **YES** | NO | NO |
| 19 | `cash:session:open` | Open cashier cash session | **YES** | **YES** | **YES** | **YES** | NO |
| 20 | `cash:session:close` | Close cash session & count | **YES** | **YES** | **YES** | **YES** | NO |
| 21 | `cash:session:view` | View active cash drawer totals | **YES** | **YES** | **YES** | NO | **YES** |
| 22 | `reports:general:view` | View daily sales & Z-reports | **YES** | **YES** | **YES** | NO | **YES** |
| 23 | `reports:general:export` | Export CSV/PDF reports | **YES** | **YES** | **YES** | NO | **YES** |
| 24 | `admin:users:manage` | Add or edit store staff accounts | **YES** | **YES** | NO | NO | NO |
| 25 | `admin:users:role` | Change a user's role | **YES** | NO | NO | NO | NO |
| 26 | `admin:users:credentials` | Reset staff passwords / PINs | **YES** | **YES** | NO | NO | NO |
| 27 | `admin:settings:manage` | Modify store security settings | **YES** | NO | NO | NO | NO |
| 28 | `admin:terminals:manage` | Enroll and pair POS counters | **YES** | **YES** | NO | NO | NO |
| 29 | `admin:backup:create` | Trigger manual database backup | **YES** | **YES** | NO | NO | NO |
| 30 | `admin:backup:restore` | Restore database from backup | **YES** | NO | NO | NO | NO |
| 31 | `admin:audit:view` | View system audit event logs | **YES** | **YES** | NO | NO | NO |
| 32 | `admin:audit:export` | Export audit trail logs | **YES** | **YES** | NO | NO | NO |
| | **TOTAL GRANTED** | | **32** | **29** | **22** | **7** | **8** |

---

## 3. Authentication & Account Security Policies

Configured via `SecuritySettings.cs` and managed by the Store Owner:

1. **Brute-Force PIN Lockout**:
   - Cashiers authenticate using a 4-to-6 digit PIN.
   - If an incorrect PIN is entered **5 consecutive times** (`MaxFailedPinAttempts = 5`), the account is locked for **15 minutes** (`PinLockoutMinutes = 15`).
   - The lockout timestamp is recorded in the database (`LockoutEndUtc`) and persists across application restarts.
2. **Session Inactivity Timeout**:
   - If a terminal detects no keyboard, mouse, or scanner activity for **15 minutes** (`IdleTimeoutMinutes = 15`), the active session locks automatically.
3. **Cashier Discount Ceiling**:
   - Cashiers can apply discounts up to **10.00%** (`MaxCashierDiscountPercentage = 10.00`).
   - Any discount exceeding 10.00% automatically requires Supervisor Approval.

---

## 4. Supervisor Authorization Core

When a cashier attempts an operation requiring supervisor privileges (e.g. refund, sale void, price override, elevated discount):
1. The terminal UI displays a **Supervisor Elevation Dialog**.
2. A Store Manager, Admin, or Owner enters their PIN.
3. The server executes `SupervisorAuthorizationCore.cs`:
   - Validates supervisor account credentials.
   - Verifies that the supervisor possesses the `security:supervisor-override` permission.
   - Verifies the supervisor account is not locked out or disabled.
4. If valid, the operation proceeds and an audit event is recorded capturing:
   - Cashier User ID and Username.
   - Authorizing Supervisor User ID and Username.
   - Override reason and financial parameters (e.g. original price vs overridden price).

---

## 5. Audit Logging Architecture

RetailPOS maintains an immutable audit trail in the SQLite `audit_logs` table. Audit events are recorded synchronously within business transactions:

### Audit Event Schema
- `Id`: Unique 64-bit sequence identifier.
- `TimestampUtc`: Atomic UTC timestamp of the event.
- `Category`: `AUTH`, `SALE`, `TERM`, `BACKUP`, `SEC`, or `INVENTORY`.
- `Action`: Specific operation (e.g. `AUTH_USER_LOCKOUT`, `SALE_PRICE_OVERRIDE`, `SALE_VOID`, `TERM_PAIRING_APPROVED`, `BACKUP_RESTORE_INITIATED`).
- `UserId` & `Username`: Active cashier account.
- `SupervisorUserId`: Authorizing supervisor (if elevated).
- `TerminalId`: Originating physical checkout counter.
- `CorrelationId`: Unique GUID tracking the end-to-end HTTP request.
- `Details`: JSON-structured operational metadata.

---

## 6. Cryptographic Identity & Data Protection

RetailPOS implements defense-in-depth cryptography to protect store identity and transactions:

### DPAPI LocalMachine Protection (`store_identity.dat`)
- The active store identity, installation secrets, and server signing keys are stored in `%ProgramData%\GROZET\RetailPOS\Security\store_identity.dat`.
- The file is encrypted using Windows Data Protection API (DPAPI) with `DataProtectionScope.LocalMachine`.
- This ensures that only processes running on the authorized Windows machine can decrypt the operational identity.

### Store Signing Key & Master Recovery Key Architecture
- During store provisioning, the server generates a 256-bit `StoreSigningKey`.
- The `StoreSigningKey` is wrapped using AES-256-GCM with a key derived from the 256-bit `StoreMasterRecoveryKey` via HKDF (`"grozet-pos-store-key-wrapping-v1"`).
- An HMAC validation hash (`"grozet-pos-store-key-validation-v1"`) is stored in the database.
- The physical Master Recovery Key is displayed once, printed, and stored in the store fireproof safe. The key is never saved on disk.

### Counter Terminal Pairing Cryptography (SRP-6a)
- Terminal pairing executes using SRP-6a with an 80-bit CSPRNG `HumanPairingCode` (16 Crockford Base32 characters formatted as `XXXX-XXXX-XXXX-XXXX`).
- Mutual authentication is verified using a 40-bit HMAC-derived Short Authentication String (SAS, 8 Crockford Base32 characters formatted as `XXXX-XXXX`).
- No passwords or persistent secrets are transmitted across the LAN.
