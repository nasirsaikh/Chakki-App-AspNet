using Chakki.Web.Data;
using Chakki.Web.Domain;
using Chakki.Web.Infrastructure;
using Chakki.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Chakki.Web.Controllers;

public record DashboardStats(decimal TodayKg, decimal TodayIncome, decimal TodayExpense, decimal CashInHand, decimal Udhaar, int Pending, int Ready);
public record DashboardModel(DashboardStats Stats, List<GrindingOrder> Pending, List<InventoryItem> LowStock, List<MaintenanceTask> Maintenance, string[] Days, decimal[] Kg, decimal[] Income);

public class HomeController(AppDbContext db, LedgerService ledger) : AppController
{
    [Microsoft.AspNetCore.Authorization.Authorize, HttpGet("/")]
    public async Task<IActionResult> Index()
    {
        // A platform admin has no chakki of their own; send them to the organisation list.
        if (User.FindFirst(Tenancy.TenantClaims.OrganizationId) == null)
            return User.IsInRole(Roles.PlatformAdmin) ? Redirect("/platform") : Forbid();
        var stats = await StatsAsync();
        var pending = await db.GrindingOrders.Include(o => o.Customer).Include(o => o.Lines)
            .Where(o => o.Status == OrderStatus.Intake || o.Status == OrderStatus.Grinding || o.Status == OrderStatus.Ready)
            .OrderBy(o => o.CreatedAt).Take(8).ToListAsync();
        var low = (await db.InventoryItems.ToListAsync()).Where(i => i.Quantity <= i.ReorderLevel).Take(6).ToList();
        var soon = Today.AddDays(7);
        var maint = await db.MaintenanceTasks.Where(m => m.Status == MaintenanceStatus.Scheduled && m.DueOn <= soon).OrderBy(m => m.DueOn).Take(5).ToListAsync();

        // Seven-day processing chart
        var from = Today.AddDays(-6);
        var fromUtc = from.ToDateTime(TimeOnly.MinValue).ToUniversalTime();
        var lines = await db.GrindingOrderLines
            .Join(db.GrindingOrders.Where(o => o.Status != OrderStatus.Cancelled && o.CreatedAt >= fromUtc), l => l.GrindingOrderId, o => o.Id, (l, o) => new { o.CreatedAt, l.WeightKg, l.Amount })
            .ToListAsync();
        var days = Enumerable.Range(0, 7).Select(i => from.AddDays(i)).ToArray();
        var kg = days.Select(d => lines.Where(l => DateOnly.FromDateTime(l.CreatedAt.ToLocalTime()) == d).Sum(l => l.WeightKg)).ToArray();
        var income = days.Select(d => lines.Where(l => DateOnly.FromDateTime(l.CreatedAt.ToLocalTime()) == d).Sum(l => l.Amount)).ToArray();
        return View(new DashboardModel(stats, pending, low, maint, days.Select(d => d.ToString("dd MMM")).ToArray(), kg, income));
    }

    [HttpGet("/dashboard/stats")]
    public async Task<IActionResult> Stats() => PartialView("_Stats", await StatsAsync());

    private async Task<DashboardStats> StatsAsync()
    {
        var start = Today.ToDateTime(TimeOnly.MinValue).ToUniversalTime();
        var todayKg = (await db.GrindingOrderLines.Where(l => db.GrindingOrders.Any(o => o.Id == l.GrindingOrderId && o.CreatedAt >= start && o.Status != OrderStatus.Cancelled))
            .Select(l => l.WeightKg).ToListAsync()).Sum();
        var tb = await ledger.TrialBalanceAsync(Today, Today);
        var all = await ledger.TrialBalanceAsync();
        var cash = all.Where(a => a.Account.Code is SystemAccounts.Cash).Sum(a => a.Balance);
        var udhaar = (await ledger.AllUdhaarAsync()).Values.Where(v => v > 0).Sum();
        var pending = await db.GrindingOrders.CountAsync(o => o.Status == OrderStatus.Intake || o.Status == OrderStatus.Grinding);
        var ready = await db.GrindingOrders.CountAsync(o => o.Status == OrderStatus.Ready);
        return new DashboardStats(todayKg, tb.Where(a => a.Account.Type == AccountType.Income).Sum(a => a.Balance),
            tb.Where(a => a.Account.Type == AccountType.Expense).Sum(a => a.Balance), cash, udhaar, pending, ready);
    }
}
