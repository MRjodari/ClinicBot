using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;

namespace ClinicBot.Services;

/// <summary>Thin wrapper over the Anthropic Messages API (no SDK dependency).</summary>
public class ClaudeClient : ILlmClient
{
    private readonly HttpClient _http;
    private readonly ClaudeOptions _opt;

    public ClaudeClient(HttpClient http, IOptions<ClaudeOptions> opt)
    {
        _http = http;
        _opt = opt.Value;
        _http.BaseAddress = new Uri(_opt.BaseUrl);
        _http.Timeout = TimeSpan.FromSeconds(60);
    }

    /// <param name="systemBlocks">System prompt blocks; mark the large static one cacheable.</param>
    public async Task<JsonObject> CreateMessageAsync(JsonArray systemBlocks, JsonArray messages, JsonArray tools, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_opt.ApiKey))
            throw new InvalidOperationException("Claude:ApiKey is not configured (set env var Claude__ApiKey).");

        var body = new JsonObject
        {
            ["model"] = _opt.Model,
            ["max_tokens"] = _opt.MaxTokens,
            ["system"] = systemBlocks.DeepClone(),
            ["messages"] = messages.DeepClone(),
            ["tools"] = tools.DeepClone(),
        };

        using var req = new HttpRequestMessage(HttpMethod.Post, "v1/messages")
        {
            Content = new StringContent(body.ToJsonString(), System.Text.Encoding.UTF8, "application/json")
        };
        req.Headers.Add("x-api-key", _opt.ApiKey);
        req.Headers.Add("anthropic-version", "2023-06-01");

        using var res = await _http.SendAsync(req, ct);
        var text = await res.Content.ReadAsStringAsync(ct);
        if (!res.IsSuccessStatusCode)
            throw new HttpRequestException($"Claude API {(int)res.StatusCode}: {text}");

        return JsonNode.Parse(text)!.AsObject();
    }
}
