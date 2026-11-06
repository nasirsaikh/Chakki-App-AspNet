using Chakki.Web.Data;
using Chakki.Web.Domain;
using Chakki.Web.Infrastructure;
using Chakki.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Chakki.Web.Controllers;

public record UserRow(AppUser User, string Role);
public record PlatformRow(Organization Org, int Users, int Orders);

public class SetupController(AppDbContext db, UserManager<AppUser> users, SecretProtector secrets) : AppController
{
    private Guid OrgId => db.CurrentOrganizationId;

    // ---------------------------------------------------------------- Rate card
    [HttpGet("/rates")]
    public async Task<IActionResult> Rates()
    {
        ViewBag.Taxes = await db.TaxRates.Where(t => t.IsActive).OrderBy(t => t.Name).ToListAsync();
        return View(await db.RateCards.Include(r => r.TaxRate).OrderByDescending(r => r.IsActive).ThenBy(r => r.Grain).ThenBy(r => r.Jalan).ToListAsync());
    }

    [Authorize(Policy = Roles.People), HttpPost("/rates/save"), ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveRate(int id, string grain, string service, string? serviceHi, bool jalan, decimal systemRate, decimal minRate, decimal maxRate, bool allowOverride, bool isActive = true, int? taxRateId = null)
    {
        if (string.IsNullOrWhiteSpace(grain) || string.IsNullOrWhiteSpace(service)) throw new BusinessRuleException("Grain and service are required.");
        if (systemRate <= 0) throw new BusinessRuleException("Rate must be more than zero.");
        if (allowOverride && (minRate > systemRate || maxRate < systemRate)) throw new BusinessRuleException("The rate must lie between the minimum and maximum.");
        RateCard r;
        if (id == 0) { r = new RateCard { EffectiveFrom = Today }; db.RateCards.Add(r); }
        else r = await db.RateCards.FindAsync(id) ?? throw new KeyNotFoundException();
        if (id != 0 && r.SystemRate != systemRate) r.EffectiveFrom = Today; // old orders keep their snapshot
        r.Grain = grain.Trim(); r.Service = service.Trim(); r.ServiceHi = serviceHi?.Trim(); r.Jalan = jalan; r.SystemRate = systemRate;
        r.AllowOverride = allowOverride; r.MinRate = allowOverride ? minRate : systemRate; r.MaxRate = allowOverride ? maxRate : systemRate; r.IsActive = isActive;
        r.TaxRateId = taxRateId is > 0 ? taxRateId : null;
        await db.SaveChangesAsync();
        Flash(T["Rate saved."]);
        return Redirect("/rates");
    }

    // ---------------------------------------------------------------- Settings
    [HttpGet("/settings")]
    public async Task<IActionResult> Settings()
    {
        ViewBag.Gateways = await db.PaymentGateways.OrderByDescending(g => g.IsDefault).ToListAsync();
        ViewBag.Taxes = await db.TaxRates.OrderByDescending(t => t.IsActive).ThenBy(t => t.Name).ToListAsync();
        return View(await db.Organizations.SingleAsync(o => o.Id == OrgId));
    }

    [Authorize(Policy = Roles.Admin), HttpPost("/settings"), ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveSettings(string name, string? address, string? phone, string? gstin, string defaultLanguage)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new BusinessRuleException("Name is required.");
        var org = await db.Organizations.SingleAsync(o => o.Id == OrgId);
        org.Name = name.Trim(); org.Address = address?.Trim(); org.Phone = phone?.Trim(); org.Gstin = gstin?.Trim().ToUpperInvariant();
        org.DefaultLanguage = defaultLanguage == "en" ? "en" : "hi";
        await db.SaveChangesAsync();
        Flash(T["Settings saved. Sign out and in again to see the new name everywhere."]);
        return Redirect("/settings");
    }

    // ---------------------------------------------------------------- Taxes (GST / VAT / any other): data, not code
    [Authorize(Policy = Roles.Admin), HttpGet("/settings/taxes")]
    public async Task<IActionResult> Taxes(int? id)
    {
        ViewBag.Edit = id is > 0 ? await db.TaxRates.FindAsync(id) : null;
        ViewBag.Usage = (await db.RateCards.Where(r => r.TaxRateId != null).Select(r => r.TaxRateId!.Value).ToListAsync())
            .Concat(await db.InventoryItems.Where(i => i.TaxRateId != null).Select(i => i.TaxRateId!.Value).ToListAsync())
            .GroupBy(x => x).ToDictionary(g => g.Key, g => g.Count());
        return View(await db.TaxRates.OrderByDescending(t => t.IsActive).ThenBy(t => t.Name).ToListAsync());
    }

    /// <summary>Old bills keep the tax they were made with; a change here applies to new bills only.</summary>
    [Authorize(Policy = Roles.Admin), HttpPost("/settings/taxes"), ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveTax(int id, string name, decimal percent, bool inclusive, bool splitInHalves, string? halfLabelA, string? halfLabelB, bool isActive)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new BusinessRuleException("Name is required.");
        if (percent < 0 || percent > 100) throw new BusinessRuleException("Percent must be between 0 and 100.");
        TaxRate t;
        if (id == 0) { t = new TaxRate(); db.TaxRates.Add(t); }
        else t = await db.TaxRates.FindAsync(id) ?? throw new KeyNotFoundException();
        t.Name = name.Trim(); t.Percent = Math.Round(percent, 3); t.Inclusive = inclusive; t.IsActive = isActive;
        t.SplitInHalves = splitInHalves;
        t.HalfLabelA = splitInHalves ? (string.IsNullOrWhiteSpace(halfLabelA) ? "CGST" : halfLabelA.Trim()) : null;
        t.HalfLabelB = splitInHalves ? (string.IsNullOrWhiteSpace(halfLabelB) ? "SGST" : halfLabelB.Trim()) : null;
        await db.SaveChangesAsync();
        Flash(T["Tax saved. Attach it to rates in the Rate card and to items in Inventory."]);
        return Redirect("/settings/taxes");
    }

    [Authorize(Policy = Roles.Admin), HttpGet("/settings/payments")]
    public async Task<IActionResult> Payments(int? id)
    {
        ViewBag.Edit = id is > 0 ? await db.PaymentGateways.FindAsync(id) : null;
        return View(await db.PaymentGateways.OrderByDescending(g => g.IsDefault).ThenBy(g => g.Name).ToListAsync());
    }

    /// <summary>Secrets are write-only: stored encrypted, never shown again. Leave a secret blank to keep the stored one.</summary>
    [Authorize(Policy = Roles.Admin), HttpPost("/settings/payments"), ValidateAntiForgeryToken]
    public async Task<IActionResult> SavePayment(int id, string name, GatewayProvider provider, string? upiVpa, string? merchantName, string? keyId,
        string? apiSecret, string? webhookSecret, string? signatureHeader, bool isActive, bool isDefault)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new BusinessRuleException("Name is required.");
        if (!string.IsNullOrWhiteSpace(upiVpa) && !System.Text.RegularExpressions.Regex.IsMatch(upiVpa.Trim(), @"^[\w.\-]{2,256}@[a-zA-Z][a-zA-Z0-9.\-]{1,64}$"))
            throw new BusinessRuleException("UPI ID looks wrong. It should look like name@bank.");
        if (provider == GatewayProvider.UpiQr && string.IsNullOrWhiteSpace(upiVpa)) throw new BusinessRuleException("Enter the UPI ID that should receive the money.");
        if (!string.IsNullOrWhiteSpace(webhookSecret) && webhookSecret.Length < 24) throw new BusinessRuleException("Use a webhook secret of at least 24 characters.");

        PaymentGateway g;
        if (id == 0) { g = new PaymentGateway(); db.PaymentGateways.Add(g); }
        else g = await db.PaymentGateways.FindAsync(id) ?? throw new KeyNotFoundException();
        g.Name = name.Trim(); g.Provider = provider; g.UpiVpa = upiVpa?.Trim(); g.MerchantName = merchantName?.Trim(); g.KeyId = keyId?.Trim();
        g.SignatureHeader = string.IsNullOrWhiteSpace(signatureHeader) ? (provider == GatewayProvider.Razorpay ? "X-Razorpay-Signature" : "X-Chakki-Signature") : signatureHeader.Trim();
        if (!string.IsNullOrWhiteSpace(apiSecret)) g.ApiSecretProtected = secrets.Protect(apiSecret.Trim());
        if (!string.IsNullOrWhiteSpace(webhookSecret)) g.WebhookSecretProtected = secrets.Protect(webhookSecret.Trim());
        if (provider == GatewayProvider.Razorpay && isActive && (string.IsNullOrWhiteSpace(g.KeyId) || g.ApiSecretProtected == null))
            throw new BusinessRuleException("Razorpay needs the Key ID and Key Secret before it can be active.");
        g.IsActive = isActive;
        g.IsDefault = isDefault && isActive;
        if (g.IsDefault)
            foreach (var other in await db.PaymentGateways.Where(x => x.Id != g.Id && x.IsDefault).ToListAsync()) other.IsDefault = false;
        await db.SaveChangesAsync();
        Flash(T["Payment gateway saved."]);
        return Redirect("/settings/payments");
    }

    // ---------------------------------------------------------------- Users (owner)
    [Authorize(Policy = Roles.Admin), HttpGet("/users")]
    public async Task<IActionResult> Users()
    {
        var list = await users.Users.Where(u => u.OrganizationId == OrgId).OrderBy(u => u.FullName).ToListAsync();
        var rows = new List<UserRow>();
        foreach (var u in list) rows.Add(new UserRow(u, (await users.GetRolesAsync(u)).FirstOrDefault() ?? "-"));
        return View(rows);
    }

    [Authorize(Policy = Roles.Admin), HttpPost("/users"), ValidateAntiForgeryToken]
    public async Task<IActionResult> AddUser(string fullName, string email, string password, string role)
    {
        if (!Roles.TenantRoles.Contains(role)) throw new BusinessRuleException("Choose a role.");
        if (await users.FindByEmailAsync(email.Trim()) != null) throw new BusinessRuleException("This email is already registered.");
        var u = new AppUser { UserName = email.Trim(), Email = email.Trim(), FullName = fullName.Trim(), OrganizationId = OrgId, EmailConfirmed = true };
        var r = await users.CreateAsync(u, password ?? "");
        if (!r.Succeeded) throw new BusinessRuleException(string.Join(" ", r.Errors.Select(e => e.Description)));
        await users.AddToRoleAsync(u, role);
        Flash(T["User added. Share the email and password with them."]);
        return Redirect("/users");
    }

    [Authorize(Policy = Roles.Admin), HttpPost("/users/{id}"), ValidateAntiForgeryToken]
    public async Task<IActionResult> UpdateUser(string id, string role, bool isActive, string? newPassword)
    {
        // Lookup is always scoped to this chakki: an owner can never touch another chakki's users.
        var u = await users.Users.FirstOrDefaultAsync(x => x.Id == id && x.OrganizationId == OrgId) ?? throw new KeyNotFoundException();
        if (u.Id == UserId && (!isActive || role != Roles.Owner)) throw new BusinessRuleException("You cannot remove your own owner access.");
        if (!Roles.TenantRoles.Contains(role)) throw new BusinessRuleException("Choose a role.");
        var current = await users.GetRolesAsync(u);
        await users.RemoveFromRolesAsync(u, current);
        await users.AddToRoleAsync(u, role);
        u.IsActive = isActive;
        await users.UpdateAsync(u);
        if (!string.IsNullOrWhiteSpace(newPassword))
        {
            var token = await users.GeneratePasswordResetTokenAsync(u);
            var r = await users.ResetPasswordAsync(u, token, newPassword);
            if (!r.Succeeded) throw new BusinessRuleException(string.Join(" ", r.Errors.Select(e => e.Description)));
        }
        await users.UpdateSecurityStampAsync(u); // signs them out everywhere within a minute
        Flash(T["User updated."]);
        return Redirect("/users");
    }

    // ---------------------------------------------------------------- Platform admin: organisations only, never their records
    [Authorize(Policy = Roles.PlatformAdmin), HttpGet("/platform")]
    public async Task<IActionResult> Platform()
    {
        var orgs = await db.Organizations.OrderBy(o => o.Name).ToListAsync();
        var userCounts = await users.Users.Where(u => u.OrganizationId != null).GroupBy(u => u.OrganizationId).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key!.Value, x => x.Count);
        var orderCounts = await db.GrindingOrders.IgnoreQueryFilters().GroupBy(o => o.OrganizationId).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count);
        return View(orgs.Select(o => new PlatformRow(o, userCounts.GetValueOrDefault(o.Id), orderCounts.GetValueOrDefault(o.Id))).ToList());
    }

    [Authorize(Policy = Roles.PlatformAdmin), HttpPost("/platform/{id:guid}"), ValidateAntiForgeryToken]
    public async Task<IActionResult> SetActive(Guid id, bool active)
    {
        var org = await db.Organizations.FindAsync(id) ?? throw new KeyNotFoundException();
        org.IsActive = active;
        await db.SaveChangesAsync();
        foreach (var u in await users.Users.Where(u => u.OrganizationId == id).ToListAsync()) await users.UpdateSecurityStampAsync(u);
        Flash(active ? $"{org.Name} activated." : $"{org.Name} suspended.");
        return Redirect("/platform");
    }
}
