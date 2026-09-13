# Security Boundaries & Network Model

## 1. Environment Profiles: Development vs. Production

### Development Profile
* **Host Binding**: Bound strictly to `127.0.0.1` (loopback).
* **Protocol**: Plain HTTP over localhost (`http://127.0.0.1:5100`).
* **Firewall**: No inbound LAN rules required. Zero network exposure outside the development machine.
* **Diagnostics**: Full local access to diagnostic and health endpoints.

### Production Profile
* **Host Binding**: Dedicated physical store server. Can bind to loopback for single-PC installations, or to the private LAN adapter (`0.0.0.0` or static LAN IP) for multi-counter installations.
* **Network Topology**: Isolated physical wired Ethernet switch. No wireless POS counters.
* **Internet Boundary**: **Zero public internet exposure.** The server must never have a public IP, port forwarding, or internet-facing reverse proxy.
* **Transport Encryption (HTTPS)**: For deployments requiring wire-level encryption, HTTPS (`https://server-ip:5101`) can be enabled using an internal machine-trusted certificate issued by the store authority.

---

## 2. The Fallacy of "LAN Alone is Sufficient Security"

> [!WARNING]
> A private wired LAN alone does **NOT** provide complete security.

While an isolated LAN eliminates broad external internet attacks, it leaves the system vulnerable to:
1. **Rogue Hardware**: Any device plugged into an open physical Ethernet port on the switch.
2. **Network Bridging**: A POS counter connected to both LAN and an unauthorized Wi-Fi hotspot or mobile data tether.
3. **Malicious or Compromised Clients**: Tampered client software sending altered prices, negative quantities, or forged sequence numbers.
4. **Insider Threats**: Unauthorized store staff attempting to access managerial or database diagnostic APIs.

---

## 3. Layered Defense-in-Depth Model

To prevent single-point failures, RetailPOS implements layered defense-in-depth:

### Layer 1: OS Firewall & Subnet Restrictions
* Windows Defender Firewall permits TCP 5100 only on the `Private` network profile, restricted to the store's subnet (e.g., `192.168.1.0/24`).

### Layer 2: Authoritative Server Verification (Zero Client Trust)
* Remote POS counters never access SQLite directly (no SMB shares, no direct SQL).
* The server re-computes all pricing, tax components (CGST/SGST), and discounts. Client-supplied financial computations are never trusted.
* Monotonic invoice numbering and atomic stock balances are enforced exclusively by the server in SQLite.

### Layer 3: Terminal Registration & Identification
* Protected endpoints enforce `TerminalValidationMiddleware`.
* Only registered, active terminals stored in `RetailDbContext.Terminals` can perform billing operations.
* Unknown, inactive, or malformed terminal IDs are rejected with `403 Forbidden` (`TERMINAL_UNAUTHORIZED`).

### Layer 4: Diagnostic & Health Endpoint Isolation
* Public liveness (`GET /api/v1/health`) reveals only high-level status and store metadata.
* Deep database diagnostics (`GET /api/v1/health/database`) are restricted strictly to loopback (`127.0.0.1` / `::1`) service diagnostics. Remote callers are rejected with `403 Forbidden`.
* Database file paths, SQL queries, raw exception strings, and stack traces are **never** returned in API responses.

### Layer 5: Local Cryptographic Sessions
* Authenticated operations require cryptographically random 64-character tokens (`RandomNumberGenerator`).
* Tokens are process-local, in-memory, and immediately revocable.
* Server restart purges active sessions, requiring cashiers to re-authenticate.
* Plaintext passwords, PINs, and session tokens are strictly excluded from operational logs.
