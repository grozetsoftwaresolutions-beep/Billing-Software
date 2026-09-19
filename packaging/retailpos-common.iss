; Grozet Retail POS - Shared Inno Setup Compile-Time Definitions
; Authoritative Architecture v5.2.9.6 Compliant - Sub-Phase 16.4 Production Packaging
; This file contains ONLY shared metadata and common compiler flags.
; AppId, DefaultDirName, [Run], [UninstallRun], and [UninstallDelete] MUST remain explicit in each role installer.

#define AppPublisher "Grozet Software Solutions"
#define AppVersion "1.0.0"
#define AppServerExe "RetailPOS.Server.exe"
#define AppClientExe "RetailPOS.Client.exe"
#define ServiceName "GrozetPOSServer"
#define ServiceAccount "NT SERVICE\GrozetPOSServer"
#define HttpPort "5100"
#define HttpsPort "5101"
#define DiscoveryPort "5105"

; Frozen AppId constants for collision detection across installers
#define ServerAppId "{D37D15A2-3482-4E9A-963C-09B926F14920}"
#define ClientAppId "{E5A4D3B1-5693-4F8B-A12C-18E2938B7401}"
#define UnifiedAppId "{C8F24E67-91B5-4D3A-B804-6F294E8A1D39}"
