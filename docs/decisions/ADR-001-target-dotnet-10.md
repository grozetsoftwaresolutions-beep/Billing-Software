# ADR-001: Target Framework Locked to .NET 10

## Status
Accepted (Locked)

## Context
The POS & Billing system is a high-performance, native Windows commercial product. It requires modern C# 14 language features, optimized runtime performance, enhanced Ahead-Of-Time (AOT) and JIT optimizations, and LTS/current support from Microsoft for desktop and local server workloads.

## Decision
Strictly lock all solution projects to .NET 10 (TargetFramework: net10.0). Do NOT use floating definitions such as 'latest stable' or automatically upgrade/downgrade to other major versions.

## Consequences
- All projects share unified TargetFramework: net10.0 defined centrally in Directory.Build.props.
- High-performance memory management and zero-allocation span APIs are available throughout domain and persistence layers.
