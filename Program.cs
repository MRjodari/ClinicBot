using System.Text.Json;
using System.Text.Json.Nodes;
using ClinicBot.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<BotOptions>(builder.Configuration.GetSection("Bot"));
builder.Services.Configure<ClaudeOptions>(builder.Configuration.GetSection("Claude"));
builder.Services.Configure<WhatsAppOptions>(builder.Configuration.GetSection("WhatsApp"));

builder.Services.AddHttpClient<ClaudeClient>();
builder.Services.AddHttpClient<WhatsAppClient>();
builder.Services.AddSingleton<KnowledgeBase>();
builder.Services.AddSingleton<ConversationStore>();
builder.Services.AddSingleton<AppointmentStore>();
builder.Services.AddSingleton<BotEngine>();

var app = builder.Build();
app.UseDefaultFiles();
app.UseStaticFiles();

// ---------- Web simulator (works without any Meta setup) ----------
app.MapPost("/api/chat", async (ChatRequest req, BotEngine bot, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(req.SessionId) || string.IsNullOrWhiteSpace(req.Message))
        return Results.BadRequest(new { error = "sessionId and message are required" });

    var reply = await bot.HandleAsync(req.SessionId, req.Message, channel: "web", ct);
    return Results.Ok(new { reply });
});

app.MapPost("/api/reset", (ChatRequest req, ConversationStore store) =>
{
    store.Reset(req.SessionId);
    return Results.Ok();
});

app.MapGet("/api/appointments", (AppointmentStore store) => Results.Ok(store.All()));
app.MapGet("/api/clinic", (KnowledgeBase kb) => Results.Ok(new { name = kb.ClinicName, chunks = kb.ChunkCount }));

// ---------- WhatsApp Cloud API webhook ----------
// Verification handshake when you register the webhook in the Meta app dashboard.
app.MapGet("/webhook", (HttpRequest r, Microsoft.Extensions.Options.IOptions<WhatsAppOptions> opt) =>
{
    var mode = r.Query["hub.mode"].ToString();
    var token = r.Query["hub.verify_token"].ToString();
    var challenge = r.Query["hub.challenge"].ToString();
    return mode == "subscribe" && token == opt.Value.VerifyToken
        ? Results.Text(challenge)
        : Results.StatusCode(403);
});

// Incoming messages. Meta expects a fast 200, so the bot runs in the background.
app.MapPost("/webhook", async (HttpRequest r, WhatsAppClient wa, BotEngine bot, ILogger<Program> log) =>
{
    using var reader = new StreamReader(r.Body);
    var raw = await reader.ReadToEndAsync();

    if (!wa.IsSignatureValid(raw, r.Headers["X-Hub-Signature-256"].ToString()))
        return Results.StatusCode(401);

    foreach (var msg in WhatsAppClient.ParseIncoming(raw))
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await wa.MarkReadAsync(msg.MessageId);
                var reply = await bot.HandleAsync(msg.From, msg.Text, channel: "whatsapp", CancellationToken.None,
                    contactName: msg.ProfileName);
                await wa.SendTextAsync(msg.From, reply);
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Failed to handle WhatsApp message from {From}", msg.From);
            }
        });
    }
    return Results.Ok();
});

app.Run();

public record ChatRequest(string SessionId, string Message);
