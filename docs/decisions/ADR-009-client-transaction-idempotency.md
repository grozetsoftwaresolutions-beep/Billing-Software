# ADR-009: Client Transaction Idempotency

## Status
Accepted (Locked)

## Context
In a local network POS, if a network packet drops after a sale has been committed on the server, a client retry might result in a duplicate invoice and double stock reduction.

## Decision
Clients generate a unique \ClientTransactionId\ (GUID) for every checkout attempt. The server tracks these keys in \idempotency_keys\. If a request arrives with an existing key, the server safely returns the previously committed sale invoice without re-executing transactions.

## Consequences
- Cashiers can safely retry failed network requests without creating duplicate bills or deducting duplicate inventory.
