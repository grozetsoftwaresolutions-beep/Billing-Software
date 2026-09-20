# POS Hardware Integration & Troubleshooting Guide

> [!NOTE]
> **Documentation Version Alignment**: Product: `1.0.0` | Protocol: `1` | Schema: `15` | Target OS: `Windows 10/11 x64`

This guide specifies hardware interface standards, wiring topologies, driver configurations, and troubleshooting procedures for retail peripherals integrated with **GROZET RetailPOS**.

---

## 1. POS Hardware Architecture Overview

GROZET RetailPOS communicates directly with retail peripherals using standard Windows and industry-standard protocols:
- **Barcode Scanners**: USB Human Interface Device (HID) Keyboard Wedge emulation.
- **Receipt Printers**: Windows Print Spooler (RAW ESC/POS mode) or direct TCP/IP (port 9100).
- **Cash Drawers**: Printer-driven solenoid kick-out via RJ-11/RJ-12 interface.
- **Weighing Scales**: Standard RS-232 serial communication or USB-to-Serial COM port emulation.

This native driver architecture eliminates dependency on proprietary, fragile third-party OPOS/JPOS driver stacks.

---

## 2. Barcode Scanners (USB HID Keyboard Wedge)

### Technical Operation
RetailPOS supports any standard 1D laser or 2D image barcode scanner operating in **USB HID Keyboard Emulation** mode. The scanner appears to Windows as an auxiliary keyboard.

When a barcode is read:
1. The scanner rapidly emits character keystrokes matching the barcode digits.
2. The internal input buffer (`KeyboardWedgeParser.cs`) buffers keystrokes occurring within sub-millisecond intervals.
3. The scanner emits a trailing `CR` (Carriage Return / Enter) or `CR+LF` terminator.
4. `KeyboardWedgeParser.cs` recognizes the rapid burst and terminator, routing the barcode directly to the active cart engine regardless of which UI field has focus.

### Configuration Requirements
Configure the barcode scanner by scanning the programming barcodes in the manufacturer's manual:
1. Set interface to: **USB HID Keyboard Mode**.
2. Set suffix terminator to: **CR (Enter)**.
3. Set keyboard layout to: **English (US)**.

### Troubleshooting Scanners

| Symptom | Root Cause | Resolution |
|---|---|---|
| **Scanner beeps but no item is added** | Scanner is configured in USB Serial / VCOM mode instead of USB HID mode. | Scan the manufacturer's programming barcode for **"USB HID Keyboard Emulation"**. |
| **Digits appear with missing characters** | Inter-character delay is too fast for the USB bus, or PC CPU is throttled. | Scan the barcode to add a 5ms or 10ms inter-character delay in the scanner settings. |
| **Barcode appears in wrong text field** | Suffix terminator is disabled or configured as Tab instead of Enter. | Scan programming barcode for **"Add Suffix: Enter (CR)"**. |
| **Scanner light is completely off** | Defective USB cable or disconnected USB port. | Plug scanner into a rear USB port on the motherboard; verify Windows Device Manager shows "HID Keyboard Device". |

---

## 3. Thermal Receipt Printers

RetailPOS supports standard 80mm (3-inch) and 58mm (2-inch) thermal receipt printers from major manufacturers (Epson, Star Micronics, TVS Electronics, Posiflex, Bixolon, Citizen).

### Integration Modes

#### Mode A: Windows Print Spooler RAW ESC/POS (Recommended for USB Printers)
- The printer is installed in Windows using the vendor driver or standard **"Generic / Text Only"** printer driver.
- The software sends raw ESC/POS byte streams directly through the Windows Spooler (`WindowsSpoolerReceiptPrinter.cs`).
- **5000ms Bounded Lock Timeout**: To prevent printing threads from hanging when a spooler deadlocks, RetailPOS enforces a strict 5000ms cancellation timeout (`cts.CancelAfter(5000)`). If the spooler does not accept the job within 5 seconds, the system logs an error without freezing the cashier UI.

#### Mode B: Direct Network TCP Printer (Recommended for Kitchen / Remote Printers)
- Printer connects via Ethernet cable to the store switch and is assigned a static IP (e.g. `192.168.1.50`).
- The software connects directly to **TCP Port 9100** (`NetworkTcpReceiptPrinter.cs`) and streams raw ESC/POS commands without going through Windows Print Spooler.

### Standard ESC/POS Command Sequences
- **Initialize Printer**: `ESC @` (`0x1B, 0x40`)
- **Align Center**: `ESC a 1` (`0x1B, 0x61, 0x01`)
- **Align Left**: `ESC a 0` (`0x1B, 0x61, 0x00`)
- **Bold Text ON / OFF**: `ESC E 1` / `ESC E 0` (`0x1B, 0x45, 0x01` / `0x1B, 0x45, 0x00`)
- **Paper Cut (Full / Partial)**: `GS V 66 0` (`0x1D, 0x56, 0x42, 0x00`)

### Troubleshooting Thermal Printers

| Symptom | Root Cause | Resolution |
|---|---|---|
| **Spooler error: "Windows Print Spooler timed out waiting for device lock (5000ms)"** | Another application holds an exclusive lock on the printer, or printer is paused in Windows queue. | Open `services.msc`, restart **Print Spooler** service. Open Windows Printer Queue and cancel stuck print jobs. |
| **Printer prints raw programming code / garbage symbols** | Windows printer driver is rendering bitmap graphics instead of passing raw ESC/POS text. | In Windows Printer Properties $\rightarrow$ Advanced, set driver to **"Generic / Text Only"** or enable RAW data type passing. |
| **Printer cuts paper too early (cuts through bill text)** | Form feed / line feed count is too low before cut command. | In Store Settings $\rightarrow$ Hardware Config $\rightarrow$ Printer, increase **Feed Lines Before Cut** from 3 to 5 lines. |
| **Paper out indicator flashing** | Thermal paper roll is depleted or installed upside down. | Replace paper roll; ensure thermal coating faces the thermal print head (scratch paper with fingernail to confirm black mark). |

---

## 4. Printer-Driven Cash Drawers

### Hardware Connection
Standard POS cash drawers (12V or 24V solenoids) connect to the receipt printer via a standard **RJ-11 or RJ-12 modular cable**. The cash drawer has no independent USB or power connection; it receives a momentary electrical firing pulse from the receipt printer when a cash sale completes.

### Pulse Command
When a cash sale is finalized or an authorized cashier triggers drawer opening, `PrinterDrivenCashDrawer.cs` prepends the standard ESC/POS kick-out pulse command to the print stream:
```text
ESC p m t1 t2
Hex: 0x1B, 0x70, 0x00, 0x19, 0xFA
```
- `m = 0`: Drawer pin 2 (standard RJ-11 pinout).
- `t1 = 25` (50ms on-time pulse duration).
- `t2 = 250` (500ms off-time delay).

### Troubleshooting Cash Drawers

| Symptom | Root Cause | Resolution |
|---|---|---|
| **Drawer does not open, but receipt prints** | RJ-11 cable is plugged into telephone port or loose; or solenoid voltage mismatch (12V drawer on 24V printer). | Check RJ-11 cable is firmly seated in the printer's **DK (Drawer Kick)** port. Verify drawer key lock is in the vertical (unlocked) position. |
| **Drawer opens on every print job (including card/UPI sales)** | Windows printer driver has "Open Cash Drawer" enabled in Windows Printer Preferences. | In Windows Printer Properties $\rightarrow$ Device Settings, set "Cash Drawer" to **"Do Not Open"** so RetailPOS software controls the pulse exclusively. |
| **Drawer kicks open with a buzzing sound but doesn't slide out** | Mechanical latch obstruction, bent rails, or loose coin lodged in drawer mechanism. | Use manual physical key to open drawer; inspect rails and clear lodged currency or coins behind drawer tray. |

---

## 5. Electronic Weighing Scales

### Hardware Connection & Protocol
RetailPOS integrates with certified electronic checkout scales (CAS, Essae, Mettler-Toledo, Datalogic Magellan) using an RS-232 serial cable or a USB-to-Serial adapter:
- **Serial Parameters**: Default 9600 Baud, 8 Data Bits, No Parity, 1 Stop Bit (`9600, 8, N, 1`).
- **Driver**: `SerialComWeighingScale.cs` with parsers in `ScaleParsers.cs`.

### Supported Protocol Modes
1. **Continuous Stream Mode**:
   The scale continuously broadcasts ASCII weight packets 5 to 10 times per second:
   ```text
   ST,GS,+001.450kg<CR><LF>
   ```
   `ScaleParsers.cs` extracts the net weight (`1.450`), verifies stability flag (`ST`), and updates the active weight display in the cashier UI.
2. **Poll / Command-Response Mode**:
   The terminal sends an inquiry command byte (e.g. `ENQ` / `0x05` or `W<CR>`), and the scale responds with the current weight packet.

### Troubleshooting Scales

| Symptom | Root Cause | Resolution |
|---|---|---|
| **UI displays "Scale Offline"** | COM port mismatch or USB-Serial adapter assigned a new COM port number. | Open Windows Device Manager $\rightarrow$ Ports (COM & LPT). Note the active COM port (e.g. `COM3`). Update COM port in RetailPOS Hardware Settings. |
| **Weight reads "0.000" or fluctuates wildly** | Scale platter is touching counter bezel, or scale is unlevel. | Ensure weighing platter moves freely without touching counter cutouts. Check bubble level on scale base. |
| **Error: "Scale Unstable"** | Customer or cashier is resting hand on platter during scan. | Remove hand from scale. RetailPOS requires weight stabilization (`ST` flag) before committing weight-based pricing. |
