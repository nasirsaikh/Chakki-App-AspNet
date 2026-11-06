using Chakki.Web.Data;
using Chakki.Web.Domain;
using Chakki.Web.Infrastructure;
using Chakki.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Chakki.Web.Controllers;

public record CustomerRow(Customer Customer, decimal Udhaar, int Orders);
public record CustomerListModel(List<CustomerRow> Rows, string? Q, string? Village, List<string> Villages);
public record CustomerDetailModel(Customer Customer, decimal Udhaar, List<GrindingOrder> Orders, List<OpeningBalance> Openings, List<LedgerLine> Ledger);

[Route("customers")]
public class CustomersController(AppDbContext db, LedgerService ledger) : AppController
{
    [HttpGet("")]
    public async Task<IActionResult> Index(string? q, string? village)
    {
        var query = db.Customers.AsQueryable();
        if (!string.IsNullOrWhiteSpace(q)) query = query.Where(c => c.Name.Contains(q) || (c.Phone != null && c.Phone.Contains(q)) || (c.Village != null && c.Village.Contains(q)));
        if (!string.IsNullOrWhiteSpace(village)) query = query.Where(c => c.Village == village);
        var customers = await query.OrderBy(c => c.Name).Take(300).ToListAsync();
        var udhaar = await ledger.AllUdhaarAsync();
        var counts = await db.GrindingOrders.GroupBy(o => o.CustomerId).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count);
        var villages = await db.Customers.Where(c => c.Village != null).Select(c => c.Village!).Distinct().OrderBy(v => v).ToListAsync();
        var model = new CustomerListModel(customers.Select(c => new CustomerRow(c, udhaar.GetValueOrDefault(c.Id), counts.GetValueOrDefault(c.Id))).ToList(), q, village, villages);
        return IsHtmx && Request.Headers["HX-Target"] == "customer-rows" ? PartialView("_Rows", model) : View(model);
    }

    [HttpGet("new")]
    public IActionResult New() => View("Edit", new Customer());

    [HttpGet("{id:int}/edit")]
    public async Task<IActionResult> Edit(int id) => View(await db.Customers.FindAsync(id) ?? throw new KeyNotFoundException());

    [HttpPost("save"), ValidateAntiForgeryToken]
    public async Task<IActionResult> Save(Customer input)
    {
        if (string.IsNullOrWhiteSpace(input.Name)) throw new BusinessRuleException("Name is required.");
        Customer c;
        if (input.Id == 0) { c = new Customer(); db.Customers.Add(c); }
        else c = await db.Customers.FindAsync(input.Id) ?? throw new KeyNotFoundException();
        c.Name = input.Name.Trim(); c.Phone = input.Phone?.Trim(); c.Village = input.Village?.Trim(); c.Address = input.Address?.Trim();
        c.Kind = input.Kind; c.Notes = input.Notes; c.IsActive = input.IsActive || input.Id == 0;
        await db.SaveChangesAsync();
        Flash(T["Customer saved."]);
        return Redirect($"/customers/{c.Id}");
    }

    [HttpGet("{id:int}")]
    public async Task<IActionResult> Detail(int id)
    {
        var c = await db.Customers.FindAsync(id) ?? throw new KeyNotFoundException();
        var orders = await db.GrindingOrders.Include(o => o.Lines).Where(o => o.CustomerId == id).OrderByDescending(o => o.Id).Take(50).ToListAsync();
        var openings = await db.OpeningBalances.Where(o => o.CustomerId == id).OrderBy(o => o.AsOf).ToListAsync();
        var lines = await db.LedgerLines.Include(l => l.JournalEntry).Where(l => l.CustomerId == id && l.Account!.Code == SystemAccounts.Receivable)
            .OrderByDescending(l => l.Id).Take(100).ToListAsync();
        return View(new CustomerDetailModel(c, await ledger.CustomerUdhaarAsync(id), orders, openings, lines));
    }
}

public record UdhaarRow(Customer Customer, decimal Balance, decimal OldUdhaar);

[Route("udhaar")]
public class UdhaarController(AppDbContext db, LedgerService ledger, OrderService orders, OperationsService ops) : AppController
{
    [HttpGet("")]
    public async Task<IActionResult> Index()
    {
        var all = await ledger.AllUdhaarAsync();
        var ids = all.Where(kv => kv.Value != 0).Select(kv => kv.Key).ToList();
        var customers = await db.Customers.Where(c => ids.Contains(c.Id)).ToListAsync();
        var old = (await db.OpeningBalances.ToListAsync()).GroupBy(o => o.CustomerId).ToDictionary(g => g.Key, g => g.Sum(o => o.Remaining));
        var rows = customers.Select(c => new UdhaarRow(c, all[c.Id], old.GetValueOrDefault(c.Id))).OrderByDescending(r => r.Balance).ToList();
        return View(rows);
    }

    [Authorize(Policy = "Member"), HttpPost("receive"), ValidateAntiForgeryToken]
    public async Task<IActionResult> Receive(int customerId, decimal amount)
    {
        await orders.ReceiveUdhaarAsync(customerId, amount, UserId);
        Flash(T["Receipt of {0} saved.", Fmt.Rs(amount)]);
        return Redirect(Request.Headers.Referer.ToString() is { Length: > 0 } r && r.Contains("/customers/") ? $"/customers/{customerId}" : "/udhaar");
    }

    [Authorize(Policy = Roles.Money), HttpPost("opening"), ValidateAntiForgeryToken]
    public async Task<IActionResult> Opening(int customerId, decimal amount, DateOnly asOf, string? note)
    {
        await ops.AddOpeningBalanceAsync(customerId, amount, asOf == default ? Today : asOf, note, UserId);
        Flash(T["Old Udhaar added."]);
        return Redirect("/udhaar/old");
    }

    [HttpGet("old")]
    public async Task<IActionResult> Old()
    {
        ViewBag.All = await db.Customers.OrderBy(c => c.Name).ToListAsync();
        return View(await db.OpeningBalances.Include(o => o.Customer).OrderByDescending(o => o.AsOf).ToListAsync());
    }
}
