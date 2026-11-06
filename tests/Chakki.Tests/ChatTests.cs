using Chakki.Web.Services.Chat;
using Microsoft.Extensions.Logging.Abstractions;

namespace Chakki.Tests;

public class ChatTests
{
    [Theory]
    [InlineData("आज की बिक्री कितनी हुई?", "en", "hi")]
    [InlineData("Ramesh ka udhaar kitna hai", "en", "hi")]
    [InlineData("How much did we grind today?", "hi", "en")]
    [InlineData("123", "hi", "hi")]
    public void Language_follows_the_question(string q, string ui, string expected) => Assert.Equal(expected, LanguageDetector.Detect(q, ui));

    [Fact]
    public async Task Offline_answers_use_only_the_current_chakkis_data()
    {
        using var env = await TestEnv.CreateAsync();
        using (env.As(env.OrgB)) await env.BilledOrderAsync(10);
        env.Db.ChangeTracker.Clear();
        using (env.As(env.OrgA))
        {
            var chat = new ChatAssistant(new ChatTools(env.Db, env.Ledger), new AiOptions(), NullLogger<ChatAssistant>.Instance);
            var en = await chat.AnswerAsync("Which customers owe the most?", [], "en");
            Assert.Equal("en", en.Language);
            Assert.DoesNotContain("Chakki B", en.Text + string.Join(" ", en.Table?.Rows.SelectMany(r => r) ?? []));
            var hi = await chat.AnswerAsync("आज कितनी पिसाई हुई?", [], "en");
            Assert.Equal("hi", hi.Language);
        }
    }
}
