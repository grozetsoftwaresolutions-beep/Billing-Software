# ADR-003: Authoritative Local SQLite Storage with WAL Mode

## Status
Accepted (Locked)

## Context
The POS system is offline-first and store-centric. The store must own its data without cloud dependencies. Multiple counters must see a single, consistent source of truth.

## Decision
Use a single local SQLite database hosted exclusively on the local server PC. Write-Ahead Logging (WAL) is enabled (PRAGMA journal_mode=WAL) to allow concurrent non-blocking reads during active billing transactions.

## Consequences
- High-speed local transactions without network database server setup overhead.
- Under NO circumstance may POS clients open the SQLite database directly over an SMB network share. All counter access must flow through the local server API.
