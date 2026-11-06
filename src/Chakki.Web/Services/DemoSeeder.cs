using Chakki.Web.Data;
using Chakki.Web.Domain;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Chakki.Web.Services;

/// <summary>
/// Startup data: roles, an optional platform admin (from configuration / environment, never from source), and with
/// <c>Seed:Demo=true</c> two demo chakkis so the isolation between organisations can be seen side by side.
/// </summary>
public class DemoSeeder(AppDbContext db, RoleManager<IdentityRole> roles, UserManager<AppUser> users, IServiceProvider sp, IConfiguration config, ILogger<DemoSeeder> log)
{
    public async Task RunAsync()
    {
        foreach (var role in Roles.TenantRoles.Append(Roles.PlatformAdmin))
            if (!await roles.RoleExistsAsync(role)) await roles.CreateAsync(new IdentityRole(role));

        var adminEmail = config["Seed:PlatformAdminEmail"];
        var adminPassword = config["Seed:PlatformAdminPassword"];
        if (!string.IsNullOrWhiteSpace(adminEmail) && !string.IsNullOrWhiteSpace(adminPassword) && await users.FindByEmailAsync(adminEmail) == null)
        {
            var admin = new AppUser { UserName = adminEmail, Email = adminEmail, FullName = "Platform admin", EmailConfirmed = true };
            var r = await users.CreateAsync(admin, adminPassword);
            if (r.Succeeded) await users.AddToRoleAsync(admin, Roles.PlatformAdmin);
            else log.LogWarning("Platform admin not created: {Errors}", string.Join("; ", r.Errors.Select(e => e.Description)));
        }

        if (!config.GetValue<bool>("Seed:Demo")) return;
        var password = config["Seed:DemoPassword"];
        if (string.IsNullOrWhiteSpace(password))
        {
            log.LogWarning("Seed:Demo is on but Seed:DemoPassword is empty; demo data skipped.");
            return;
        }
        if (await db.Organizations.AnyAsync(o => o.Slug == "shri-ram-atta-chakki")) return;

        await SeedChakkiAsync("Shri Ram Atta Chakki", "Ramesh Yadav", "owner@shriram.demo", password, "hi",
            [("Suresh Kumar", "Rampur"), ("Geeta Devi", "Rampur"), ("Mohan Lal", "Sitapur"), ("Rafiq Khan", "Sitapur"), ("Kamla Bai", "Bhojpur")]);
        await SeedChakkiAsync("Ganga Flour Mill", "Anita Sharma", "owner@ganga.demo", password, "en",
            [("Vijay Singh", "Haripur"), ("Sunita Verma", "Haripur"), ("Arjun Patel", "Madhopur")]);
        log.LogInformation("Demo chakkis created: owner@shriram.demo and owner@ganga.demo");
    }

    private async Task SeedChakkiAsync(string name, string owner, string email, string password, string lang, (string Name, string Village)[] customers)
    {
        var onboarding = sp.GetRequiredService<OnboardingService>();
        var (org, user) = await onboarding.RegisterAsync(name, owner, email, password, lang, "+91 90000 00000");
        using var scope = db.Tenant.Use(org.Id);

        var op = new AppUser { UserName = email.Replace("owner@", "operator@"), Email = email.Replace("owner@", "operator@"), FullName = "Counter Operator", OrganizationId = org.Id, EmailConfirmed = true };
        if ((await users.CreateAsync(op, password)).Succeeded) await users.AddToRoleAsync(op, Roles.Operator);

        var orders = sp.GetRequiredService<OrderService>();
        var ops = sp.GetRequiredService<OperationsService>();
        var cs = customers.Select((c, i) => new Customer { Name = c.Name, Village = c.Village, Phone = $"98{i}0012{i}45" }).ToList();
        db.Customers.AddRange(cs);
        var supplier = new Supplier { Name = name.StartsWith("Shri") ? "Kisan Traders" : "Agro Supplies", Phone = "9876500000" };
        db.Suppliers.Add(supplier);
        db.Employees.AddRange(
            new Employee { Name = "Raju", Role = "Operator", WageType = WageType.Daily, WageRate = 400, JoinedOn = DateOnly.FromDateTime(DateTime.Today.AddMonths(-8)) },
            new Employee { Name = "Shyam", Role = "Helper", WageType = WageType.Monthly, WageRate = 9000, JoinedOn = DateOnly.FromDateTime(DateTime.Today.AddMonths(-3)) });
        db.MaintenanceTasks.AddRange(
            new MaintenanceTask { Title = "Stone dressing (Takai)", Kind = "Takai", DueOn = DateOnly.FromDateTime(DateTime.Today.AddDays(3)), RepeatDays = 30 },
            new MaintenanceTask { Title = "Belt tension check", Kind = "Belt", DueOn = DateOnly.FromDateTime(DateTime.Today.AddDays(10)), RepeatDays = 15 });
        // A demo UPI QR gateway (a placeholder VPA). The webhook secret comes only from configuration.
        var webhookSecret = config["Seed:DemoWebhookSecret"];
        db.PaymentGateways.Add(new PaymentGateway
        {
            Name = "UPI QR (demo)", Provider = GatewayProvider.UpiQr, UpiVpa = $"{org.Slug.Replace("-", "")[..Math.Min(12, org.Slug.Replace("-", "").Length)]}@okdemo",
            MerchantName = name, SignatureHeader = "X-Chakki-Signature", IsActive = true, IsDefault = true,
            WebhookSecretProtected = string.IsNullOrWhiteSpace(webhookSecret) ? null : sp.GetRequiredService<SecretProtector>().Protect(webhookSecret),
        });
        await db.SaveChangesAsync();

        await sp.GetRequiredService<LedgerService>().PostAsync(DateOnly.FromDateTime(DateTime.Today.AddDays(-30)), "Opening cash", "opening", null,
            [new(SystemAccounts.Cash, 15000, 0), new(SystemAccounts.OpeningEquity, 0, 15000)], user.Id);
        await db.SaveChangesAsync();
        var rates = await db.RateCards.ToListAsync();
        var wheat = rates.First(r => r.Grain == "Wheat" && !r.Jalan);
        var mustard = rates.First(r => r.Grain == "Mustard");
        var items = await db.InventoryItems.ToListAsync();
        await ops.PurchaseAsync(supplier.Id, items.First(i => i.Name.StartsWith("Wheat")).Id, 500, 24, false, null, user.Id);
        await ops.PurchaseAsync(supplier.Id, items.First(i => i.Name.StartsWith("Packed")).Id, 20, 300, true, null, user.Id);

        var rnd = new Random(org.Name.Length);
        foreach (var c in cs)
        {
            var o = await orders.CreateAsync(c.Id, [new(wheat.Id, rnd.Next(10, 60), null, null)], null, user.Id);
            await orders.AdvanceAsync(o, OrderStatus.Grinding);
            await orders.AdvanceAsync(o, OrderStatus.Ready);
            await orders.BillAsync(o, rnd.Next(0, 3), user.Id);
            if (rnd.Next(0, 2) == 0) await orders.CollectCashAsync(o, Math.Floor(o.Outstanding / 2), user.Id);
        }
        var oil = await orders.CreateAsync(cs[0].Id, [new(mustard.Id, 15, null, null)], "Mustard for oil", user.Id);
        await orders.AdvanceAsync(oil, OrderStatus.Grinding);
        await orders.CreateAsync(cs[^1].Id, [new(wheat.Id, 25, 2.20m, "Regular customer")], null, user.Id);
        await ops.AddOpeningBalanceAsync(cs[1].Id, 350, DateOnly.FromDateTime(DateTime.Today.AddMonths(-2)), "Carried from old register", user.Id);
        await ops.UtilityAsync("Electricity", 1850, 240, "Monthly bill", null, user.Id);
    }
}
