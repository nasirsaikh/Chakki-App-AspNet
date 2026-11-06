using System.Text.Json;
using System.Text.RegularExpressions;
using Anthropic;
using Anthropic.Models.Messages;

namespace Chakki.Web.Services.Chat;

public record ChatTurn(string Role, string Content);

public record ChatAnswer(string Text, ChatTable? Table, string Provider, string Language);

public class AiOptions
{
    /// <summary>"local" (offline rules, default) or "claude".</summary>
    public string Provider { get; set; } = "local";
    public string Model { get; set; } = "claude-opus-5-5";
    /// <summary>Leave empty in config; set ANTHROPIC_API_KEY in the environment or user-secrets.</summary>
    public string? AnthropicApiKey { get; set; }
    public string Effort { get; set; } = "low";

    /// <summary>The key from configuration, or ANTHROPIC_API_KEY from the environment; null when neither is set.</summary>
    public string? ResolveKey() => string.IsNullOrWhiteSpace(AnthropicApiKey) ? Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY") is { Length: > 0 } k ? k : null : AnthropicApiKey;
    public bool UsesClaude => Provider.Equals("claude", StringComparison.OrdinalIgnoreCase) && ResolveKey() != null;
}

public static class LanguageDetector
{
    private static readonly Regex Devanagari = new(@"[ऀ-ॿ]", RegexOptions.Compiled);
    private static readonly string[] Hinglish = ["kitna", "kitne", "kya", "kaun", "aaj", "kal", "batao", "bata", "hai", "hain", "udhar", "udhaar", "pisai", "kab", "mahine", "dikhao", "kiska", "kitni", "baki", "baaki"];

    /// <summary>Hindi when the text is in Devanagari or uses common Hinglish words; otherwise the UI language.</summary>
    public static string Detect(string text, string uiLanguage)
    {
        if (Devanagari.IsMatch(text)) return "hi";
        var words = Regex.Split(text.ToLowerInvariant(), @"\W+");
        if (words.Any(w => Hinglish.Contains(w))) return "hi";
        return Regex.IsMatch(text, "[a-zA-Z]{2,}") ? "en" : uiLanguage;
    }
}

/// <summary>
/// Chat in Hindi or English, following the CoreApp-AI idea "AI proposes, the backend decides": the assistant only
/// reads tenant-scoped data through <see cref="ChatTools"/>; it cannot change records. Works fully offline with the
/// rule engine; when Ai:Provider = claude and an API key is configured, Claude answers using the same tools.
/// </summary>
public class ChatAssistant(ChatTools tools, AiOptions options, ILogger<ChatAssistant> log)
{
    public async Task<ChatAnswer> AnswerAsync(string question, IReadOnlyList<ChatTurn> history, string uiLanguage, CancellationToken ct = default)
    {
        var lang = LanguageDetector.Detect(question, uiLanguage);
        var key = options.ResolveKey();
        if (options.UsesClaude && key != null)
        {
            try { return await AskClaudeAsync(question, history, lang, key, ct); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogWarning(ex, "Claude chat failed; falling back to local rules");
            }
        }
        return await AnswerLocallyAsync(question, lang);
    }

    // ------------------------------------------------------------------ Local, deterministic rules (EN / HI / Hinglish)

    public async Task<ChatAnswer> AnswerLocallyAsync(string question, string lang)
    {
        var hi = lang == "hi";
        var q = question.ToLowerInvariant();
        var period = DetectPeriod(q);
        ToolResult result;

        if (Has(q, "help", "madad", "मदद", "क्या पूछ", "kya puch"))
            result = new(hi
                ? "आप पूछ सकते हैं: \"आज की पिसाई कितनी हुई?\", \"रमेश का उधार कितना है?\", \"सबसे ज़्यादा उधार किसका है?\", \"कौन से ऑर्डर तैयार हैं?\", \"कम स्टॉक दिखाओ\", \"इस महीने का खर्च\", \"मरम्मत कब है?\""
                : "Try: \"How much did we grind today?\", \"What does Ramesh owe?\", \"Who owes the most?\", \"Which orders are pending?\", \"Show low stock\", \"Expenses this month\", \"What maintenance is due?\"");
        else if (Has(q, "top", "most", "sabse", "सबसे", "highest", "debtors", "defaulter"))
            result = await tools.TopDebtorsAsync(10, hi);
        else if (ExtractCustomer(question) is { } name)
            result = await tools.CustomerBalanceAsync(name, hi);
        else if (Has(q, "pending", "ready", "order", "ऑर्डर", "आर्डर", "taiyar", "तैयार", "लंबित", "baki order"))
            result = await tools.PendingOrdersAsync(hi);
        else if (Has(q, "stock", "inventory", "स्टॉक", "माल", "saman", "सामान"))
            result = await tools.StockAsync(Has(q, "low", "kam", "कम", "reorder", "khatam", "ख़त्म", "खत्म"), hi);
        else if (Has(q, "expense", "kharch", "खर्च", "kharcha", "bijli", "बिजली"))
            result = await tools.ExpensesAsync(period, hi);
        else if (Has(q, "maintenance", "takai", "टकाई", "मरम्मत", "repair", "belt", "बेल्ट"))
            result = await tools.MaintenanceDueAsync(hi);
        else if (Has(q, "udhaar", "udhar", "उधार", "baki", "बाकी", "outstanding", "due"))
            result = await tools.TopDebtorsAsync(10, hi);
        else
            result = await tools.SummaryAsync(period, hi);

        return new ChatAnswer(result.Summary, result.Table, "local", lang);
    }

    private static bool Has(string q, params string[] words) => words.Any(q.Contains);

    public static string DetectPeriod(string q)
    {
        if (Has(q, "yesterday", "कल का", "kal ka", "kal ki", "कल की")) return "yesterday";
        if (Has(q, "week", "hafte", "हफ्ते", "हफ़्ते", "7 din", "7 दिन")) return "week";
        if (Has(q, "month", "mahine", "महीने", "mahina", "महीना")) return "month";
        if (Has(q, "year", "saal", "साल")) return "year";
        return "today";
    }

    private static readonly Regex[] CustomerPatterns =
    [
        new(@"(?:does|did|is)\s+(?<n>[\p{L} ]{2,40}?)\s+(?:owe|have)", RegexOptions.IgnoreCase),
        new(@"(?:balance|udhaa?r|due)\s+(?:of|for)\s+(?<n>[\p{L} ]{2,40})", RegexOptions.IgnoreCase),
        new(@"(?<n>[\p{L} ]{2,40}?)\s+(?:ka|ki|के|का|की)\s+(?:udhaa?r|उधार|baki|बाकी|hisab|हिसाब)", RegexOptions.IgnoreCase),
    ];

    public static string? ExtractCustomer(string question)
    {
        foreach (var re in CustomerPatterns)
        {
            var m = re.Match(question);
            if (!m.Success) continue;
            var name = m.Groups["n"].Value.Trim();
            var stop = new[] { "what", "how", "much", "kitna", "कितना", "sabse", "सबसे", "kis", "किस", "kiska", "किसका", "customer", "total", "कुल", "sab", "सब" };
            name = string.Join(' ', name.Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(w => !stop.Contains(w.ToLowerInvariant())));
            if (name.Length >= 2) return name;
        }
        return null;
    }

    // ------------------------------------------------------------------ Claude with tool use

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static Tool MakeTool(string name, string description, Dictionary<string, object>? props = null, string[]? required = null) => new()
    {
        Name = name,
        Description = description,
        InputSchema = new()
        {
            Properties = (props ?? []).ToDictionary(kv => kv.Key, kv => JsonSerializer.SerializeToElement(kv.Value)),
            Required = required ?? [],
        },
    };

    private static readonly object PeriodProp = new { type = "string", @enum = new[] { "today", "yesterday", "week", "month", "year" } };

    private static readonly List<ToolUnion> ToolDefs =
    [
        MakeTool("business_summary", "Grinding weight, income, expenses, net, cash received, total Udhaar and pending orders for a period.", new() { ["period"] = PeriodProp }),
        MakeTool("customer_balance", "Outstanding Udhaar (credit) of a customer, searched by part of the name or phone.", new() { ["name"] = new { type = "string" } }, ["name"]),
        MakeTool("top_debtors", "Customers with the highest outstanding Udhaar.", new() { ["count"] = new { type = "integer", minimum = 1, maximum = 25 } }),
        MakeTool("pending_orders", "Grinding orders not yet delivered (received, grinding, ready)."),
        MakeTool("stock_levels", "Inventory quantities and value; low_only lists items at or below reorder level.", new() { ["low_only"] = new { type = "boolean" } }),
        MakeTool("expenses", "Expenses by account for a period.", new() { ["period"] = PeriodProp }),
        MakeTool("maintenance_due", "Maintenance and stone dressing (Takai) tasks due within 7 days."),
    ];

    private async Task<ToolResult> RunToolAsync(string name, JsonElement input, bool hi)
    {
        string? Str(string p) => input.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        return name switch
        {
            "business_summary" => await tools.SummaryAsync(Str("period"), hi),
            "customer_balance" => await tools.CustomerBalanceAsync(Str("name") ?? "", hi),
            "top_debtors" => await tools.TopDebtorsAsync(input.TryGetProperty("count", out var c) && c.TryGetInt32(out var n) ? n : 10, hi),
            "pending_orders" => await tools.PendingOrdersAsync(hi),
            "stock_levels" => await tools.StockAsync(input.TryGetProperty("low_only", out var l) && l.ValueKind == JsonValueKind.True, hi),
            "expenses" => await tools.ExpensesAsync(Str("period"), hi),
            "maintenance_due" => await tools.MaintenanceDueAsync(hi),
            _ => new ToolResult("Unknown tool."),
        };
    }

    private async Task<ChatAnswer> AskClaudeAsync(string question, IReadOnlyList<ChatTurn> history, string lang, string apiKey, CancellationToken ct)
    {
        var hi = lang == "hi";
        AnthropicClient client = new() { ApiKey = apiKey };
        var system =
            "You are the assistant inside a flour mill (chakki) management app used at a village counter in India. " +
            "Answer questions about this chakki's business using the tools; never invent figures. Amounts are in Indian rupees (₹). " +
            "You can only read data; if the user asks to change something, tell them which screen to use. " +
            "Keep answers short (2-4 sentences) and practical. " +
            (hi ? "Reply in simple Hindi written in Devanagari script." : "Reply in simple English.") +
            " If the user writes in Hinglish, reply in Hindi.";

        List<MessageParam> messages = [];
        foreach (var turn in history.TakeLast(10))
            messages.Add(turn.Role == "user" ? new UserMessageParam { Content = turn.Content } : new AssistantMessageParam { Content = turn.Content });
        messages.Add(new UserMessageParam { Content = question });

        ChatTable? lastTable = null;
        for (var round = 0; round < 5; round++)
        {
            var response = await client.Messages.Create(new MessageCreateParams
            {
                Model = options.Model,
                MaxTokens = 4000,
                System = system,
                Tools = ToolDefs,
                Messages = messages,
                OutputConfig = new OutputConfig { Effort = options.Effort },
            }, ct);

            List<ContentBlockParam> assistant = [];
            List<ContentBlockParam> results = [];
            var text = new System.Text.StringBuilder();
            foreach (var block in response.Content)
            {
                if (block.TryPickText(out var t))
                {
                    assistant.Add(new TextBlockParam { Text = t.Text });
                    text.Append(t.Text);
                }
                else if (block.TryPickThinking(out var th))
                    assistant.Add(new ThinkingBlockParam { Thinking = th.Thinking, Signature = th.Signature });
                else if (block.TryPickRedactedThinking(out var rt))
                    assistant.Add(new RedactedThinkingBlockParam { Data = rt.Data });
                else if (block.TryPickToolUse(out var tu))
                {
                    assistant.Add(new ToolUseBlockParam { ID = tu.ID, Name = tu.Name, Input = tu.Input });
                    var input = JsonSerializer.SerializeToElement(tu.Input, Json);
                    var r = await RunToolAsync(tu.Name, input, hi);
                    lastTable = r.Table ?? lastTable;
                    results.Add(new ToolResultBlockParam { ToolUseID = tu.ID, Content = JsonSerializer.Serialize(new { r.Summary, r.Table }, Json) });
                }
            }
            if (results.Count == 0)
                return new ChatAnswer(text.Length > 0 ? text.ToString().Trim() : (hi ? "माफ़ कीजिए, जवाब नहीं मिला।" : "Sorry, I could not find an answer."), lastTable, "claude", lang);
            messages.Add(new AssistantMessageParam { Content = assistant });
            messages.Add(new UserMessageParam { Content = results });
        }
        return new ChatAnswer(hi ? "यह सवाल बहुत जटिल है, कृपया छोटा करके पूछें।" : "That took too many steps; please ask a narrower question.", lastTable, "claude", lang);
    }
}
