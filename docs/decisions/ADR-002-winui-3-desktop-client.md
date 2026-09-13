# ADR-002: WinUI 3 + Windows App SDK Desktop Client

## Status
Accepted (Locked)

## Context
The user interface must be modern, keyboard-first, native Windows, with high contrast and immediate responsiveness during high-volume retail scanning. Web technologies (Electron/React) add unnecessary memory overhead, browser sandboxing issues for direct hardware access (USB ESC/POS printing, cash drawer kick), and lack native Windows shell integration.

## Decision
Use WinUI 3 with the Windows App SDK and XAML, following the MVVM architectural pattern powered by CommunityToolkit.Mvvm.

## Consequences
- Native Windows 10/11 UX with smooth rendering and native hardware driver interop.
- Strict separation of View and ViewModel. No business logic or database queries in XAML code-behind or ViewModels.
