# ADR-005: Wired Ethernet LAN Network Model

## Status
Accepted (Locked)

## Context
Retail billing counters require 99.999% uptime and immunity from Wi-Fi interference, packet loss, and WAN/internet outages.

## Decision
The authoritative network setup uses wired Ethernet cabling through a local unmanaged 8/16-port network switch on a private subnet (e.g. 192.168.50.0/24). Internet access is strictly optional and never required for normal sales or inventory workflows.

## Consequences
- Zero external dependencies for billing, receipt printing, and synchronization.
- Server discovery is conducted locally via UDP broadcasts on port 5101.
