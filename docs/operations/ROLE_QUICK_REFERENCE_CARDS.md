# Role Quick Reference Cards

> [!NOTE]
> **Documentation Version Alignment**: Product: `1.0.0` | Protocol: `1` | Schema: `15` | Target OS: `Windows 10/11 x64`

These printable, laminated quick-reference cards provide operational step-by-step instructions tailored for daily use at checkout counters and manager desks.

---

## Card 1: POS Cashier Quick Reference Card

```text
================================================================================
                    GROZET RETAIL POS — CASHIER QUICK CARD
================================================================================

1. STARTING YOUR SHIFT
   [ ] Log in with your assigned Username and 4-6 digit PIN.
   [ ] If prompted, enter Opening Cash Float (e.g. ₹2,000.00).
   [ ] Confirm cash drawer kicks open and verifies float notes.

2. BILLING AN ORDER
   [ ] Scan product barcode with handheld scanner (or press F2 to search by name/SKU).
   [ ] Adjust quantity: Click "+" / "-" or type quantity and press Enter.
   [ ] Apply discount (if authorized, up to 10% maximum).
   [ ] Press F5 to open Payment Checkout modal.
   [ ] Select Payment Mode:
       - CASH : Enter cash received -> Note "Change Due" -> Press Enter.
       - UPI  : Customer scans QR -> Enter UTR / Ref Number -> Confirm.
       - CARD : Swipe/dip on EDC machine -> Enter Auth Code -> Confirm.
       - SPLIT: Enter partial Cash + partial UPI -> Total must equal bill.
   [ ] Collect money, hand change to customer, and hand printed receipt.

3. HANDLING EXCEPTIONS (SUPERVISOR APPROVAL REQUIRED)
   - Price Override       : Click "Override Price" -> Manager enters PIN.
   - Discount > 10%       : System prompts -> Manager enters PIN.
   - Void Entire Sale     : Click "Void Sale" -> Select reason -> Manager enters PIN.
   - Item Return / Refund : Click "Returns" -> Enter Invoice # -> Manager enters PIN.

4. ENDING YOUR SHIFT
   [ ] Finish all pending cart checkouts.
   [ ] Press Ctrl+Shift+C (or click "Close Cash Session").
   [ ] Physically count drawer cash and enter denomination breakdown.
   [ ] System shows Expected vs Counted Cash and any Overage/Shortage.
   [ ] Click "Finalize Session" -> Drawer kicks open -> Collect printed summary slip.
   [ ] Submit cash drawer and signed summary slip to Store Manager.

================================================================================
FAST SHORTCUTS:  [F2] Search Item   [F5] Pay/Checkout   [F12] Switch User   [Del] Remove Item
================================================================================
```

---

## Card 2: Store Manager Quick Reference Card

```text
================================================================================
                 GROZET RETAIL POS — STORE MANAGER QUICK CARD
================================================================================

1. SUPERVISOR OVERRIDE PROTOCOL
   When a cashier triggers a supervisor prompt, enter your Manager PIN to authorize:
   - Cashier Discount > 10%: Inspect promotional reason before approving.
   - Price Override: Confirm damaged goods or approved store markdown.
   - Void Sale: Confirm customer has physically departed without paying.
   - Return / Refund: Inspect returned physical goods and customer original bill.
   * NEVER reveal your Manager PIN to cashiers. Always enter it yourself.

2. MID-DAY CASH SAFE DROPS (CASH OUT)
   - When counter drawer accumulates excess cash (e.g. > ₹20,000):
   - Navigate to Drawer Operations -> Cash Out (Safe Drop).
   - Enter drop amount (e.g. ₹15,000.00). Enter your Manager PIN.
   - Move cash to the back-office safe and sign the generated drop voucher.

3. INVENTORY & STOCK ADJUSTMENTS
   - Quick Stock Check: Press F3 or go to Inventory -> Stock Lookup.
   - Damaged / Expired Write-Off: Go to Inventory -> Adjust Stock -> Select Reason:
     "Damaged", "Expired", or "Stock Audit Discrepancy" -> Enter adjustment.
   - Purchase Invoice Entry: Go to Purchases -> New Invoice -> Enter supplier bill.

4. END-OF-DAY CLOSING & Z-REPORT
   [ ] Confirm all counter cashiers have finalized their cash sessions.
   [ ] Navigate to Reports -> Daily Z-Report.
   [ ] Verify total Gross Sales, Total Tax Collected, and Tender Balances.
   [ ] Print authoritative Daily Z-Report for store accounting archives.
   [ ] Verify Server PC indicates "Backup Successful" in the status bar.

================================================================================
SECURITY REMINDER: Lock your terminal screen (Windows Key + L) whenever stepping away.
================================================================================
```

---

## Card 3: Store Owner Quick Reference Card

```text
================================================================================
                  GROZET RETAIL POS — STORE OWNER QUICK CARD
================================================================================

1. USER ROLE & CREDENTIAL GOVERNANCE
   - Only the OWNER role can assign or change user roles.
   - Creating New Staff: Go to Settings -> Users -> Add User -> Assign Role:
     * Cashier: Billing & shift operations only (7 permissions).
     * Manager: Overrides, inventory, safe drops, Z-reports (22 permissions).
     * Admin: Full operational control except DB restore & store settings (29 permissions).
   - Staff Departure: Immediately mark departed employee accounts as "Inactive".

2. STORE SECURITY SETTINGS
   - Navigate to Settings -> Security Settings:
     * Idle Timeout: 15 minutes (recommended).
     * PIN Lockout: 5 failed attempts -> 15-minute lock.
     * Cashier Max Discount: Default 10.00% (elevated discounts require manager).
     * Supervisor Mandate: Enforce supervisor approval for Refunds, Voids, Overrides.

3. FINANCIAL OVERSIGHT & CUSTOMER CREDIT
   - Review Daily Gross Margins: Go to Reports -> Sales Profitability.
   - Review Outstanding Customer Credit (Khata):
     * Go to Customers -> Outstanding Credit Ledger.
     * Review customers exceeding approved credit limits.
     * Record customer debt repayments under Customer Accounting.

4. DISASTER RECOVERY & BACKUP OVERSIGHT
   - Store Master Recovery Key:
     * Stored in Store Fireproof Safe (XXXX-XXXX-XXXX-XXXX-XXXX-XXXX-XXXX-XXXX).
     * NEVER stored on the PC desktop or shared with contractors.
   - Removable USB Backup (B2):
     * Rotate dedicated USB flash drives weekly.
     * Plug USB drive into Server PC -> Click "Trigger B2 USB Backup".
     * Remove USB drive and store securely off-site.

================================================================================
EMERGENCY SUPPORT: If Server PC fails, refer to docs/operations/EMERGENCY_OPERATIONAL_RUNBOOKS.md
================================================================================
```
