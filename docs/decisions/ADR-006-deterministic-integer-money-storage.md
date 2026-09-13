# ADR-006: Deterministic Integer Minor-Unit Money Storage

## Status
Accepted (Locked)

## Context
Floating-point representations (loat, double) cause IEEE 754 precision errors and financial discrepancies during summing and tax calculations. SQLite also lacks a native fixed-point decimal type.

## Decision
In domain and business calculations, monetary values use high-precision C# decimal. In persistence, all currency amounts are stored strictly as 64-bit integer minor units (BIGINT paise in INR: ?123.45 = 12345).

## Consequences
- 100% precision guarantee across storage, math, and reporting.
- EF Core value converters handle bidirectional translation automatically.
