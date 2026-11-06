using System.Text.RegularExpressions;
using Chakki.Web.Data;
using Chakki.Web.Domain;
using Chakki.Web.Infrastructure;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Chakki.Web.Controllers;

public record SearchHit(string Href, string Icon, string Title, string? Subtitle);
public record SearchGroup(string Title, List<SearchHit> Hits);
public record SearchModel(string Q, List<SearchGroup> Groups);

/// <summary>
/// One search box for the whole app: pages, customers, orders, payments, stock, suppliers and employees.
/// Every query runs through the tenant filter, and record types are shown only to roles that can open them.
/// </summary>
public class SearchController(AppDbContext db) : AppController
{
    private const int Take = 6;

    [HttpGet("/search")]
    public async Task<IActionResult> Index(string? q)
    {
        q = (q ?? "").Trim();
        if (q.Length > 60) q = q[..60];
        var model = new SearchModel(q, q.Length == 0 ? [] : await SearchAsync(q));
        return IsHtmx ? PartialView("_Results", model) : View(model);
    }

    private async Task<List<SearchGroup>> SearchAsync(string q)
    {
        var groups = new List<SearchGroup>();
        var menu = NavMenu.For(User).SelectMany(s => s.Items).ToList();
        bool Can(string href) => menu.Any(i => i.Href == href);
        var like = $"%{q.Replace("%", "").Replace("_", "")}%";
        var lower = q.ToLowerInvariant();

        // Pages: match the English name or its Hindi translation.
        var pages = menu.Where(i => i.Label.Contains(q, StringComparison.OrdinalIgnoreCase) || T[i.Label].Value.Contains(q, StringComparison.OrdinalIgnoreCase))
            .Take(Take).Select(i => new SearchHit(i.Href, i.Icon, T[i.Label], null)).ToList();
        if (pages.Count > 0) groups.Add(new(T["Pages"], pages));

        // Orders: "G-00012", "12", or the customer's name.
        var number = Regex.Match(q, @"^(?:g-?)?0*(\d{1,7})$", RegexOptions.IgnoreCase) is { Success: true } m ? int.Parse(m.Groups[1].Value) : (int?)null;
        var orders = await db.GrindingOrders.Include(o => o.Customer)
            .Where(o => (number != null && o.Number == number) || EF.Functions.Like(o.Customer!.Name, like))
            .OrderByDescending(o => o.Id).Take(Take).ToListAsync();
        if (orders.Count > 0)
            groups.Add(new(T["Grinding orders"], orders.Select(o => new SearchHit($"/orders/{o.Id}", "bi-basket", $"{o.Ref} · {o.Customer?.Name}",
                $"{T[o.Status == OrderStatus.Intake ? "Received" : o.Status.ToString()]} · {Fmt.Rs(o.Total)}")).ToList()));

        var customers = await db.Customers
            .Where(c => EF.Functions.Like(c.Name, like) || (c.Phone != null && EF.Functions.Like(c.Phone, like)) || (c.Village != null && EF.Functions.Like(c.Village, like)))
            .OrderBy(c => c.Name).Take(Take).ToListAsync();
        if (customers.Count > 0)
            groups.Add(new(T["Customers"], customers.Select(c => new SearchHit($"/customers/{c.Id}", "bi-person", c.Name,
                string.Join(" · ", new[] { c.Village, c.Phone }.Where(s => !string.IsNullOrWhiteSpace(s))))).ToList()));

        if (Can("/payments") && q.Length >= 4)
        {
            var payments = await db.Payments.Include(p => p.GrindingOrder)
                .Where(p => EF.Functions.Like(p.Reference, like) || (p.ProviderPaymentId != null && EF.Functions.Like(p.ProviderPaymentId, like)))
                .OrderByDescending(p => p.Id).Take(Take).ToListAsync();
            if (payments.Count > 0)
                groups.Add(new(T["Payments"], payments.Select(p => new SearchHit($"/orders/{p.GrindingOrderId}", "bi-qr-code", p.Reference,
                    $"{Fmt.Rs(p.Amount)} · {T[p.Status.ToString()]} · {p.GrindingOrder?.Ref}")).ToList()));
        }

        var items = (await db.InventoryItems.ToListAsync())
            .Where(i => i.Name.Contains(q, StringComparison.OrdinalIgnoreCase) || (i.NameHi?.Contains(q) ?? false)).Take(Take).ToList();
        if (items.Count > 0)
            groups.Add(new(T["Inventory"], items.Select(i => new SearchHit("/inventory", "bi-box-seam", Fmt.Name(i), $"{Fmt.Qty(i.Quantity)} {i.Unit}")).ToList()));

        if (Can("/suppliers"))
        {
            var suppliers = await db.Suppliers.Where(s => EF.Functions.Like(s.Name, like) || (s.Phone != null && EF.Functions.Like(s.Phone, like)))
                .OrderBy(s => s.Name).Take(Take).ToListAsync();
            if (suppliers.Count > 0) groups.Add(new(T["Suppliers"], suppliers.Select(s => new SearchHit("/suppliers", "bi-shop", s.Name, s.Phone)).ToList()));
        }

        if (Can("/employees"))
        {
            var employees = await db.Employees.Where(e => EF.Functions.Like(e.Name, like) || (e.Phone != null && EF.Functions.Like(e.Phone, like)))
                .OrderBy(e => e.Name).Take(Take).ToListAsync();
            if (employees.Count > 0) groups.Add(new(T["Employees"], employees.Select(e => new SearchHit("/employees", "bi-person-badge", e.Name, e.Role)).ToList()));
        }
        return groups;
    }
}
