# Store User Operations Manual

> [!NOTE]
> **Documentation Version Alignment**: Product: `1.0.0` | Protocol: `1` | Schema: `15` | Target OS: `Windows 10/11 x64`

This manual provides cashier staff, store supervisors, and retail managers with step-by-step instructions for executing daily retail sales, cash handling, invoicing, customer credit, and end-of-day store operations using **GROZET RetailPOS**.

---

## 1. User Authentication & Daily Login

### Starting a Shift
1. Power on the Counter Terminal PC and launch **Grozet Retail POS**.
2. On the login screen, enter your assigned Cashier Username and Password, or click your name from the staff roster and enter your **4-to-6 digit numeric PIN**.
3. Click **Login** or press **Enter**.

### Fast Cashier Switching
During busy checkout periods, multiple cashiers sharing a terminal can quickly switch active sessions without restarting the application:
- Press **F12** or click the **Switch User** button in the top navigation bar.
- The new cashier enters their PIN to take over active billing.
- Active cart contents are preserved under the new cashier's active session.

### Brute-Force Lockout Protection
To prevent PIN guessing by unauthorized staff:
- If an incorrect PIN is entered **5 consecutive times**, the account is locked out for **15 minutes** (`PinLockoutMinutes = 15`).
- During lockout, the terminal displays an error: *"Account locked due to excessive failed attempts. Try again in 15 minutes or request a Manager Reset."*
- A Store Manager or Owner can immediately unlock the cashier account from the Admin Users screen.

### Inactivity Auto-Lock
If the terminal is left untouched for **15 minutes** (`IdleTimeoutMinutes = 15`), the screen automatically locks to prevent unauthorized customer access. The cashier must re-enter their PIN to resume billing.

---

## 2. Cash Session & Drawer Management

Every cashier must operate within an active **Cash Session** to account for physical cash collected during their shift.

### Opening a Cash Session
1. Upon logging in, if no cash session is open, the terminal prompts: **"Open New Cash Session"**.
2. Count the physical cash in the drawer (the opening float / change fund).
3. Enter the **Opening Float Amount** (e.g. `₹2,000.00`).
4. Click **Confirm Open Session**. The cash drawer kicks open for float verification.

### Mid-Shift Cash In / Cash Out
During the day, cash may enter or leave the drawer outside of regular sales transactions:
- **Cash In (Petty Cash Addition)**: Used when adding more small change to the drawer.
  - Navigate to **Drawer Operations** $\rightarrow$ **Cash In**.
  - Enter the amount and select a reason code (e.g. *"Float Top-Up"*).
- **Cash Out / Safe Drop**: Used when the drawer accumulates excess cash that must be moved to the back-office safe.
  - Navigate to **Drawer Operations** $\rightarrow$ **Cash Out (Safe Drop)**.
  - Enter the amount (requires Store Manager PIN approval).
  - Both Cashier and Manager sign the generated cash drop slip.

### Closing a Cash Session (End of Shift)
1. At the end of the shift, ensure all open carts are completed.
2. Click **Close Cash Session** (or press **Ctrl+Shift+C**).
3. The terminal displays the **End-of-Shift Cash Reconciliation** screen.
4. Physically count all currency denominations (notes and coins) in the drawer and enter the counts into the breakdown table:
   - ₹500, ₹200, ₹100, ₹50, ₹20, ₹10, and coins.
5. The system automatically calculates:
   - **Total Counted Cash**: Sum of counted cash.
   - **Expected Cash**: Opening Float + Total Cash Sales + Cash In - Cash Out - Refunds Paid.
   - **Overage / Shortage**: Difference between counted and expected cash.
6. If a discrepancy exists, enter an explanation in the notes field.
7. Click **Finalize Session & Print Report**. The cash drawer kicks open and the session summary receipt prints.

---

## 3. Product Search & Catalog Lookup

RetailPOS provides three rapid methods to add products to a customer's cart:

### 1. Barcode Scanning (Primary Method)
- Point the USB handheld barcode scanner at the product's barcode label.
- The scanner reads the 1D/2D code via USB Keyboard Wedge mode and instantly adds the item to the active cart with a quantity of 1.
- Scanning the same barcode multiple times automatically increments the item quantity.

### 2. Manual SKU / Barcode Entry
- If a barcode label is torn or unreadable, press **F2** to focus the search box.
- Type the numeric barcode or item SKU and press **Enter**.

### 3. Product Name / Keyword Search
- Type any part of the product name or category (e.g. `"Basmati"`, `"Milk"`, `"Shampoo"`).
- A real-time dropdown displays matching items, current selling price, and real-time stock on hand.
- Use the **Up/Down Arrow** keys and press **Enter** to select the item.

---

## 4. Cart & Billing Operations

### Modifying Item Quantities
- Click the **+** or **-** buttons next to an item in the cart, or:
- Click the quantity box, type the desired quantity (e.g. `5`), and press **Enter**.
- To weigh loose produce, see [Hardware Guide: Weighing Scales](file:///docs/operations/HARDWARE_INTEGRATION_AND_TROUBLESHOOTING.md#5-electronic-weighing-scales).

### Removing an Item
- Click the **Trash Can** icon next to an item, or highlight the item and press **Delete**.

### Price Overrides (Supervisor Gated)
If an item has a damaged package or a special promotional price not yet updated in the system:
1. Highlight the item in the cart and click **Override Price**.
2. Enter the new unit selling price and select a mandatory reason code.
3. Because price overrides represent financial risk, the terminal prompts for **Supervisor Approval**.
4. A Store Manager or Owner enters their PIN to authorize the price override.
5. The override is stamped with the manager's ID in the audit log.

---

## 5. Applying Discounts

RetailPOS enforces strict governance over price discounts:

### Line-Item Discounts vs. Bill-Level Discounts
- **Line-Item Discount**: Applies to a single product line (e.g. 5% off a specific item).
- **Bill-Level Discount**: Applies to the entire invoice subtotal (e.g. ₹100 flat festival discount).

### Cashier Discount Threshold (10.00%)
- Cashiers are authorized to grant discounts up to **10.00%** (`MaxCashierDiscountPercentage = 10.00`).
- If a cashier enters a discount of 10% or less, it applies immediately without prompting.
- If a discount exceeding 10% is entered, the terminal triggers an **Elevated Discount Approval** dialog requiring a Store Manager or Owner PIN.

---

## 6. GST & Tax Calculation

RetailPOS calculates Indian Goods and Services Tax (GST) deterministically using integer paise arithmetic:
- **Intra-State Sales** (Same state): Automatically splits tax into equal **CGST** (Central GST) and **SGST** (State GST) components (e.g. an 18% GST item is split into 9% CGST and 9% SGST).
- **Inter-State Sales** (Different state): Applies full **IGST** (Integrated GST).
- **Tax-Inclusive Pricing**: Product MRP already includes GST; the server automatically calculates the backward base price and tax breakdown without penny rounding errors.
- **Tax-Exclusive Pricing**: Base price is displayed; taxes are calculated and added at checkout.

---

## 7. Payment Processing & Tenders

When all items are scanned, press **F5** or click **Pay / Checkout** to open the Payment Modal:

### 1. Cash Payment
- Type the amount of physical cash received from the customer (e.g. customer gives ₹500 for a ₹342 bill).
- The terminal displays **Change Due**: `₹158.00`.
- Press **Enter** to complete the sale. The cash drawer automatically kicks open.

### 2. UPI Payment (Dynamic / Static QR)
- Select **UPI**. The customer scans the store QR code or counter display.
- Enter the UPI Transaction Reference Number (UTR / Ref ID).
- Click **Confirm Payment**.

### 3. Card Payment (POS EDC Terminal)
- Select **Card**. Swipe or dip the customer's debit/credit card on the external bank EDC machine.
- Once the EDC machine prints an approval slip, enter the last 4 digits of the card and approval code.
- Click **Confirm Payment**.

### 4. Customer Store Credit (Udhar / Khata)
- Select **Customer Credit**.
- The customer must be selected from the customer roster.
- The system checks the customer's available credit limit; if within limits, the sale is charged to the customer's credit balance.

### 5. Split Tenders
RetailPOS supports split payments across multiple payment modes (e.g. ₹500 paid in Cash, remaining ₹1,250 paid via UPI). The sale cannot be finalized until the total payments equal the invoice grand total.

---

## 8. Invoicing & Receipt Printing

Upon finalizing payment:
1. **Invoice Number Generation**: The server atomically assigns the next gap-free sequential invoice number (`INV-2026-XXXXXX`).
2. **Thermal Receipt Printing**: The receipt prints automatically on the thermal printer (80mm or 58mm).
3. **Receipt Reprinting**:
   - If a customer requests a duplicate receipt, go to **Sales History**, search by invoice number, and click **Reprint Receipt**.
   - All reprinted receipts prominently display the watermarked label: `*** DUPLICATE REPRINT ***` along with reprint timestamp.

---

## 9. Returns, Refunds & Void Sales

### Voiding an Incomplete Sale
If a customer walks away before paying:
1. Click **Void Sale**.
2. Select a reason (e.g. *"Customer Left"*).
3. Requires **Supervisor Approval** (Manager PIN).
4. The cart is cleared and recorded in the audit log; inventory is not deducted.

### Processing Returns & Refunds
1. Click **Returns / Refunds** and scan or enter the original Invoice Number.
2. Select the specific item(s) and quantities being returned.
3. Select return reason (e.g. *"Damaged Goods"*, *"Wrong Size"*, *"Customer Dissatisfied"*).
4. Select the refund method: Cash Refund or Customer Store Credit.
5. **Supervisor Approval Required**: A Store Manager must enter their PIN to authorize the refund.
6. Upon confirmation, a Credit Note / Refund Invoice prints, inventory is returned to stock, and the cash drawer kicks open if cash is refunded.

---

## 10. End-of-Day (EOD) Procedures

At the close of business each evening:
1. Ensure all cashiers have closed their cash sessions and physical cash has been collected.
2. Store Manager navigates to **Reports** $\rightarrow$ **Daily Z-Report**.
3. Generate and print the **Z-Report** showing:
   - Gross Sales, Net Sales, Total Discounts.
   - Total Tax Breakdown (CGST, SGST, IGST).
   - Tender breakdown (Total Cash, UPI, Card, Store Credit).
   - Total Returns and Cash Drawer Overage/Shortage.
4. Verify that the scheduled automatic database backup has executed successfully in the background.
