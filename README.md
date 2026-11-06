# Chakki ERP (ASP.NET Core + HTMX)

A mobile-first, bilingual (Hindi / English) **flour mill (chakki) management and accounting app** that many chakkis can share safely.
Each chakki signs up, gets its own private books, and cannot see or touch another chakki's data.

Built with **ASP.NET Core 10 MVC + Razor + HTMX 2**, the **CoreApp-AI theme** (AdminLTE 4 / Bootstrap 5, chat UI), ApexCharts,
EF Core on SQLite, ASP.NET Identity, and an optional **Claude** chat assistant.

## What it does

| Area | Screens |
|---|---|
| Counter | Dashboard (auto-refresh every 30 s, 7-day chart), new grinding order, order flow Intake → Grinding → Ready → Delivered, billing with forgiveness |
| Payments | Cash, **dynamic UPI QR** (any UPI app), **card / UPI via Razorpay Checkout**, shareable pay link for WhatsApp, signed webhooks, manual UTR confirmation |
| Customers | Customers, village filter, live search, history, **Udhaar** (credit) with receipts, **Old Udhaar** (opening balances, settled FIFO) |
| Rates | Grain × service × Jalan rate card with a min / max band; any change needs a reason; every order line keeps a rate snapshot |
| Stock | Inventory, purchases (supplier payables), retail sales with COGS, buyback against fees, production / yield, wastage |
| Books | Double-entry journal, ledger per account, manual entries, reversal (no edits or deletes), reports, P&L, trial balance |
| People | Employees (daily / monthly wages), attendance, payroll posting, maintenance (Takai, belts, bearings) with repeats |
| Setup | Chakki settings, payment gateways, users and roles, platform admin (organisations only) |
| Chat | Ask in Hindi, Hinglish or English: "Geeta ka udhaar kitna hai?", "आज कितनी पिसाई हुई?", "Which customers owe the most?" |

## Quick start

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download).

```bash
cd src/Chakki.Web
# Demo data: two chakkis side by side so you can see that they are isolated.
export Seed__DemoPassword='choose-a-password'
ASPNETCORE_ENVIRONMENT=Development dotnet run
```

Open http://localhost:5050. Demo logins, all with the password you set:

| Email | Chakki | Role |
|---|---|---|
| owner@shriram.demo | Shri Ram Atta Chakki (Hindi) | Owner |
| operator@shriram.demo | Shri Ram Atta Chakki | Operator |
| owner@ganga.demo | Ganga Flour Mill (English) | Owner |

Or click **Register your chakki** on the login page to create a new organisation. The database and keys are created in
`src/Chakki.Web/App_Data/` on first run (migrations are applied automatically).

Windows Command Prompt (cmd):

```bat
cd src\Chakki.Web
set Seed__DemoPassword=choose-a-password
set ASPNETCORE_ENVIRONMENT=Development
dotnet run
```

Windows PowerShell (the prompt starts with `PS`; `set` does not work there): `$env:Seed__DemoPassword='choose-a-password'; $env:ASPNETCORE_ENVIRONMENT='Development'; dotnet run`

### Tests

```bash
dotnet test
```

37 tests cover tenant isolation (filters, stamped writes, cross-tenant writes and foreign keys rejected), balanced journals,
forgiveness kept out of Udhaar, rate bands, FIFO Udhaar receipts, webhook signature / amount / replay, and chat language detection.

## Configuration

Nothing secret is in source control. Use environment variables, `dotnet user-secrets`, or a git-ignored `appsettings.Local.json`.

| Setting (env var form) | Purpose |
|---|---|
| `ConnectionStrings__Default` | SQLite connection string (default `App_Data/chakki.db`) |
| `DataProtection__KeysPath` | Where encryption keys live (default `App_Data/keys`). Back this up: gateway secrets are encrypted with it |
| `Seed__Demo`, `Seed__DemoPassword` | Create the demo chakkis (on in Development; needs the password) |
| `Seed__DemoWebhookSecret` | Optional webhook secret for the demo UPI gateway, to try signed webhooks locally |
| `Seed__PlatformAdminEmail`, `Seed__PlatformAdminPassword` | Creates the platform admin who can list, suspend and re-activate chakkis |
| `Ai__Provider` | `local` (default, offline rules) or `claude` |
| `ANTHROPIC_API_KEY` (or `Ai__AnthropicApiKey`) | Enables Claude for the chat when the provider is `claude` |
| `Ai__Model`, `Ai__Effort` | Default `claude-opus-5-5`, `low` |
| `Payments__AllowSimulation` | Show the "simulate payment" button outside Development (keep off in production) |

## Multi-tenancy: how chakkis are kept apart

Shared database, shared schema, one `OrganizationId` on every business row (`TenantEntity`). Three layers enforce it in `AppDbContext`:

1. **Read filter.** A global EF Core query filter on every tenant table limits every query to the signed-in user's chakki.
   With no chakki (e.g. the platform admin), tenant tables return nothing.
2. **Write guard.** `SaveChanges` stamps new rows with the current chakki and throws `CrossTenantWriteException` if a row
   belongs to another chakki or its `OrganizationId` is changed.
3. **Foreign-key guard.** Every reference to another tenant row (customer, item, order, gateway…) is checked to belong to the
   same chakki before saving, so a forged `customerId` from another chakki is refused.

The chakki comes only from the signed auth cookie (`chakki:org` claim), never from the URL or a form. Unique indexes
(order numbers, account codes, payment references) are per chakki. The security stamp is re-checked every minute, so a
suspended chakki or a disabled user is signed out quickly. The platform admin sees organisation names and counts only.

Public endpoints (pay link, webhooks) find the payment by its unguessable reference first and switch to that chakki only for
that request, after the webhook signature is verified.

## Payments

Set up gateways at **Settings → Payments** (owner only). Secrets are write-only and stored encrypted (ASP.NET Data Protection).

* **UPI QR**: enter your UPI ID (VPA). The bill screen shows a dynamic QR (`upi://pay?...&am=…&tr=<reference>`) that any UPI app
  can pay, plus a link `/pay/<reference>` to send on WhatsApp. The screen polls every 3 s and turns green when paid.
  Confirmation comes from your bank / aggregator webhook, or the owner can confirm manually with the UTR.
* **Card / UPI app (Razorpay)**: enter Key ID, Key Secret and a webhook secret. The customer pays in Razorpay Checkout; the
  signature is verified on the server, and the Razorpay webhook (`payment.captured`, `order.paid`) is a backup.

### Webhooks

```
POST /api/v1/payments/qr-webhook          header: X-Chakki-Signature (configurable)
POST /api/v1/payments/razorpay-webhook    header: X-Razorpay-Signature
```

Generic body:

```json
{ "payment_reference": "QR-…", "status": "SUCCESS", "amount": "55.00", "provider_payment_id": "UTR123" }
```

Signature = hex HMAC-SHA256 of the exact raw body with the gateway's webhook secret (`sha256=` prefix accepted).
Wrong signature → 401, amount mismatch → 422, non-success status → 202 ignored, replay → 200 "already settled" (posted once).

```bash
BODY='{"payment_reference":"QR-…","status":"SUCCESS","amount":"55.00","provider_payment_id":"UTR1"}'
SIG=$(printf '%s' "$BODY" | openssl dgst -sha256 -hmac "$SECRET" | awk '{print $2}')
curl -H "Content-Type: application/json" -H "X-Chakki-Signature: $SIG" --data-binary "$BODY" http://localhost:5050/api/v1/payments/qr-webhook
```

## Accounting

Seeded accounts per chakki: Cash, Bank / UPI, Udhaar (receivable), Inventory, Supplier payable, Buyback payable, Opening
equity, Grinding income, Sales income, Goodwill / forgiven, COGS, Utilities, Wages, Maintenance, General expense.
Every journal must balance; posted entries are never edited, only reversed.

For a ₹43 bill with ₹3 forgiven and ₹40 paid:

```text
Dr Udhaar (receivable)        ₹40
Dr Goodwill / forgiven         ₹3
    Cr Grinding income             ₹43

Dr Cash                       ₹40
    Cr Udhaar (receivable)         ₹40
```

Udhaar left: ₹0. Forgiveness never enters the customer's balance. Udhaar receipts settle Old Udhaar first, then the oldest bills.

Default rates (each chakki can change its own): Wheat → Atta ₹2.40 (₹2.00–3.00), Jalan ₹3.00, Chana → Besan ₹3.00,
Multigrain ₹3.50, Mustard oil extraction ₹5.00, Bajra and Makka ₹2.50 per kg.

Buyback: 15 kg mustard at ₹5 = ₹75 fee; customer sells 10 kg Khal at ₹20 = ₹200; ₹75 is adjusted against the fee and ₹125 is paid out.

## Roles

| Role | Can do |
|---|---|
| Owner | Everything in their chakki, including users, settings and payment gateways |
| Manager | Daily work, money, people, rates; larger forgiveness; manual payment confirmation |
| Operator | Counter: orders, billing, cash and QR, attendance, production, maintenance |
| Accountant | Books, reports, purchases, payroll, utilities |
| Platform admin | List and suspend chakkis. No access to any chakki's records |

## Hindi and English

Hindi is the default. The EN / हिं toggle in the top bar switches the whole UI and is remembered per user. Translations are
in `src/Chakki.Web/Resources/i18n/hi.json` (the English text is the key). Rates, items and accounts carry Hindi names.

## Chat assistant

Same idea as CoreApp-AI: the assistant can only **read** this chakki's data through a fixed set of tools (summary, customer
Udhaar, top debtors, pending orders, stock, expenses, maintenance). It never changes records.

* Default: offline rules, no API key needed, understands Hindi, Hinglish and English.
* With `Ai__Provider=claude` and `ANTHROPIC_API_KEY`: Claude answers using the same tools, in the language of the question,
  and falls back to the offline rules if the API is unreachable.

## Security

* Strict Content Security Policy (no inline scripts), HSTS outside Development, `X-Frame-Options: DENY`, no-sniff, referrer policy.
* Anti-forgery tokens on every form and HTMX request; rate limits on login, chat and webhooks; account lockout.
* Gateway secrets encrypted at rest and never shown again.
* Webhook secret is found from the payment record, never from the request.

## Production notes

* Run behind HTTPS (forwarded headers are honoured). Keep `App_Data` (database and keys) on a persistent, backed-up volume.
* For many chakkis, switch to PostgreSQL: add `Npgsql.EntityFrameworkCore.PostgreSQL`, change `UseSqlite`, and regenerate
  migrations. The tenancy guards do not depend on the database.
* Set `Seed__Demo=false` (the default outside Development).

## Project layout

```
src/Chakki.Web
  Controllers/     MVC + HTMX endpoints, webhooks
  Data/            AppDbContext (tenant guards), migrations
  Domain/          Entities, roles, system accounts
  Services/        Ledger, orders, payments, operations, onboarding, chat
  Tenancy/         Tenant context from the auth cookie
  Localization/    JSON localizer
  Views/           Razor views (CoreApp-AI theme)
  wwwroot/         CSS, JS, vendor (Bootstrap, AdminLTE, HTMX, ApexCharts)
tests/Chakki.Tests xUnit tests
```
