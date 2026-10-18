using System;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using RetailPOS.Client.Services;
using RetailPOS.Contracts.CashSessions;
using RetailPOS.Contracts.Reports;
using RetailPOS.Domain.Enums;

namespace RetailPOS.Client.ViewModels;

public sealed partial class ShiftTransactionItemViewModel : ObservableObject
{
    [ObservableProperty]
    public partial long Id { get; set; }

    [ObservableProperty]
    public partial string TypeText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Reason { get; set; } = string.Empty;

    [ObservableProperty]
    public partial long AmountPaise { get; set; }

    [ObservableProperty]
    public partial string FormattedAmount { get; set; } = "₹0.00";

    [ObservableProperty]
    public partial string FormattedTimestamp { get; set; } = string.Empty;
}

public sealed partial class RecentTerminalTransactionItemViewModel : ObservableObject
{
    [ObservableProperty]
    public partial DateTime TimestampUtc { get; set; }

    [ObservableProperty]
    public partial string FormattedTimestamp { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string TransactionType { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string ReferenceNumber { get; set; } = string.Empty;

    [ObservableProperty]
    public partial long AmountPaise { get; set; }

    [ObservableProperty]
    public partial string FormattedAmount { get; set; } = "₹0.00";

    [ObservableProperty]
    public partial string CashierName { get; set; } = string.Empty;
}

public sealed partial class ShiftReconciliationViewModel : ObservableObject
{
    private static readonly Regex RupeesRegex = new(@"^\d+(\.\d{1,2})?$", RegexOptions.Compiled);

    [ObservableProperty]
    public partial Guid? SessionId { get; set; }

    [ObservableProperty]
    public partial Guid? TerminalId { get; set; }

    [ObservableProperty]
    public partial string TerminalCode { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string TerminalName { get; set; } = string.Empty;

    [ObservableProperty]
    public partial Guid? CashierUserId { get; set; }

    [ObservableProperty]
    public partial string CashierName { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string ActiveCashierUsername { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string? ClosedByName { get; set; }

    [ObservableProperty]
    public partial string SessionStatus { get; set; } = "No Active Session";

    [ObservableProperty]
    public partial string FormattedSessionStartTime { get; set; } = "-";

    [ObservableProperty]
    public partial string FormattedSessionEndTime { get; set; } = "-";

    // Cash Reconciliation Components
    [ObservableProperty]
    public partial long OpeningFloatPaise { get; set; }
    [ObservableProperty]
    public partial string FormattedOpeningFloat { get; set; } = "₹0.00";

    [ObservableProperty]
    public partial long TotalCashSalesPaise { get; set; }
    [ObservableProperty]
    public partial string FormattedTotalCashSales { get; set; } = "₹0.00";

    [ObservableProperty]
    public partial long TotalCashRepaymentsPaise { get; set; }
    [ObservableProperty]
    public partial string FormattedTotalCashRepayments { get; set; } = "₹0.00";

    [ObservableProperty]
    public partial long TotalPayInsPaise { get; set; }
    [ObservableProperty]
    public partial string FormattedTotalPayIns { get; set; } = "₹0.00";

    [ObservableProperty]
    public partial long TotalCashRefundsPaise { get; set; }
    [ObservableProperty]
    public partial string FormattedTotalCashRefunds { get; set; } = "₹0.00";

    [ObservableProperty]
    public partial long TotalPayOutsPaise { get; set; }
    [ObservableProperty]
    public partial string FormattedTotalPayOuts { get; set; } = "₹0.00";

    [ObservableProperty]
    public partial long TotalCashDropsPaise { get; set; }
    [ObservableProperty]
    public partial string FormattedTotalCashDrops { get; set; } = "₹0.00";

    [ObservableProperty]
    public partial long CurrentExpectedCashPaise { get; set; }
    [ObservableProperty]
    public partial string FormattedCurrentExpectedCash { get; set; } = "₹0.00";

    [ObservableProperty]
    public partial long? ClosingCashPaise { get; set; }
    [ObservableProperty]
    public partial string FormattedClosingCash { get; set; } = "-";

    // Discrepancy & Counted Cash Audit
    [ObservableProperty]
    public partial string CountedCashInput { get; set; } = string.Empty;

    [ObservableProperty]
    public partial long? DiscrepancyPaise { get; set; }
    [ObservableProperty]
    public partial string FormattedDiscrepancy { get; set; } = "₹0.00";

    [ObservableProperty]
    public partial string DiscrepancyStatus { get; set; } = "Balanced (₹0.00)";

    // Management Audit Slips
    [ObservableProperty]
    public partial string SlipPreviewText { get; set; } = string.Empty;

    // Terminal Snapshot Activity Today
    [ObservableProperty]
    public partial int TodayBillsCount { get; set; }

    [ObservableProperty]
    public partial long TodayGrossSalesPaise { get; set; }
    [ObservableProperty]
    public partial string FormattedTodayGrossSales { get; set; } = "₹0.00";

    [ObservableProperty]
    public partial long TodayNetSalesPaise { get; set; }
    [ObservableProperty]
    public partial string FormattedTodayNetSales { get; set; } = "₹0.00";

    [ObservableProperty]
    public partial long TodayTaxPaise { get; set; }
    [ObservableProperty]
    public partial string FormattedTodayTax { get; set; } = "₹0.00";

    [ObservableProperty]
    public partial int TodayRefundsCount { get; set; }
    [ObservableProperty]
    public partial long TodayRefundsPaise { get; set; }
    [ObservableProperty]
    public partial string FormattedTodayRefunds { get; set; } = "₹0.00";

    public ObservableCollection<ShiftTransactionItemViewModel> Transactions { get; } = new();
    public ObservableCollection<RecentTerminalTransactionItemViewModel> RecentTransactions { get; } = new();

    partial void OnCountedCashInputChanged(string value)
    {
        RecalculateCountedCash();
    }

    private void RecalculateCountedCash()
    {
        if (string.IsNullOrWhiteSpace(CountedCashInput))
        {
            DiscrepancyPaise = null;
            FormattedDiscrepancy = "—";
            DiscrepancyStatus = "Enter Counted Cash";
            return;
        }

        var trimmed = CountedCashInput.Trim();
        if (!RupeesRegex.IsMatch(trimmed) || !decimal.TryParse(trimmed, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var countedRupees))
        {
            DiscrepancyPaise = null;
            FormattedDiscrepancy = "Invalid";
            DiscrepancyStatus = "Invalid Format";
            return;
        }

        long countedPaise = (long)Math.Round(countedRupees * 100.0m, MidpointRounding.AwayFromZero);
        // Formula: Discrepancy = Counted Cash - Expected Cash
        long discrepancy = countedPaise - CurrentExpectedCashPaise;
        DiscrepancyPaise = discrepancy;
        FormattedDiscrepancy = FormatCurrency(discrepancy);

        if (discrepancy == 0)
        {
            DiscrepancyStatus = "Exact Match (Balanced)";
        }
        else if (discrepancy > 0)
        {
            DiscrepancyStatus = string.Format(CultureInfo.InvariantCulture, "Cash Overage (+{0})", FormatCurrency(discrepancy));
        }
        else
        {
            DiscrepancyStatus = string.Format(CultureInfo.InvariantCulture, "Cash Shortage ({0})", FormatCurrency(discrepancy));
        }
    }

    public void PopulateFromSession(CashSessionDto session)
    {
        ArgumentNullException.ThrowIfNull(session);

        SessionId = session.Id;
        TerminalId = session.TerminalId;
        CashierUserId = session.CashierUserId;
        SessionStatus = session.Status == CashSessionStatus.Open ? "OPEN / ACTIVE" : "CLOSED";

        FormattedSessionStartTime = session.OpeningTimeUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
        FormattedSessionEndTime = session.ClosingTimeUtc.HasValue
            ? session.ClosingTimeUtc.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)
            : "-";

        OpeningFloatPaise = session.OpeningFloatPaise;
        FormattedOpeningFloat = FormatCurrency(session.OpeningFloatPaise);

        TotalCashSalesPaise = session.TotalCashSalesPaise;
        FormattedTotalCashSales = FormatCurrency(session.TotalCashSalesPaise);

        TotalCashRepaymentsPaise = session.TotalCashRepaymentsPaise;
        FormattedTotalCashRepayments = FormatCurrency(session.TotalCashRepaymentsPaise);

        TotalPayInsPaise = session.TotalPayInsPaise;
        FormattedTotalPayIns = FormatCurrency(session.TotalPayInsPaise);

        TotalCashRefundsPaise = session.TotalCashRefundsPaise;
        FormattedTotalCashRefunds = FormatCurrency(session.TotalCashRefundsPaise);

        TotalPayOutsPaise = session.TotalPayOutsPaise;
        FormattedTotalPayOuts = FormatCurrency(session.TotalPayOutsPaise);

        TotalCashDropsPaise = session.TotalCashDropsPaise;
        FormattedTotalCashDrops = FormatCurrency(session.TotalCashDropsPaise);

        CurrentExpectedCashPaise = session.ExpectedCashPaise;
        FormattedCurrentExpectedCash = FormatCurrency(session.ExpectedCashPaise);

        ClosingCashPaise = session.ClosingCashPaise;
        FormattedClosingCash = session.ClosingCashPaise.HasValue
            ? FormatCurrency(session.ClosingCashPaise.Value)
            : "-";

        if (session.DiscrepancyPaise.HasValue)
        {
            DiscrepancyPaise = session.DiscrepancyPaise;
            FormattedDiscrepancy = FormatCurrency(session.DiscrepancyPaise.Value);
            DiscrepancyStatus = session.DiscrepancyPaise.Value == 0
                ? "Balanced (₹0.00)"
                : session.DiscrepancyPaise.Value > 0
                    ? string.Format(CultureInfo.InvariantCulture, "Over (+{0})", FormatCurrency(session.DiscrepancyPaise.Value))
                    : string.Format(CultureInfo.InvariantCulture, "Short ({0})", FormatCurrency(session.DiscrepancyPaise.Value));
        }
        else
        {
            RecalculateCountedCash();
        }
    }

    public void PopulateFromTerminalDto(CurrentTerminalReportDto dto)
    {
        ArgumentNullException.ThrowIfNull(dto);

        TerminalId = dto.TerminalId;
        TerminalCode = dto.TerminalCode ?? string.Empty;
        TerminalName = dto.TerminalName ?? string.Empty;
        SessionId = dto.ActiveSessionId;
        ActiveCashierUsername = dto.ActiveCashierUsername ?? "No Active Cashier";
        SessionStatus = dto.SessionStatus ?? "Unknown";

        FormattedSessionStartTime = dto.SessionStartTimeUtc.HasValue
            ? dto.SessionStartTimeUtc.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)
            : "-";

        OpeningFloatPaise = dto.OpeningFloatPaise;
        FormattedOpeningFloat = FormatCurrency(dto.OpeningFloatPaise);

        CurrentExpectedCashPaise = dto.CurrentExpectedCashPaise;
        FormattedCurrentExpectedCash = FormatCurrency(dto.CurrentExpectedCashPaise);

        TotalCashSalesPaise = dto.TotalCashSalesPaise;
        FormattedTotalCashSales = FormatCurrency(dto.TotalCashSalesPaise);

        TotalCashRepaymentsPaise = dto.TotalCashRepaymentsPaise;
        FormattedTotalCashRepayments = FormatCurrency(dto.TotalCashRepaymentsPaise);

        TotalPayInsPaise = dto.TotalPayInsPaise;
        FormattedTotalPayIns = FormatCurrency(dto.TotalPayInsPaise);

        TotalCashRefundsPaise = dto.TotalCashRefundsPaise;
        FormattedTotalCashRefunds = FormatCurrency(dto.TotalCashRefundsPaise);

        TotalPayOutsPaise = dto.TotalPayOutsPaise;
        FormattedTotalPayOuts = FormatCurrency(dto.TotalPayOutsPaise);

        TotalCashDropsPaise = dto.TotalCashDropsPaise;
        FormattedTotalCashDrops = FormatCurrency(dto.TotalCashDropsPaise);

        TodayBillsCount = dto.TodayBillsCount;
        TodayGrossSalesPaise = dto.TodayGrossSalesPaise;
        FormattedTodayGrossSales = FormatCurrency(dto.TodayGrossSalesPaise);

        TodayNetSalesPaise = dto.TodayNetSalesPaise;
        FormattedTodayNetSales = FormatCurrency(dto.TodayNetSalesPaise);

        TodayTaxPaise = dto.TodayTaxPaise;
        FormattedTodayTax = FormatCurrency(dto.TodayTaxPaise);

        TodayRefundsCount = dto.TodayRefundsCount;
        TodayRefundsPaise = dto.TodayRefundsPaise;
        FormattedTodayRefunds = FormatCurrency(dto.TodayRefundsPaise);

        RecentTransactions.Clear();
        if (dto.RecentTransactions != null)
        {
            foreach (var tx in dto.RecentTransactions)
            {
                RecentTransactions.Add(new RecentTerminalTransactionItemViewModel
                {
                    TimestampUtc = tx.TimestampUtc,
                    FormattedTimestamp = tx.TimestampUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
                    TransactionType = tx.TransactionType ?? string.Empty,
                    ReferenceNumber = tx.ReferenceNumber ?? string.Empty,
                    AmountPaise = tx.AmountPaise,
                    FormattedAmount = FormatCurrency(tx.AmountPaise),
                    CashierName = tx.CashierName ?? string.Empty
                });
            }
        }
    }

    public async Task LoadActiveSessionAsync(PosApiClient client, Guid terminalId, string? token, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(client);

        var sessionRes = await client.GetActiveCashSessionAsync(terminalId, token, ct);
        if (sessionRes.Success && sessionRes.Data != null)
        {
            PopulateFromSession(sessionRes.Data);
        }
        else
        {
            // Try loading terminal snapshot
            var termRes = await client.GetCurrentTerminalReportAsync(terminalId, token, ct);
            if (termRes.Success && termRes.Data != null)
            {
                PopulateFromTerminalDto(termRes.Data);
            }
        }
    }

    public async Task FetchXReportAsync(PosApiClient client, string? token, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        if (!SessionId.HasValue || SessionId.Value == Guid.Empty)
        {
            SlipPreviewText = "No active session ID found to generate X-Report.";
            return;
        }

        var res = await client.GetXReportAsync(SessionId.Value, token, ct);
        if (res.Success && res.Data != null)
        {
            PopulateFromReport(res.Data, "X-REPORT (MID-DAY SNAPSHOT)");
        }
        else
        {
            SlipPreviewText = string.Format(CultureInfo.InvariantCulture, "Failed to load X-Report: {0}", res.ErrorMessage ?? "Unknown error");
        }
    }

    public async Task FetchZReportAsync(PosApiClient client, string? token, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        if (!SessionId.HasValue || SessionId.Value == Guid.Empty)
        {
            SlipPreviewText = "No session ID found to generate Z-Report.";
            return;
        }

        var res = await client.GetZReportAsync(SessionId.Value, token, ct);
        if (res.Success && res.Data != null)
        {
            PopulateFromReport(res.Data, "Z-REPORT (END-OF-DAY CLOSEOUT)");
        }
        else
        {
            SlipPreviewText = string.Format(CultureInfo.InvariantCulture, "Failed to load Z-Report: {0}", res.ErrorMessage ?? "Unknown error");
        }
    }

    public void PopulateFromReport(CashSessionReportDto report, string slipType)
    {
        ArgumentNullException.ThrowIfNull(report);
        PopulateFromSession(report.Session);

        CashierName = report.CashierName;
        ClosedByName = report.ClosedByName;
        TerminalName = report.TerminalName;

        Transactions.Clear();
        if (report.Transactions != null)
        {
            foreach (var tx in report.Transactions)
            {
                Transactions.Add(new ShiftTransactionItemViewModel
                {
                    Id = tx.Id,
                    TypeText = tx.Type.ToString(),
                    Reason = tx.Reason,
                    AmountPaise = tx.AmountPaise,
                    FormattedAmount = FormatCurrency(tx.AmountPaise),
                    FormattedTimestamp = tx.TimestampUtc.ToLocalTime().ToString("t", CultureInfo.InvariantCulture)
                });
            }
        }

        GenerateSlipText(report, slipType);
    }

    private void GenerateSlipText(CashSessionReportDto report, string slipType)
    {
        var sb = new StringBuilder();
        var sep = new string('=', 38);
        var subsep = new string('-', 38);

        sb.AppendLine(sep);
        sb.AppendLine(CenterText(string.Format(CultureInfo.InvariantCulture, "GROZET RETAILPOS — {0}", slipType.ToUpperInvariant()), 38));
        sb.AppendLine(sep);
        sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "Terminal : {0}", report.TerminalName));
        sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "Cashier  : {0}", report.CashierName));
        if (!string.IsNullOrEmpty(report.ClosedByName))
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "Closed By: {0}", report.ClosedByName));
        sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "Opened   : {0:yyyy-MM-dd HH:mm:ss}", report.Session.OpeningTimeUtc.ToLocalTime()));
        if (report.Session.ClosingTimeUtc.HasValue)
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "Closed   : {0:yyyy-MM-dd HH:mm:ss}", report.Session.ClosingTimeUtc.Value.ToLocalTime()));
        sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "Status   : {0}", report.Session.Status));
        sb.AppendLine(subsep);
        sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "Opening Float       : {0,16}", FormatCurrency(report.Session.OpeningFloatPaise)));
        sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "+ Cash Sales        : {0,16}", FormatCurrency(report.Session.TotalCashSalesPaise)));
        sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "+ Cash Repayments   : {0,16}", FormatCurrency(report.Session.TotalCashRepaymentsPaise)));
        sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "+ Pay-Ins           : {0,16}", FormatCurrency(report.Session.TotalPayInsPaise)));
        sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "- Cash Refunds      : {0,16}", FormatCurrency(report.Session.TotalCashRefundsPaise)));
        sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "- Pay-Outs          : {0,16}", FormatCurrency(report.Session.TotalPayOutsPaise)));
        sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "- Cash Drops        : {0,16}", FormatCurrency(report.Session.TotalCashDropsPaise)));
        sb.AppendLine(subsep);
        sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "EXPECTED CASH       : {0,16}", FormatCurrency(report.Session.ExpectedCashPaise)));
        if (report.Session.ClosingCashPaise.HasValue)
        {
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "COUNTED CASH        : {0,16}", FormatCurrency(report.Session.ClosingCashPaise.Value)));
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "DISCREPANCY         : {0,16}", FormatCurrency(report.Session.DiscrepancyPaise ?? 0)));
        }
        sb.AppendLine(sep);

        if (report.Transactions != null && report.Transactions.Count > 0)
        {
            sb.AppendLine("CASH MOVEMENTS:");
            foreach (var tx in report.Transactions)
            {
                sb.AppendLine(string.Format(CultureInfo.InvariantCulture, " {0:HH:mm} {1,-10} {2,10}", tx.TimestampUtc.ToLocalTime(), tx.Type, FormatCurrency(tx.AmountPaise)));
                if (!string.IsNullOrWhiteSpace(tx.Reason))
                    sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "   Reason: {0}", tx.Reason));
            }
            sb.AppendLine(sep);
        }

        sb.AppendLine(CenterText("*** END OF REPORT ***", 38));
        sb.AppendLine(sep);

        SlipPreviewText = sb.ToString();
    }

    private static string CenterText(string text, int width)
    {
        if (text.Length >= width) return text;
        int left = (width - text.Length) / 2;
        return new string(' ', left) + text;
    }

    public static string FormatCurrency(long paise)
    {
        if (paise < 0)
        {
            return string.Format(CultureInfo.InvariantCulture, "-₹{0:N2}", Math.Abs(paise) / 100.0m);
        }
        return string.Format(CultureInfo.InvariantCulture, "₹{0:N2}", paise / 100.0m);
    }
}
