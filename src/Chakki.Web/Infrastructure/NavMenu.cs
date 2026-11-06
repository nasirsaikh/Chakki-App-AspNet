using System.Security.Claims;
using Chakki.Web.Domain;
using Chakki.Web.Tenancy;

namespace Chakki.Web.Infrastructure;

public record NavItem(string Href, string Icon, string Label);
public record NavSection(string Title, List<NavItem> Items);

/// <summary>The module list, filtered by role. Used by the side menu and by global search.</summary>
public static class NavMenu
{
    public static List<NavSection> For(ClaimsPrincipal user)
    {
        bool Any(params string[] roles) => roles.Any(user.IsInRole);
        var tenant = user.HasClaim(c => c.Type == TenantClaims.OrganizationId);
        var floor = tenant && Any(Roles.Owner, Roles.Manager, Roles.Operator);
        var money = tenant && Any(Roles.Owner, Roles.Manager, Roles.Accountant);
        var books = tenant && Any(Roles.Owner, Roles.Accountant);
        var people = tenant && Any(Roles.Owner, Roles.Manager);
        (string Title, (string Href, string Icon, string Label, bool Show)[] Items)[] all =
        [
            ("Daily work", [
                ("/", "bi-speedometer2", "Dashboard", tenant),
                ("/orders", "bi-basket", "Grinding orders", tenant),
                ("/orders/new", "bi-plus-lg", "New order", floor),
                ("/customers", "bi-people", "Customers", tenant),
                ("/udhaar", "bi-journal-text", "Udhaar", money || floor),
                ("/udhaar/old", "bi-journal-bookmark", "Old Udhaar", tenant),
                ("/payments", "bi-qr-code", "Payments", money || floor),
                ("/chat", "bi-chat-dots", "Ask the assistant", tenant),
            ]),
            ("Stock", [
                ("/inventory", "bi-box-seam", "Inventory", tenant),
                ("/purchases", "bi-truck", "Purchases", money),
                ("/sales", "bi-cart", "Sales", tenant),
                ("/buyback", "bi-arrow-left-right", "Buyback", floor || money),
                ("/production", "bi-gear-wide-connected", "Production", floor),
                ("/wastage", "bi-trash3", "Wastage", floor),
                ("/suppliers", "bi-shop", "Suppliers", money),
            ]),
            ("Money", [
                ("/accounts", "bi-bank", "Accounts", books),
                ("/utilities", "bi-lightning-charge", "Utilities", money),
                ("/reports", "bi-graph-up", "Reports", money),
                ("/reports/trial-balance", "bi-list-columns", "Trial balance", money),
            ]),
            ("People & machines", [
                ("/employees", "bi-person-badge", "Employees", people),
                ("/attendance", "bi-calendar-check", "Attendance", floor),
                ("/payroll", "bi-cash-stack", "Payroll", money),
                ("/maintenance", "bi-tools", "Maintenance", floor),
            ]),
            ("Setup", [
                ("/rates", "bi-tags", "Rate card", tenant),
                ("/settings", "bi-sliders", "Settings", tenant),
                ("/settings/payments", "bi-credit-card", "Payment gateways", tenant && user.IsInRole(Roles.Owner)),
                ("/users", "bi-person-gear", "Users", tenant && user.IsInRole(Roles.Owner)),
                ("/platform", "bi-buildings", "All chakkis", user.IsInRole(Roles.PlatformAdmin)),
            ]),
        ];
        return all.Select(s => new NavSection(s.Title, s.Items.Where(i => i.Show).Select(i => new NavItem(i.Href, i.Icon, i.Label)).ToList()))
            .Where(s => s.Items.Count > 0).ToList();
    }

    /// <summary>Items shown in the side menu (sub-pages such as "New order" are reached from their module).</summary>
    public static readonly HashSet<string> MenuOnly = ["/orders/new", "/udhaar/old", "/reports/trial-balance", "/settings/payments"];
}
