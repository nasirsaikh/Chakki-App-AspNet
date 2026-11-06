using System.Text.Json;
using Chakki.Web.Data;
using Chakki.Web.Domain;
using Chakki.Web.Infrastructure;
using Chakki.Web.Services.Chat;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

namespace Chakki.Web.Controllers;

public record ChatPageModel(List<ChatSession> Sessions, ChatSession? Current, List<ChatMessage> Messages, string Provider);
public record ChatExchange(ChatMessage Question, ChatMessage Answer);

/// <summary>Bilingual assistant. Each person sees only their own conversations, inside their own chakki.</summary>
[Route("chat")]
public class ChatController(AppDbContext db, ChatAssistant assistant, AiOptions ai) : AppController
{
    [HttpGet("")]
    public async Task<IActionResult> Index(int? session)
    {
        var sessions = await db.ChatSessions.Where(s => s.UserId == UserId).OrderByDescending(s => s.Id).Take(30).ToListAsync();
        var current = session is > 0 ? sessions.FirstOrDefault(s => s.Id == session) : null;
        var messages = current == null ? [] : await db.ChatMessages.Where(m => m.ChatSessionId == current.Id).OrderBy(m => m.Id).ToListAsync();
        var provider = ai.UsesClaude ? "claude" : "local";
        return View(new ChatPageModel(sessions, current, messages, provider));
    }

    [HttpPost("ask"), ValidateAntiForgeryToken, EnableRateLimiting("chat")]
    public async Task<IActionResult> Ask(int? session, string question, CancellationToken ct)
    {
        question = (question ?? "").Trim();
        if (question.Length == 0) return BadRequest();
        if (question.Length > 1000) question = question[..1000];

        var s = session is > 0 ? await db.ChatSessions.FirstOrDefaultAsync(x => x.Id == session && x.UserId == UserId, ct) : null;
        if (s == null)
        {
            s = new ChatSession { UserId = UserId, Title = question.Length > 60 ? question[..60] + "…" : question };
            db.ChatSessions.Add(s);
            await db.SaveChangesAsync(ct);
            Response.Headers["HX-Push-Url"] = $"/chat?session={s.Id}";
        }
        var history = await db.ChatMessages.Where(m => m.ChatSessionId == s.Id).OrderByDescending(m => m.Id).Take(10)
            .Select(m => new ChatTurn(m.Role, m.Content)).ToListAsync(ct);
        history.Reverse();

        var answer = await assistant.AnswerAsync(question, history, Hindi ? "hi" : "en", ct);
        var q = new ChatMessage { ChatSessionId = s.Id, Role = "user", Content = question, Language = answer.Language };
        var a = new ChatMessage
        {
            ChatSessionId = s.Id, Role = "assistant", Content = answer.Text, Language = answer.Language, Provider = answer.Provider,
            DataJson = answer.Table == null ? null : JsonSerializer.Serialize(answer.Table),
        };
        db.ChatMessages.AddRange(q, a);
        await db.SaveChangesAsync(ct);
        ViewData["SessionId"] = s.Id;
        return PartialView("_Exchange", new ChatExchange(q, a));
    }
}
