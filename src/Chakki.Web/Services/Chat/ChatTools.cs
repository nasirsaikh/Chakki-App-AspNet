using Chakki.Web.Data;
using Chakki.Web.Domain;
using Microsoft.EntityFrameworkCore;

namespace Chakki.Web.Services.Chat;

public record ChatTable(string[] Columns, List<string[]> Rows);

public record ToolResult(string Summary, ChatTable? Table = null);

/// <summary>
/// Read-only questions the assistant may ask of the data. Every query runs through the tenant-filtered DbContext,
/// so the assistant (rules or LLM) can only ever see the signed-in chakki's records.
/// </summary>
public class ChatTools(AppDbContext db, LedgerService ledger)
{
    public static (DateOnly From, DateOnly To) Period(string? period)
    {
        var today = DateOnly.FromDateTime(DateTime.Today);
        return (period ?? "today").ToLowerInvariant() switch
        {
            "yesterday" => (today.AddDays(-1), today.AddDays(-1)),
            "week" => (today.AddDays(-6), today),
            "month" => (new DateOnly(today.Year, today.Month, 1), today),
            "year" => (new DateOnly(today.Year, 1, 1), today),
            _ => (today, today),
        };
    }

    public async Task<ToolResult> SummaryAsync(string? period, bool hi)
    {
        var (from, to) = Period(period);
        var start = from.ToDateTime(TimeOnly.MinValue);
        var end = to.ToDateTime(TimeOnly.MaxValue);
        var lines = await db.GrindingOrderLines.Where(l => db.GrindingOrders.Any(o => o.Id == l.GrindingOrderId && o.Status != OrderStatus.Cancelled && o.CreatedAt >= start.ToUniversalTime() && o.CreatedAt <= end.ToUniversalTime()))
            .Select(l => new { l.WeightKg, l.Amount }).ToListAsync();
        var tb = await ledger.TrialBalanceAsync(from, to);
        decimal Bal(string code) => tb.First(t => t.Account.Code == code).Balance;
        var income = tb.Where(t => t.Account.Type == AccountType.Income).Sum(t => t.Balance);
        var expense = tb.Where(t => t.Account.Type == AccountType.Expense).Sum(t => t.Balance);
        var cashIn = tb.First(t => t.Account.Code == SystemAccounts.Cash).Debit + tb.First(t => t.Account.Code == SystemAccounts.Bank).Debit;
        var udhaar = (await ledger.AllUdhaarAsync()).Values.Sum();
        var pending = await db.GrindingOrders.CountAsync(o => o.Status == OrderStatus.Intake || o.Status == OrderStatus.Grinding || o.Status == OrderStatus.Ready);
        var label = PeriodLabel(period, hi);
        var table = new ChatTable(
            hi ? ["विवरण", "मान"] : ["Item", "Value"],
            [
                [hi ? "पिसाई वजन" : "Grinding weight", $"{lines.Sum(l => l.WeightKg):0.##} kg"],
                [hi ? "पिसाई बिल" : "Grinding billed", Rs(Bal(SystemAccounts.GrindingIncome))],
                [hi ? "कुल आय" : "Total income", Rs(income)],
                [hi ? "कुल खर्च" : "Total expenses", Rs(expense)],
                [hi ? "शुद्ध आय" : "Net income", Rs(income - expense)],
                [hi ? "नकद/UPI प्राप्त" : "Cash/UPI received", Rs(cashIn)],
                [hi ? "कुल बकाया उधार" : "Outstanding Udhaar (all time)", Rs(udhaar)],
                [hi ? "लंबित ऑर्डर" : "Pending orders", pending.ToString()],
            ]);
        var summary = hi
            ? $"{label}: {lines.Sum(l => l.WeightKg):0.##} किलो पिसाई, आय {Rs(income)}, खर्च {Rs(expense)}, शुद्ध {Rs(income - expense)}। कुल उधार {Rs(udhaar)} बाकी है और {pending} ऑर्डर लंबित हैं।"
            : $"{label}: {lines.Sum(l => l.WeightKg):0.##} kg ground, income {Rs(income)}, expenses {Rs(expense)}, net {Rs(income - expense)}. Udhaar outstanding is {Rs(udhaar)} and {pending} orders are pending.";
        return new(summary, table);
    }

    public async Task<ToolResult> CustomerBalanceAsync(string name, bool hi)
    {
        var term = name.Trim();
        var matches = await db.Customers.Where(c => c.Name.Contains(term) || (c.Phone != null && c.Phone.Contains(term))).Take(5).ToListAsync();
        if (matches.Count == 0)
            return new(hi ? $"\"{term}\" नाम का कोई ग्राहक नहीं मिला।" : $"No customer matches \"{term}\".");
        var all = await ledger.AllUdhaarAsync();
        var rows = matches.Select(c => new[] { c.Name, c.Village ?? "", Rs(all.GetValueOrDefault(c.Id)) }).ToList();
        var first = matches[0];
        var bal = all.GetValueOrDefault(first.Id);
        var summary = hi ? $"{first.Name} का बकाया उधार {Rs(bal)} है।" : $"{first.Name} owes {Rs(bal)} in Udhaar.";
        if (matches.Count > 1) summary += hi ? $" ({matches.Count} मिलते-जुलते ग्राहक नीचे हैं।)" : $" ({matches.Count} similar customers are listed below.)";
        return new(summary, new ChatTable(hi ? ["ग्राहक", "गाँव", "उधार"] : ["Customer", "Village", "Udhaar"], rows));
    }

    public async Task<ToolResult> TopDebtorsAsync(int count, bool hi)
    {
        var all = await ledger.AllUdhaarAsync();
        var top = all.Where(kv => kv.Value > 0).OrderByDescending(kv => kv.Value).Take(Math.Clamp(count, 1, 25)).ToList();
        var ids = top.Select(t => t.Key).ToList();
        var names = await db.Customers.Where(c => ids.Contains(c.Id)).ToDictionaryAsync(c => c.Id);
        var rows = top.Select(t => new[] { names[t.Key].Name, names[t.Key].Village ?? "", names[t.Key].Phone ?? "", Rs(t.Value) }).ToList();
        if (rows.Count == 0) return new(hi ? "किसी ग्राहक का उधार बाकी नहीं है।" : "No customer has outstanding Udhaar.");
        return new(hi ? $"सबसे ज़्यादा उधार वाले {rows.Count} ग्राहक, कुल {Rs(top.Sum(t => t.Value))}।" : $"Top {rows.Count} customers by Udhaar, together {Rs(top.Sum(t => t.Value))}.",
            new ChatTable(hi ? ["ग्राहक", "गाँव", "फ़ोन", "उधार"] : ["Customer", "Village", "Phone", "Udhaar"], rows));
    }

    public async Task<ToolResult> PendingOrdersAsync(bool hi)
    {
        var orders = await db.GrindingOrders.Include(o => o.Customer).Include(o => o.Lines)
            .Where(o => o.Status == OrderStatus.Intake || o.Status == OrderStatus.Grinding || o.Status == OrderStatus.Ready)
            .OrderBy(o => o.CreatedAt).Take(25).ToListAsync();
        if (orders.Count == 0) return new(hi ? "कोई ऑर्डर लंबित नहीं है।" : "There are no pending orders.");
        var rows = orders.Select(o => new[] { o.Ref, o.Customer!.Name, $"{o.Lines.Sum(l => l.WeightKg):0.##} kg", StatusText(o.Status, hi), Rs(o.Total) }).ToList();
        var ready = orders.Count(o => o.Status == OrderStatus.Ready);
        return new(hi ? $"{orders.Count} ऑर्डर लंबित हैं, जिनमें {ready} तैयार हैं।" : $"{orders.Count} orders are pending; {ready} are ready for pickup.",
            new ChatTable(hi ? ["ऑर्डर", "ग्राहक", "वजन", "स्थिति", "राशि"] : ["Order", "Customer", "Weight", "Status", "Amount"], rows));
    }

    public async Task<ToolResult> StockAsync(bool lowOnly, bool hi)
    {
        var items = (await db.InventoryItems.OrderBy(i => i.Name).ToListAsync())
            .Where(i => !lowOnly || i.Quantity <= i.ReorderLevel).ToList();
        if (items.Count == 0) return new(hi ? (lowOnly ? "कोई सामान कम स्टॉक में नहीं है।" : "स्टॉक में कोई सामान नहीं है।") : (lowOnly ? "Nothing is below its reorder level." : "No stock items yet."));
        var rows = items.Select(i => new[] { hi && i.NameHi != null ? i.NameHi : i.Name, $"{i.Quantity:0.##} {i.Unit}", $"{i.ReorderLevel:0.##}", Rs(i.Quantity * i.AvgCost) }).ToList();
        return new(hi ? $"{items.Count} सामान{(lowOnly ? " कम स्टॉक में" : "")}। कुल मूल्य {Rs(items.Sum(i => i.Quantity * i.AvgCost))}।"
                      : $"{items.Count} item(s){(lowOnly ? " below reorder level" : "")}. Stock value {Rs(items.Sum(i => i.Quantity * i.AvgCost))}.",
            new ChatTable(hi ? ["सामान", "मात्रा", "न्यूनतम", "मूल्य"] : ["Item", "Quantity", "Reorder at", "Value"], rows));
    }

    public async Task<ToolResult> ExpensesAsync(string? period, bool hi)
    {
        var (from, to) = Period(period);
        var tb = await ledger.TrialBalanceAsync(from, to);
        var rows = tb.Where(t => t.Account.Type == AccountType.Expense && t.Balance != 0)
            .Select(t => new[] { hi && t.Account.NameHi != null ? t.Account.NameHi : t.Account.Name, Rs(t.Balance) }).ToList();
        var total = tb.Where(t => t.Account.Type == AccountType.Expense).Sum(t => t.Balance);
        return new(hi ? $"{PeriodLabel(period, true)} कुल खर्च {Rs(total)}।" : $"Expenses {PeriodLabel(period, false).ToLowerInvariant()}: {Rs(total)}.",
            rows.Count > 0 ? new ChatTable(hi ? ["खर्च", "राशि"] : ["Expense", "Amount"], rows) : null);
    }

    public async Task<ToolResult> MaintenanceDueAsync(bool hi)
    {
        var soon = DateOnly.FromDateTime(DateTime.Today.AddDays(7));
        var due = await db.MaintenanceTasks.Where(m => m.Status == MaintenanceStatus.Scheduled && m.DueOn <= soon).OrderBy(m => m.DueOn).ToListAsync();
        if (due.Count == 0) return new(hi ? "अगले 7 दिनों में कोई मरम्मत/टकाई बाकी नहीं है।" : "No maintenance is due in the next 7 days.");
        return new(hi ? $"{due.Count} काम अगले 7 दिनों में बाकी हैं।" : $"{due.Count} maintenance task(s) due within 7 days.",
            new ChatTable(hi ? ["काम", "प्रकार", "तारीख"] : ["Task", "Kind", "Due"], due.Select(m => new[] { m.Title, m.Kind, m.DueOn.ToString("dd MMM") }).ToList()));
    }

    public static string Rs(decimal v) => Infrastructure.Fmt.Rs(v);

    public static string PeriodLabel(string? period, bool hi) => (period ?? "today").ToLowerInvariant() switch
    {
        "yesterday" => hi ? "कल" : "Yesterday",
        "week" => hi ? "पिछले 7 दिन" : "Last 7 days",
        "month" => hi ? "इस महीने" : "This month",
        "year" => hi ? "इस साल" : "This year",
        _ => hi ? "आज" : "Today",
    };

    public static string StatusText(OrderStatus s, bool hi) => (s, hi) switch
    {
        (OrderStatus.Intake, true) => "जमा",
        (OrderStatus.Grinding, true) => "पिसाई में",
        (OrderStatus.Ready, true) => "तैयार",
        (OrderStatus.Delivered, true) => "दे दिया",
        (OrderStatus.Cancelled, true) => "रद्द",
        (OrderStatus.Intake, false) => "Received",
        (OrderStatus.Grinding, false) => "Grinding",
        (OrderStatus.Ready, false) => "Ready",
        (OrderStatus.Delivered, false) => "Delivered",
        _ => "Cancelled",
    };
}
