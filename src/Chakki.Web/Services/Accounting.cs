using Chakki.Web.Data;
using Chakki.Web.Domain;
using Microsoft.EntityFrameworkCore;

namespace Chakki.Web.Services;

public record PostLine(string AccountCode, decimal Debit, decimal Credit, int? CustomerId = null, int? SupplierId = null);

public class UnbalancedJournalException(string message) : InvalidOperationException(message);

/// <summary>Double-entry posting. Every money movement in the app goes through here.</summary>
public class LedgerService(AppDbContext db)
{
    public async Task<JournalEntry> PostAsync(DateOnly date, string memo, string source, int? sourceId, IEnumerable<PostLine> lines, string? userId = null)
    {
        var list = lines.Where(l => l.Debit != 0 || l.Credit != 0).ToList();
        if (list.Count < 2) throw new UnbalancedJournalException("A journal needs at least two lines.");
        if (list.Any(l => l.Debit < 0 || l.Credit < 0 || (l.Debit > 0 && l.Credit > 0)))
            throw new UnbalancedJournalException("Each line is either a debit or a credit, never negative.");
        var dr = list.Sum(l => l.Debit);
        var cr = list.Sum(l => l.Credit);
        if (dr != cr) throw new UnbalancedJournalException($"Debits ({dr}) must equal credits ({cr}).");

        var codes = list.Select(l => l.AccountCode).Distinct().ToList();
        var accounts = await db.Accounts.Where(a => codes.Contains(a.Code)).ToDictionaryAsync(a => a.Code);
        var org = await db.Organizations.SingleAsync(o => o.Id == db.CurrentOrganizationId);

        var entry = new JournalEntry
        {
            Number = org.NextJournalNo++,
            Date = date,
            Memo = memo,
            Source = source,
            SourceId = sourceId,
            PostedById = userId,
        };
        foreach (var l in list)
        {
            if (!accounts.TryGetValue(l.AccountCode, out var account))
                throw new UnbalancedJournalException($"Account {l.AccountCode} does not exist.");
            entry.Lines.Add(new LedgerLine { Account = account, AccountId = account.Id, Debit = l.Debit, Credit = l.Credit, CustomerId = l.CustomerId, SupplierId = l.SupplierId });
        }
        db.JournalEntries.Add(entry);
        return entry;
    }

    /// <summary>Corrections never edit posted rows: they post the mirror image and mark the original reversed.</summary>
    public async Task<JournalEntry> ReverseAsync(int journalId, string reason, string? userId)
    {
        var original = await db.JournalEntries.Include(j => j.Lines).ThenInclude(l => l.Account).SingleAsync(j => j.Id == journalId);
        if (original.IsReversed || original.ReversesId != null) throw new InvalidOperationException("This entry is already reversed or is itself a reversal.");
        var rev = await PostAsync(DateOnly.FromDateTime(DateTime.Today), $"Reversal of #{original.Number}: {reason}", "reversal", original.Id,
            original.Lines.Select(l => new PostLine(l.Account!.Code, l.Credit, l.Debit, l.CustomerId, l.SupplierId)), userId);
        rev.ReversesId = original.Id;
        original.IsReversed = true;
        return rev;
    }

    public static async Task SeedAccountsAsync(AppDbContext db, Guid orgId)
    {
        var existing = await db.Accounts.IgnoreQueryFilters().Where(a => a.OrganizationId == orgId).Select(a => a.Code).ToListAsync();
        foreach (var (code, name, hi, type) in SystemAccounts.Seed.Where(s => !existing.Contains(s.Code)))
            db.Accounts.Add(new Account { OrganizationId = orgId, Code = code, Name = name, NameHi = hi, Type = type, IsSystem = true });
    }

    /// <summary>Balance per account (debit-positive for assets/expenses, credit-positive otherwise).</summary>
    public async Task<List<AccountBalance>> TrialBalanceAsync(DateOnly? from = null, DateOnly? to = null)
    {
        var q = db.LedgerLines.Include(l => l.JournalEntry).AsQueryable();
        if (from != null) q = q.Where(l => l.JournalEntry!.Date >= from);
        if (to != null) q = q.Where(l => l.JournalEntry!.Date <= to);
        var lines = await q.Select(l => new { l.AccountId, l.Debit, l.Credit }).ToListAsync();
        var accounts = await db.Accounts.OrderBy(a => a.Code).ToListAsync();
        return accounts.Select(a =>
        {
            var mine = lines.Where(l => l.AccountId == a.Id).ToList();
            return new AccountBalance(a, mine.Sum(l => l.Debit), mine.Sum(l => l.Credit));
        }).ToList();
    }

    public async Task<decimal> CustomerUdhaarAsync(int customerId)
    {
        var lines = await db.LedgerLines.Where(l => l.CustomerId == customerId && l.Account!.Code == SystemAccounts.Receivable)
            .Select(l => new { l.Debit, l.Credit }).ToListAsync();
        return lines.Sum(l => l.Debit - l.Credit);
    }

    public async Task<Dictionary<int, decimal>> AllUdhaarAsync()
    {
        var lines = await db.LedgerLines.Where(l => l.CustomerId != null && l.Account!.Code == SystemAccounts.Receivable)
            .Select(l => new { l.CustomerId, l.Debit, l.Credit }).ToListAsync();
        return lines.GroupBy(l => l.CustomerId!.Value).ToDictionary(g => g.Key, g => g.Sum(l => l.Debit - l.Credit));
    }

    public async Task<decimal> SupplierPayableAsync(int supplierId)
    {
        var lines = await db.LedgerLines.Where(l => l.SupplierId == supplierId && l.Account!.Code == SystemAccounts.Payable)
            .Select(l => new { l.Debit, l.Credit }).ToListAsync();
        return lines.Sum(l => l.Credit - l.Debit);
    }

    public async Task<decimal> AccountBalanceAsync(string code)
    {
        var lines = await db.LedgerLines.Where(l => l.Account!.Code == code).Select(l => new { l.Debit, l.Credit }).ToListAsync();
        return lines.Sum(l => l.Debit - l.Credit);
    }
}

public record AccountBalance(Account Account, decimal Debit, decimal Credit)
{
    public bool DebitNature => Account.Type is AccountType.Asset or AccountType.Expense;
    public decimal Balance => DebitNature ? Debit - Credit : Credit - Debit;
}
