# ADR-004: Standalone ASP.NET Core Windows Service Architecture

## Status
Accepted (Locked)

## Context
In retail environments, desktop UI clients may be closed, crashed, or restarted by cashiers without disrupting backend database operations or other billing counters. 

## Decision
The ASP.NET Core server is designed and hosted as an independent Windows Service (GrozetPOSServerService) utilizing Microsoft.Extensions.Hosting.WindowsServices. 

## Consequences
- Single-PC stores run both the Windows Service and WinUI 3 client on the same machine communicating over http://localhost:5100.
- Multi-Counter stores run the Windows Service on the main PC and WinUI 3 clients on counter PCs over the LAN.
- The WinUI client remains strictly an HTTP API client in all deployment modes, maintaining architectural uniformity.
