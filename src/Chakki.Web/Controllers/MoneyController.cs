using Chakki.Web.Data;
using Chakki.Web.Domain;
using Chakki.Web.Infrastructure;
using Chakki.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Chakki.Web.Controllers;

public record ReportModel(DateOnly From, DateOnly To, List<AccountBalance> Balances, decimal Kg, int Orders, List<(string Grain, decimal Kg, decimal Amount)> ByGrain);

public class MoneyController(AppDbContext db, LedgerService ledger, OperationsService ops) : AppController
{
    // ---------------------------------------------------------------- Accounts & journal (read-only ledger; corrections by reversal)
    [Authorize(Policy = Roles.Books), HttpGet("/accounts")]
    public async Task<IActionResult> Accounts()
    {
        ViewBag.Journals = await db.JournalEntries.Include(j => j.Lines).ThenInclude(l => l.Account).OrderByDescending(j => j.Id).Take(40).ToListAsync();
        return View(await ledger.TrialBalanceAsync());
    }

    [Authorize(Policy = Roles.Books), HttpGet("/accounts/{code}")]
    public async Task<IActionResult> Ledger(string code)
    {
        var account = await db.Accounts.FirstOrDefaultAsync(a => a.Code == code) ?? throw new KeyNotFoundException();
        ViewBag.Account = account;
        return View(await db.LedgerLines.Include(l => l.JournalEntry).Where(l => l.AccountId == account.Id).OrderBy(l => l.JournalEntry!.Date).ThenBy(l => l.Id).ToListAsync());
    }

    [Authorize(Policy = Roles.Books), HttpPost("/accounts/journal"), ValidateAntiForgeryToken]
    public async Task<IActionResult> Journal(DateOnly date, string memo, string debitCode, string creditCode, decimal amount)
    {
        if (string.IsNullOrWhiteSpace(memo)) throw new BusinessRuleException("Write what this entry is for.");
        if (debitCode == creditCode) throw new BusinessRuleException("Debit and credit accounts must be different.");
        amount = OrderService.Money(amount);
        if (amount <= 0) throw new BusinessRuleException("Amount must be positive.");
        var receivable = debitCode == SystemAccounts.Receivable || creditCode == SystemAccounts.Receivable;
        var payable = debitCode == SystemAccounts.Payable || creditCode == SystemAccounts.Payable;
        if (receivable || payable) throw new BusinessRuleException("Udhaar and supplier balances change only through their own screens.");
        await ledger.PostAsync(date == default ? Today : date, memo.Trim(), "manual", null, [new(debitCode, amount, 0), new(creditCode, 0, amount)], UserId);
        await db.SaveChangesAsync();
        Flash(T["Journal entry posted."]);
        return Redirect("/accounts");
    }

    [Authorize(Policy = Roles.Books), HttpPost("/accounts/journal/{id:int}/reverse"), ValidateAntiForgeryToken]
    public async Task<IActionResult> Reverse(int id, string reason)
    {
        if (string.IsNullOrWhiteSpace(reason)) throw new BusinessRuleException("A reason is required to reverse an entry.");
        var j = await db.JournalEntries.FindAsync(id) ?? throw new KeyNotFoundException();
        if (j.Source != "manual") throw new BusinessRuleException("Only manual entries can be reversed here; other entries are corrected from their own screen.");
        await ledger.ReverseAsync(id, reason.Trim(), UserId);
        await db.SaveChangesAsync();
        Flash(T["Entry reversed."]);
        return Redirect("/accounts");
    }

    // ---------------------------------------------------------------- Utilities
    [Authorize(Policy = Roles.Money), HttpGet("/utilities")]
    public async Task<IActionResult> Utilities() => View(await db.UtilityExpenses.OrderByDescending(u => u.Date).ThenByDescending(u => u.Id).Take(100).ToListAsync());

    [Authorize(Policy = Roles.Money), HttpPost("/utilities"), ValidateAntiForgeryToken]
    public async Task<IActionResult> Utility(string kind, decimal amount, decimal? units, string? note, DateOnly? date)
    {
        await ops.UtilityAsync(string.IsNullOrWhiteSpace(kind) ? "Electricity" : kind, amount, units, note, date, UserId);
        Flash(T["Expense saved."]);
        return Redirect("/utilities");
    }

    // ---------------------------------------------------------------- Reports
    [Authorize(Policy = Roles.Money), HttpGet("/reports")]
    public async Task<IActionResult> Reports(DateOnly? from, DateOnly? to)
    {
        var f = from ?? new DateOnly(Today.Year, Today.Month, 1);
        var t = to ?? Today;
        var balances = await ledger.TrialBalanceAsync(f, t);
        var fu = f.ToDateTime(TimeOnly.MinValue).ToUniversalTime();
        var tu = t.ToDateTime(TimeOnly.MaxValue).ToUniversalTime();
        var lines = await db.GrindingOrderLines.Include(l => l.RateCard)
            .Where(l => db.GrindingOrders.Any(o => o.Id == l.GrindingOrderId && o.Status != OrderStatus.Cancelled && o.CreatedAt >= fu && o.CreatedAt <= tu)).ToListAsync();
        var orders = await db.GrindingOrders.CountAsync(o => o.Status != OrderStatus.Cancelled && o.CreatedAt >= fu && o.CreatedAt <= tu);
        var byGrain = lines.GroupBy(l => l.RateCard?.Grain ?? "?").Select(g => (g.Key, g.Sum(l => l.WeightKg), g.Sum(l => l.Amount))).OrderByDescending(g => g.Item2).ToList();
        return View(new ReportModel(f, t, balances, lines.Sum(l => l.WeightKg), orders, byGrain));
    }

    [Authorize(Policy = Roles.Money), HttpGet("/reports/trial-balance")]
    public async Task<IActionResult> TrialBalance(DateOnly? to) => View(await ledger.TrialBalanceAsync(null, to ?? Today));

    // ---------------------------------------------------------------- Payments
    [HttpGet("/payments")]
    public async Task<IActionResult> Payments(string? status)
    {
        var q = db.Payments.Include(p => p.GrindingOrder).ThenInclude(o => o!.Customer).AsQueryable();
        if (status == "pending") q = q.Where(p => p.Status == SettlementStatus.Pending);
        ViewBag.Status = status;
        return View(await q.OrderByDescending(p => p.Id).Take(200).ToListAsync());
    }
}
