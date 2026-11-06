using System.Text.RegularExpressions;
using Chakki.Web.Data;
using Chakki.Web.Domain;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Chakki.Web.Services;

public class OnboardingService(AppDbContext db, UserManager<AppUser> users)
{
    /// <summary>Creates a new chakki with its owner, chart of accounts, a starter rate card and stock items.</summary>
    public async Task<(Organization Org, AppUser Owner)> RegisterAsync(string orgName, string ownerName, string email, string password, string language, string? phone = null)
    {
        var org = new Organization { Name = orgName.Trim(), Slug = await UniqueSlugAsync(orgName), DefaultLanguage = language is "en" ? "en" : "hi", Phone = phone };
        db.Organizations.Add(org);
        await db.SaveChangesAsync();

        var owner = new AppUser { UserName = email.Trim(), Email = email.Trim(), FullName = ownerName.Trim(), OrganizationId = org.Id, PreferredLanguage = org.DefaultLanguage, EmailConfirmed = true };
        var result = await users.CreateAsync(owner, password);
        if (!result.Succeeded)
        {
            db.Organizations.Remove(org);
            await db.SaveChangesAsync();
            throw new BusinessRuleException(string.Join(" ", result.Errors.Select(e => e.Description)));
        }
        await users.AddToRoleAsync(owner, Roles.Owner);

        using (db.Tenant.Use(org.Id))
        {
            await LedgerService.SeedAccountsAsync(db, org.Id);
            SeedRates(org.Id);
            SeedItems(org.Id);
            await db.SaveChangesAsync();
        }
        return (org, owner);
    }

    private async Task<string> UniqueSlugAsync(string name)
    {
        var baseSlug = Regex.Replace(name.ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');
        if (baseSlug.Length < 3) baseSlug = "chakki";
        if (baseSlug.Length > 40) baseSlug = baseSlug[..40];
        var slug = baseSlug;
        for (var i = 2; await db.Organizations.AnyAsync(o => o.Slug == slug); i++) slug = $"{baseSlug}-{i}";
        return slug;
    }

    private void SeedRates(Guid org)
    {
        var today = DateOnly.FromDateTime(DateTime.Today);
        (string grain, string service, string hi, bool jalan, decimal rate, decimal min, decimal max)[] rates =
        [
            ("Wheat", "Atta", "गेहूं → आटा", false, 2.40m, 2.00m, 3.00m),
            ("Wheat", "Atta", "गेहूं → आटा (जलन)", true, 3.00m, 2.50m, 3.50m),
            ("Chana", "Besan", "चना → बेसन", false, 3.00m, 2.50m, 3.50m),
            ("Multigrain", "Atta", "मल्टीग्रेन आटा", false, 3.50m, 3.00m, 4.00m),
            ("Mustard", "Oil extraction", "सरसों → तेल", false, 5.00m, 4.00m, 6.00m),
            ("Bajra", "Atta", "बाजरा → आटा", false, 2.50m, 2.00m, 3.00m),
            ("Makka", "Atta", "मक्का → आटा", false, 2.50m, 2.00m, 3.00m),
        ];
        foreach (var r in rates)
            db.RateCards.Add(new RateCard { OrganizationId = org, Grain = r.grain, Service = r.service, ServiceHi = r.hi, Jalan = r.jalan, SystemRate = r.rate, MinRate = r.min, MaxRate = r.max, EffectiveFrom = today });
    }

    private void SeedItems(Guid org)
    {
        (string name, string hi, ItemCategory cat, string unit, decimal sale, decimal buyback, decimal reorder)[] items =
        [
            ("Wheat (raw)", "गेहूं", ItemCategory.RawGrain, "kg", 0, 0, 200),
            ("Mustard seed", "सरसों", ItemCategory.RawGrain, "kg", 0, 0, 100),
            ("Packed Atta 10kg", "पैक आटा 10 किलो", ItemCategory.PackedAtta, "bag", 380, 0, 10),
            ("Mustard Oil", "सरसों तेल", ItemCategory.Oil, "litre", 180, 0, 20),
            ("Khal (oil cake)", "खल", ItemCategory.ByProduct, "kg", 30, 20, 50),
            ("Chokar (bran)", "चोकर", ItemCategory.ByProduct, "kg", 18, 12, 50),
            ("Gunny bag", "बोरी", ItemCategory.Packaging, "pcs", 15, 0, 50),
        ];
        foreach (var i in items)
            db.InventoryItems.Add(new InventoryItem { OrganizationId = org, Name = i.name, NameHi = i.hi, Category = i.cat, Unit = i.unit, SalePrice = i.sale, BuybackRate = i.buyback, ReorderLevel = i.reorder });
    }
}
