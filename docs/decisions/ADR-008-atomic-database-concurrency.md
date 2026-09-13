# ADR-008: Atomic Database Concurrency for Stock Deductions

## Status
Accepted (Locked)

## Context
Multiple counters may attempt to sell the last remaining units of an item at the same millisecond. Volatile in-memory locks do not survive server restarts and can fail across multi-process or multi-worker architectures.

## Decision
Stock deduction is enforced atomically at the database level inside the transaction:
\\\sql
UPDATE stock_balances 
SET quantity = quantity - @QuantityRequested, updated_at = @Timestamp
WHERE product_id = @ProductId AND quantity >= @QuantityRequested;
\\\
If affected rows is 0, the transaction rolls back immediately and throws \InsufficientStockException\.

## Consequences
- Guaranteed zero negative stock and zero stock corruption under concurrent sales.
- Concurrency safety is maintained regardless of worker threads or server restarts.
