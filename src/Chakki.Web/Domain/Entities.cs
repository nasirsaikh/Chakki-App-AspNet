using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Identity;

namespace Chakki.Web.Domain;

/// <summary>Every row that belongs to one chakki (tenant) implements this. The DbContext filters and stamps it.</summary>
public interface ITenantOwned
{
    Guid OrganizationId { get; set; }
}

public abstract class TenantEntity : ITenantOwned
{
    public int Id { get; set; }
    public Guid OrganizationId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

// ---------------------------------------------------------------- Organisation & users

public class Organization
{
    public Guid Id { get; set; } = Guid.NewGuid();
    [MaxLength(120)] public string Name { get; set; } = "";
    [MaxLength(60)] public string Slug { get; set; } = "";
    [MaxLength(200)] public string? Address { get; set; }
    [MaxLength(30)] public string? Phone { get; set; }
    [MaxLength(20)] public string? Gstin { get; set; }
    [MaxLength(5)] public string DefaultLanguage { get; set; } = "hi";
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    /// <summary>Per-organisation running numbers (orders, payments, journals).</summary>
    public int NextOrderNo { get; set; } = 1;
    public int NextJournalNo { get; set; } = 1;
}

public class AppUser : IdentityUser
{
    public Guid? OrganizationId { get; set; }
    [MaxLength(120)] public string FullName { get; set; } = "";
    [MaxLength(5)] public string? PreferredLanguage { get; set; }
    public bool IsActive { get; set; } = true;
}

public static class Roles
{
    public const string Owner = "Owner";
    public const string Manager = "Manager";
    public const string Operator = "Operator";
    public const string Accountant = "Accountant";
    public const string PlatformAdmin = "PlatformAdmin";
    public static readonly string[] TenantRoles = [Owner, Manager, Operator, Accountant];

    // Policy groups used by [Authorize(Policy = ...)]
    public const string Floor = "Floor";          // Owner, Manager, Operator
    public const string Money = "Money";          // Owner, Manager, Accountant
    public const string Books = "Books";          // Owner, Accountant
    public const string Admin = "Admin";          // Owner only
    public const string People = "People";        // Owner, Manager
}

// ---------------------------------------------------------------- Customers

public enum CustomerKind { Permanent, Temporary }

public class Customer : TenantEntity
{
    [Required, MaxLength(120)] public string Name { get; set; } = "";
    [MaxLength(20)] public string? Phone { get; set; }
    [MaxLength(80)] public string? Village { get; set; }
    [MaxLength(200)] public string? Address { get; set; }
    public CustomerKind Kind { get; set; } = CustomerKind.Permanent;
    [MaxLength(300)] public string? Notes { get; set; }
    public bool IsActive { get; set; } = true;
}

/// <summary>Old Udhaar carried in at go-live. Settled first (FIFO) by receipts.</summary>
public class OpeningBalance : TenantEntity
{
    public int CustomerId { get; set; }
    public Customer? Customer { get; set; }
    public decimal Amount { get; set; }
    public decimal Settled { get; set; }
    public DateOnly AsOf { get; set; }
    [MaxLength(200)] public string? Note { get; set; }
    public int? JournalEntryId { get; set; }
    public decimal Remaining => Amount - Settled;
}

// ---------------------------------------------------------------- Rate card & grinding

public class RateCard : TenantEntity
{
    [Required, MaxLength(60)] public string Grain { get; set; } = "";
    [Required, MaxLength(60)] public string Service { get; set; } = "";
    [MaxLength(60)] public string? ServiceHi { get; set; }
    public bool Jalan { get; set; }
    public decimal SystemRate { get; set; }
    public bool AllowOverride { get; set; } = true;
    public decimal MinRate { get; set; }
    public decimal MaxRate { get; set; }
    public DateOnly EffectiveFrom { get; set; }
    public bool IsActive { get; set; } = true;
    public string Label => $"{Grain} → {Service}{(Jalan ? " (Jalan)" : "")}";
}

public enum OrderStatus { Intake, Grinding, Ready, Delivered, Cancelled }
public enum PaymentState { Unbilled, Unpaid, Partial, Paid }

public class GrindingOrder : TenantEntity
{
    public int Number { get; set; }
    public int CustomerId { get; set; }
    public Customer? Customer { get; set; }
    public OrderStatus Status { get; set; } = OrderStatus.Intake;
    public PaymentState PaymentState { get; set; } = PaymentState.Unbilled;
    public decimal Total { get; set; }
    public decimal Forgiven { get; set; }
    public decimal Paid { get; set; }
    public DateTime? BilledAt { get; set; }
    public DateTime? DeliveredAt { get; set; }
    [MaxLength(300)] public string? Notes { get; set; }
    public string CreatedById { get; set; } = "";
    public List<GrindingOrderLine> Lines { get; set; } = [];
    public List<Payment> Payments { get; set; } = [];
    /// <summary>Amount the customer still owes on this order (0 until billed).</summary>
    public decimal Outstanding => BilledAt == null ? 0 : Total - Forgiven - Paid;
    public string Ref => $"G-{Number:D5}";
}

public class GrindingOrderLine : TenantEntity
{
    public int GrindingOrderId { get; set; }
    public int RateCardId { get; set; }
    public RateCard? RateCard { get; set; }
    public decimal WeightKg { get; set; }
    public decimal SystemRate { get; set; }
    public decimal AppliedRate { get; set; }
    [MaxLength(200)] public string? OverrideReason { get; set; }
    public decimal Amount { get; set; }
}

// ---------------------------------------------------------------- Payments

public enum PaymentMethod { Cash, UpiQr, Card, Buyback }
public enum SettlementStatus { Pending, Settled, Failed, Expired }
public enum GatewayProvider { UpiQr, Razorpay, CustomHmac }

public class PaymentGateway : TenantEntity
{
    [Required, MaxLength(80)] public string Name { get; set; } = "";
    public GatewayProvider Provider { get; set; }
    [MaxLength(100)] public string? UpiVpa { get; set; }
    [MaxLength(100)] public string? MerchantName { get; set; }
    [MaxLength(100)] public string? KeyId { get; set; }
    /// <summary>Encrypted with ASP.NET Data Protection; never rendered back.</summary>
    public string? ApiSecretProtected { get; set; }
    public string? WebhookSecretProtected { get; set; }
    [MaxLength(60)] public string SignatureHeader { get; set; } = "X-Chakki-Signature";
    public bool IsActive { get; set; }
    public bool IsDefault { get; set; }
}

public class Payment : TenantEntity
{
    public int GrindingOrderId { get; set; }
    public GrindingOrder? GrindingOrder { get; set; }
    public int CustomerId { get; set; }
    public PaymentMethod Method { get; set; }
    public decimal Amount { get; set; }
    public SettlementStatus Status { get; set; } = SettlementStatus.Pending;
    /// <summary>Globally unique, unguessable reference used by QR, webhooks and the public pay link.</summary>
    [MaxLength(40)] public string Reference { get; set; } = "";
    public int? GatewayId { get; set; }
    public PaymentGateway? Gateway { get; set; }
    [MaxLength(60)] public string? ProviderSnapshot { get; set; }
    [MaxLength(100)] public string? ProviderOrderId { get; set; }
    [MaxLength(100)] public string? ProviderPaymentId { get; set; }
    public DateTime? SettledAt { get; set; }
    public int? JournalEntryId { get; set; }
    public JournalEntry? JournalEntry { get; set; }
    public string? RecordedById { get; set; }
}

// ---------------------------------------------------------------- Accounting

public enum AccountType { Asset, Liability, Equity, Income, Expense }

public static class SystemAccounts
{
    public const string Cash = "1000";
    public const string Bank = "1010";
    public const string Receivable = "1100";
    public const string Inventory = "1200";
    public const string Payable = "2000";
    public const string BuybackPayable = "2100";
    public const string WagesPayable = "2200";
    public const string OpeningEquity = "3000";
    public const string GrindingIncome = "4000";
    public const string SalesIncome = "4100";
    public const string Goodwill = "5000";
    public const string Cogs = "5100";
    public const string Utility = "5200";
    public const string Wages = "5300";
    public const string Maintenance = "5400";
    public const string General = "5500";
    public const string Wastage = "5600";

    public static readonly (string Code, string Name, string NameHi, AccountType Type)[] Seed =
    [
        (Cash, "Cash Book", "नकद बही", AccountType.Asset),
        (Bank, "Bank / UPI", "बैंक / यूपीआई", AccountType.Asset),
        (Receivable, "Accounts Receivable (Udhaar)", "उधार (प्राप्य)", AccountType.Asset),
        (Inventory, "Inventory Asset", "स्टॉक", AccountType.Asset),
        (Payable, "Accounts Payable (Suppliers)", "सप्लायर देय", AccountType.Liability),
        (BuybackPayable, "Customer Buyback Payable", "बायबैक देय", AccountType.Liability),
        (WagesPayable, "Wages Payable", "मजदूरी देय", AccountType.Liability),
        (OpeningEquity, "Opening Balance Equity", "प्रारंभिक पूंजी", AccountType.Equity),
        (GrindingIncome, "Grinding Income", "पिसाई आय", AccountType.Income),
        (SalesIncome, "Retail Sales Income", "बिक्री आय", AccountType.Income),
        (Goodwill, "Expense: Customer Goodwill/Forgiven", "छूट / माफ़ी", AccountType.Expense),
        (Cogs, "Cost of Goods Sold", "बिक्री लागत", AccountType.Expense),
        (Utility, "Utility Expense", "बिजली/ईंधन खर्च", AccountType.Expense),
        (Wages, "Wage and Payroll Expense", "मजदूरी खर्च", AccountType.Expense),
        (Maintenance, "Maintenance Expense", "मरम्मत खर्च", AccountType.Expense),
        (General, "General Operating Expense", "सामान्य खर्च", AccountType.Expense),
        (Wastage, "Wastage / Process Loss", "बर्बादी / हानि", AccountType.Expense),
    ];
}

public class Account : TenantEntity
{
    [Required, MaxLength(10)] public string Code { get; set; } = "";
    [Required, MaxLength(80)] public string Name { get; set; } = "";
    [MaxLength(80)] public string? NameHi { get; set; }
    public AccountType Type { get; set; }
    public bool IsSystem { get; set; }
}

public class JournalEntry : TenantEntity
{
    public int Number { get; set; }
    public DateOnly Date { get; set; }
    [Required, MaxLength(200)] public string Memo { get; set; } = "";
    [MaxLength(40)] public string Source { get; set; } = "manual";
    public int? SourceId { get; set; }
    public int? ReversesId { get; set; }
    public bool IsReversed { get; set; }
    public string? PostedById { get; set; }
    public List<LedgerLine> Lines { get; set; } = [];
}

public class LedgerLine : TenantEntity
{
    public int JournalEntryId { get; set; }
    public JournalEntry? JournalEntry { get; set; }
    public int AccountId { get; set; }
    public Account? Account { get; set; }
    public decimal Debit { get; set; }
    public decimal Credit { get; set; }
    public int? CustomerId { get; set; }
    public int? SupplierId { get; set; }
}

// ---------------------------------------------------------------- Inventory, suppliers, purchase, sale

public enum ItemCategory { RawGrain, PackedAtta, Oil, ByProduct, Packaging, Other }

public class InventoryItem : TenantEntity
{
    [Required, MaxLength(80)] public string Name { get; set; } = "";
    [MaxLength(80)] public string? NameHi { get; set; }
    public ItemCategory Category { get; set; }
    [MaxLength(10)] public string Unit { get; set; } = "kg";
    public decimal Quantity { get; set; }
    public decimal AvgCost { get; set; }
    public decimal SalePrice { get; set; }
    public decimal BuybackRate { get; set; }
    public decimal ReorderLevel { get; set; }
}

public enum MovementKind { Purchase, Sale, Buyback, ProductionIn, ProductionOut, Wastage, Adjustment }

public class StockMovement : TenantEntity
{
    public int ItemId { get; set; }
    public InventoryItem? Item { get; set; }
    public MovementKind Kind { get; set; }
    public decimal Quantity { get; set; } // signed
    public decimal UnitCost { get; set; }
    [MaxLength(200)] public string? Note { get; set; }
    public DateOnly Date { get; set; }
}

public class Supplier : TenantEntity
{
    [Required, MaxLength(120)] public string Name { get; set; } = "";
    [MaxLength(20)] public string? Phone { get; set; }
    [MaxLength(200)] public string? Address { get; set; }
}

public class Purchase : TenantEntity
{
    public int SupplierId { get; set; }
    public Supplier? Supplier { get; set; }
    public int ItemId { get; set; }
    public InventoryItem? Item { get; set; }
    public decimal Quantity { get; set; }
    public decimal Rate { get; set; }
    public decimal Amount { get; set; }
    public bool PaidNow { get; set; }
    public DateOnly Date { get; set; }
}

public class SupplierPayment : TenantEntity
{
    public int SupplierId { get; set; }
    public Supplier? Supplier { get; set; }
    public decimal Amount { get; set; }
    public DateOnly Date { get; set; }
    [MaxLength(200)] public string? Note { get; set; }
}

public class Sale : TenantEntity
{
    public int? CustomerId { get; set; }
    public Customer? Customer { get; set; }
    public int ItemId { get; set; }
    public InventoryItem? Item { get; set; }
    public decimal Quantity { get; set; }
    public decimal Rate { get; set; }
    public decimal Amount { get; set; }
    public decimal CostAmount { get; set; }
    public bool OnUdhaar { get; set; }
    public DateOnly Date { get; set; }
}

public class Buyback : TenantEntity
{
    public int CustomerId { get; set; }
    public Customer? Customer { get; set; }
    public int ItemId { get; set; }
    public InventoryItem? Item { get; set; }
    public int? GrindingOrderId { get; set; }
    public decimal Quantity { get; set; }
    public decimal SystemRate { get; set; }
    public decimal AppliedRate { get; set; }
    [MaxLength(200)] public string? OverrideReason { get; set; }
    public decimal Value { get; set; }
    public decimal FeeAdjusted { get; set; }
    public decimal CashPaid { get; set; }
    public DateOnly Date { get; set; }
}

public class ProductionRun : TenantEntity
{
    public int InputItemId { get; set; }
    public InventoryItem? InputItem { get; set; }
    public decimal InputQty { get; set; }
    public int OutputItemId { get; set; }
    public InventoryItem? OutputItem { get; set; }
    public decimal OutputQty { get; set; }
    public int? ByProductItemId { get; set; }
    public InventoryItem? ByProductItem { get; set; }
    public decimal ByProductQty { get; set; }
    public decimal LossQty { get; set; }
    public decimal YieldPercent { get; set; }
    public DateOnly Date { get; set; }
}

public class WastageLog : TenantEntity
{
    public int? ItemId { get; set; }
    public InventoryItem? Item { get; set; }
    [MaxLength(40)] public string Kind { get; set; } = "Milling loss";
    public decimal Quantity { get; set; }
    public decimal CostAmount { get; set; }
    [MaxLength(200)] public string? Note { get; set; }
    public DateOnly Date { get; set; }
}

public class UtilityExpense : TenantEntity
{
    [MaxLength(40)] public string Kind { get; set; } = "Electricity";
    public decimal Amount { get; set; }
    public decimal? Units { get; set; }
    [MaxLength(200)] public string? Note { get; set; }
    public DateOnly Date { get; set; }
}

// ---------------------------------------------------------------- Workforce

public enum WageType { Monthly, Daily }
public enum AttendanceStatus { Present, HalfDay, Absent, Leave }

public class Employee : TenantEntity
{
    [Required, MaxLength(120)] public string Name { get; set; } = "";
    [MaxLength(20)] public string? Phone { get; set; }
    [MaxLength(60)] public string? Role { get; set; }
    public WageType WageType { get; set; }
    public decimal WageRate { get; set; } // per day or per month
    public DateOnly JoinedOn { get; set; }
    public bool IsActive { get; set; } = true;
}

public class Attendance : TenantEntity
{
    public int EmployeeId { get; set; }
    public Employee? Employee { get; set; }
    public DateOnly Date { get; set; }
    public AttendanceStatus Status { get; set; }
    public TimeOnly? CheckIn { get; set; }
    public TimeOnly? CheckOut { get; set; }
}

public class PayrollRun : TenantEntity
{
    public int EmployeeId { get; set; }
    public Employee? Employee { get; set; }
    public DateOnly PeriodStart { get; set; }
    public DateOnly PeriodEnd { get; set; }
    public decimal DaysWorked { get; set; }
    public decimal Gross { get; set; }
    public int? JournalEntryId { get; set; }
}

public enum MaintenanceStatus { Scheduled, Done }

public class MaintenanceTask : TenantEntity
{
    [Required, MaxLength(100)] public string Title { get; set; } = "";
    [MaxLength(40)] public string Kind { get; set; } = "Takai";
    public DateOnly DueOn { get; set; }
    public int? RepeatDays { get; set; }
    public MaintenanceStatus Status { get; set; }
    public DateOnly? DoneOn { get; set; }
    public decimal Cost { get; set; }
    [MaxLength(200)] public string? Note { get; set; }
}

// ---------------------------------------------------------------- Chat

public class ChatSession : TenantEntity
{
    public string UserId { get; set; } = "";
    [MaxLength(120)] public string Title { get; set; } = "";
    public List<ChatMessage> Messages { get; set; } = [];
}

public class ChatMessage : TenantEntity
{
    public int ChatSessionId { get; set; }
    [MaxLength(10)] public string Role { get; set; } = "user"; // user | assistant
    public string Content { get; set; } = "";
    [MaxLength(5)] public string Language { get; set; } = "en";
    [MaxLength(40)] public string? Provider { get; set; }
    /// <summary>Optional JSON with a small table for the answer.</summary>
    public string? DataJson { get; set; }
}

// ---------------------------------------------------------------- Audit

public class AuditLog : TenantEntity
{
    public string? UserId { get; set; }
    [MaxLength(60)] public string Action { get; set; } = "";
    [MaxLength(300)] public string? Detail { get; set; }
}
