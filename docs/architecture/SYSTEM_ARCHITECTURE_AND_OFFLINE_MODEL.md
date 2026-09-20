# System Architecture & Offline-First LAN Model

> [!NOTE]
> **Documentation Version Alignment**: Product: `1.0.0` | Protocol: `1` | Schema: `15` | Target OS: `Windows 10/11 x64`

---

## 1. Architectural Principles

GROZET RetailPOS is engineered around four uncompromising architectural tenets:

1. **Absolute Offline-First Autonomy**:
   Retail store billing cannot depend on internet connectivity, external cloud APIs, or third-party web services. A severed fiber line or ISP outage must never impede sales or billing. All catalog lookups, pricing, tax calculations, invoice sequencing, and receipt printing execute exclusively within the store's physical premises.

2. **Authoritative Local Server Model**:
   Counter terminals do not execute autonomous financial transactions or hold decentralized offline transaction queues. The physical Server PC hosting the `GrozetPOSServer` Windows Service is the single authoritative source of truth for all business operations, pricing calculations, invoice sequences, stock adjustments, and customer ledgers.

3. **Zero-Trust Network Boundary**:
   While the server and counter terminals communicate over a private wired LAN, the server enforces strict zero-trust validation on all incoming requests. Counter terminals are untrusted clients; all item prices, taxes, discounts, and inventory decrements are recalculated and validated on the server. Remote clients never access database files or SQL ports directly.

4. **Deterministic Financial Arithmetic**:
   All monetary amounts are represented and calculated using 64-bit signed integers in paise (1 INR = 100 paise). Floating-point data types (`float`, `double`) are strictly prohibited in financial calculations, eliminating fractional-cent rounding errors and tax discrepancy disputes (ADR-006).

---

## 2. System Topology

RetailPOS supports two physical deployment topologies:

### Topology A: Unified Single-Station Installation (Small Stores)
For single-counter retail shops (e.g. boutique, convenience store, small grocery), the server and client components reside on a single Windows PC:
- **Server Component**: Runs in the background as the `GrozetPOSServer` Windows Service.
- **Client Component**: Runs in the foreground as the `RetailPOS.Client.exe` desktop UI.
- **Communication**: Communication takes place over local loopback (`http://127.0.0.1:5100`).
- **Hardware**: Barcode scanner, receipt printer, and cash drawer are physically connected to this single PC via USB.

### Topology B: Multi-Counter LAN Deployment (Supermarkets & Department Stores)
For multi-counter retail establishments, a dedicated Back-Office Server PC connects to multiple counter terminals over a dedicated wired Ethernet switch:
- **Server PC**: Placed in the store office or behind the service desk, running `GrozetPOSServer` Windows Service. Holds the authoritative `grozet_pos.db` database, backup drives, and administrative management console.
- **Counter Terminals (Counters 1..N)**: Lightweight Windows 10/11 PCs at the checkout lanes running `RetailPOS.Client.exe`.
- **LAN Backbone**: Isolated Gigabit Ethernet switch. Counter terminals discover the server via UDP broadcast on port `5105` and communicate over HTTP (port `5100`) or TLS-encrypted HTTPS (port `5101`).

```mermaid
graph TD
    subgraph LAN["Physical Isolated Wired Ethernet Switch (1 Gbps)"]
        subgraph ServerNode["Authoritative Server PC (Static IP: 192.168.1.10)"]
            SVC["GrozetPOSServer Windows Service"]
            DB[("Authoritative SQLite Database (grozet_pos.db)")]
            SEC["DPAPI Identity (store_identity.dat)"]
            BAK["Backup Volumes (B1 / B2 / C GDR1)"]
            SVC --> DB
            SVC --> SEC
            SVC --> BAK
        end

        subgraph Lane1["Checkout Lane 1 (192.168.1.21)"]
            C1["Counter Terminal 1 (RetailPOS.Client.exe)"]
            HW1["Scanner + 80mm Printer + Cash Drawer"]
            C1 --> HW1
        end

        subgraph Lane2["Checkout Lane 2 (192.168.1.22)"]
            C2["Counter Terminal 2 (RetailPOS.Client.exe)"]
            HW2["Scanner + 80mm Printer + Cash Drawer"]
            C2 --> HW2
        end

        subgraph LaneN["Checkout Lane N (192.168.1.2N)"]
            CN["Counter Terminal N (RetailPOS.Client.exe)"]
            HWN["Scanner + 80mm Printer + Cash Drawer"]
            CN --> HWN
        end

        C1 -->|HTTP POST /api/v1/sales (TCP 5100)| SVC
        C2 -->|HTTP POST /api/v1/sales (TCP 5100)| SVC
        CN -->|HTTP POST /api/v1/sales (TCP 5100)| SVC
    end
```

---

## 3. Authoritative Local Server Architecture

The server application (`RetailPOS.Server`) is implemented using ASP.NET Core Minimal APIs hosted inside a managed Windows Service via `Microsoft.Extensions.Hosting.WindowsServices`:

- **Operating Identity**: Runs under the dedicated virtual service account `NT SERVICE\GrozetPOSServer` (least privilege; no local Administrator rights).
- **Service Lifetime**: Started automatically on operating system boot (`StartMode=auto`). If the service terminates unexpectedly, Windows Service Control Manager (SCM) automatically restarts it after 60 seconds (`restart/60000/restart/60000/restart/60000`).
- **Endpoint Routing**: Endpoints are organized into modular route groups:
  - `/api/v1/auth`: Cashier PIN authentication, session management, and brute-force lockout.
  - `/api/v1/discovery`: UDP and HTTP server challenge/response discovery.
  - `/api/v1/terminals`: Counter registration, SRP-6a mutual pairing, and terminal validation.
  - `/api/v1/sales`: Authoritative sale creation, cart checkout, invoice sequence generation, voids, returns.
  - `/api/v1/products`: Product catalog, barcode lookup, price checks, category queries.
  - `/api/v1/inventory`: Real-time stock levels, stock adjustments, low-stock thresholds.
  - `/api/v1/cash-sessions`: Cashier drawer open, float tracking, cash in/out, session close reconciliation.
  - `/api/v1/customers`: Customer profiles, outstanding credit ledgers, debt repayment.
  - `/api/v1/backups`: Manual and automated backup triggers, verification audits, GDR1 exports.
  - `/api/v1/diagnostics`: 4-tier redacted diagnostics collection, support bundle packaging.
  - `/api/v1/recovery`: Emergency loopback database restoration (`127.0.0.1` only).

---

## 4. Authoritative SQLite Storage Model

The primary data store is an authoritative, standalone SQLite database located at:
`%ProgramData%\GROZET\RetailPOS\Database\grozet_pos.db`

### SQLite Concurrency & Tuning Configuration
To achieve high-throughput multi-counter concurrency without locking conflicts, the database connection engine (`SqliteConnectionConfigurator.cs`) configures SQLite with the following pragmas:

1. **Write-Ahead Logging (`PRAGMA journal_mode = WAL;`)**:
   Enables concurrent read and write operations. Multiple counter terminals can execute read queries (product lookup, customer search, inventory queries) simultaneously without blocking or being blocked by active sales write transactions.
2. **Normal Synchronization (`PRAGMA synchronous = NORMAL;`)**:
   Ensures transactional ACID safety while reducing disk flush overhead in WAL mode. Critical checkpoint operations guarantee data durability to physical disk storage.
3. **5000ms Busy Timeout (`PRAGMA busy_timeout = 5000;`)**:
   When multiple counters commit sales within the same millisecond, SQLite's internal lock wait handler retries transparently for up to 5,000 milliseconds before throwing a busy exception. In production retail testing, transactions resolve within 15 milliseconds, ensuring smooth lane operations.
4. **Foreign Key Enforcement (`PRAGMA foreign_keys = ON;`)**:
   Strict relational integrity across invoices, line items, payments, customer credits, and cash sessions.

---

## 5. Local Network (LAN) Communication Model

### Protocol Ports
- **TCP Port 5100**: Primary HTTP REST API for counter terminal communication.
- **TCP Port 5101**: Optional TLS-encrypted HTTPS REST API.
- **UDP Port 5105**: Server Discovery Listener. Counter terminals broadcast discovery packets (`GROZETPOS_DISCOVER_SERVER_V1` or `GROZET_DISCOVERY_PROBE`) on UDP 5105; the server responds with its store identity, server version, and HTTP base URL (`http://<SERVER_IP>:5100`).

### Network Partition Behavior (Strict Server-Authoritative Model)
Because RetailPOS uses a single authoritative local server to guarantee monotonic invoice sequencing and prevent inventory overselling, **counter terminals do NOT maintain an independent offline transaction queue**.

When a counter terminal experiences a network partition (e.g. damaged Ethernet cable, disconnected switch port):
1. **Immediate Transaction Halt**: The counter client halts operations that require server authority. Cashiers must not attempt to finalize sales or print official tax receipts while disconnected.
2. **Physical & Network Diagnosis**: The operator checks the physical cable, switch link lights, and network configuration.
3. **Service Verification**: The operator verifies the `GrozetPOSServer` service status on the Server PC.
4. **Connectivity Restoration**: Once the physical connection is restored, the counter terminal automatically re-establishes its session.
5. **Resume Normal Billing**: Normal server-authoritative billing resumes immediately. Unrecorded offline sales are never manually injected into the database.

---

## 6. Financial Calculation & Data Integrity

### Deterministic Integer Monetary Arithmetic (ADR-006)
Floating-point representation (such as `float` or `double` in IEEE 754) introduces non-deterministic binary approximations (e.g. `0.1 + 0.2 = 0.30000000000000004`). In retail billing, this leads to rounding discrepancies in GST line-item tax totals, customer change calculations, and invoice reconciliation.

In RetailPOS:
- All prices, discounts, taxes, and payment amounts are stored as 64-bit integers representing **paise** (1 INR = 100 paise; e.g. ₹49.50 is stored as `4950`).
- Tax calculations compute CGST and SGST using integer division with bankers' rounding (round half to even) applied deterministically at the line-item level.
- Line-item totals sum precisely to the invoice grand total, eliminating invoice balancing anomalies.

### Monotonic Invoice Sequence Numbering
Sequential invoice numbers are generated by `InvoiceSequenceGenerator.cs` inside the primary SQLite transaction:
- The sequence name `DEFAULT_SALE` maintains a strictly increasing counter.
- Invoices are formatted deterministically (e.g. `INV-2026-000001`).
- Sequence increments occur inside the atomic database transaction; if a sale aborts, the sequence rollback prevents numbering gaps, satisfying legal tax audit requirements.

---

## 7. Architectural Decision Records (ADRs)

Key architectural decisions are formally documented in the immutable records under [`docs/decisions/`](file:///docs/decisions/):
- **[ADR-001](file:///docs/decisions/ADR-001-target-dotnet-10.md)**: Target .NET 10 LTS Platform
- **[ADR-002](file:///docs/decisions/ADR-002-winui-3-desktop-client.md)**: WinUI 3 Desktop Client Framework
- **[ADR-003](file:///docs/decisions/ADR-003-sqlite-authoritative-storage.md)**: SQLite Authoritative Storage Engine
- **[ADR-004](file:///docs/decisions/ADR-004-standalone-aspnet-core-windows-service.md)**: Standalone ASP.NET Core Windows Service
- **[ADR-005](file:///docs/decisions/ADR-005-wired-ethernet-network-model.md)**: Wired Ethernet Network Model
- **[ADR-006](file:///docs/decisions/ADR-006-deterministic-integer-monetary-calculations.md)**: Deterministic Integer Monetary Calculations
- **[ADR-007](file:///docs/decisions/ADR-007-local-session-token-auth.md)**: Local Session Token Authentication
- **[ADR-008](file:///docs/decisions/ADR-008-atomic-database-concurrency-wal.md)**: Atomic Database Concurrency via WAL Mode
- **[ADR-009](file:///docs/decisions/ADR-009-client-transaction-idempotency.md)**: Client Transaction Idempotency
- **[ADR-010](file:///docs/decisions/ADR-010-sqlite-safe-backup-api.md)**: SQLite Safe Backup API & Hot Snapshots
