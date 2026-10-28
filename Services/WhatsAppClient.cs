using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;

namespace ClinicBot.Services;

public record IncomingMessage(string From, string Text, string MessageId, string? ProfileName);

/// <summary>Minimal WhatsApp Cloud API client: parse webhooks, send text, mark read.</summary>
public class WhatsAppClient
{
    private readonly HttpClient _http;
    private readonly WhatsAppOptions _opt;
    private readonly ILogger<WhatsAppClient> _log;

    public WhatsAppClient(HttpClient http, IOptions<WhatsAppOptions> opt, ILogger<WhatsAppClient> log)
    {
        _http = http;
        _opt = opt.Value;
        _log = log;
        _http.BaseAddress = new Uri($"https://graph.facebook.com/{_opt.GraphVersion}/");
    }

    public bool IsConfigured => !string.IsNullOrEmpty(_opt.AccessToken) && !string.IsNullOrEmpty(_opt.PhoneNumberId);

    public bool IsSignatureValid(string rawBody, string header)
    {
        if (string.IsNullOrEmpty(_opt.AppSecret)) return true; // local testing only
        if (string.IsNullOrEmpty(header) || !header.StartsWith("sha256=")) return false;
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(_opt.AppSecret));
        var expected = "sha256=" + Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(rawBody))).ToLowerInvariant();
        return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(expected), Encoding.UTF8.GetBytes(header));
    }

    /// <summary>Extracts text messages from a webhook payload. Non-text messages get a placeholder so the bot can ask for text.</summary>
    public static IEnumerable<IncomingMessage> ParseIncoming(string raw)
    {
        var root = JsonNode.Parse(raw);
        foreach (var entry in root?["entry"]?.AsArray() ?? new JsonArray())
        foreach (var change in entry?["changes"]?.AsArray() ?? new JsonArray())
        {
            var value = change?["value"];
            var name = value?["contacts"]?[0]?["profile"]?["name"]?.GetValue<string>();
            foreach (var m in value?["messages"]?.AsArray() ?? new JsonArray())
            {
                if (m is null) continue;
                var type = m["type"]?.GetValue<string>();
                var text = type switch
                {
                    "text" => m["text"]?["body"]?.GetValue<string>(),
                    "button" => m["button"]?["text"]?.GetValue<string>(),
                    "interactive" => m["interactive"]?["button_reply"]?["title"]?.GetValue<string>()
                                     ?? m["interactive"]?["list_reply"]?["title"]?.GetValue<string>(),
                    _ => $"[The patient sent a {type} message, which the assistant cannot read. Ask them to write it as text, or offer to pass it to the staff.]"
                };
                yield return new IncomingMessage(
                    m["from"]!.GetValue<string>(),
                    text ?? "",
                    m["id"]!.GetValue<string>(),
                    name);
            }
        }
    }

    public async Task SendTextAsync(string to, string body)
    {
        if (!IsConfigured) { _log.LogWarning("WhatsApp not configured; would send to {To}: {Body}", to, body); return; }
        // WhatsApp text limit is 4096 chars.
        if (body.Length > 4000) body = body[..4000];
        await PostAsync(new JsonObject
        {
            ["messaging_product"] = "whatsapp",
            ["to"] = to,
            ["type"] = "text",
            ["text"] = new JsonObject { ["body"] = body, ["preview_url"] = false }
        });
    }

    public async Task MarkReadAsync(string messageId)
    {
        if (!IsConfigured) return;
        await PostAsync(new JsonObject
        {
            ["messaging_product"] = "whatsapp",
            ["status"] = "read",
            ["message_id"] = messageId
        });
    }

    private async Task PostAsync(JsonObject payload)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{_opt.PhoneNumberId}/messages")
        {
            Content = new StringContent(payload.ToJsonString(), System.Text.Encoding.UTF8, "application/json")
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _opt.AccessToken);
        using var res = await _http.SendAsync(req);
        if (!res.IsSuccessStatusCode)
            _log.LogError("WhatsApp API {Status}: {Body}", (int)res.StatusCode, await res.Content.ReadAsStringAsync());
    }
}
