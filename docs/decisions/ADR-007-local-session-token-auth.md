# ADR-007: Local Session Token Authentication

## Status
Accepted (Locked)

## Context
The POS operates entirely offline. Introducing JWT web authentication adds unnecessary dependency overhead and lacks immediate revocation capabilities on local counter terminals.

## Decision
Use cryptographically secure local session tokens (256-bit random tokens) stored in the user_sessions table with an expiration timestamp. Cashier PINs and passwords are authenticated using PBKDF2 with unique per-user cryptographically random salts (100,000 iterations).

## Consequences
- Passwords and PINs are never stored in plaintext.
- The server validates session tokens, terminal identity, and role permissions on every API call.
- Immediate cashier logout and configurable inactivity timeouts are natively supported.
