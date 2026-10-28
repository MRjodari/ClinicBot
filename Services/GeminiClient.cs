using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;

namespace ClinicBot.Services;

/// <summary>
/// Thin wrapper over the Gemini (generateContent) API that speaks the same Claude-shaped
/// request/response contract as <see cref="ClaudeClient"/> (see <see cref="ILlmClient"/>), so
/// BotEngine and ConversationStore don't need to know which provider is active.
///
/// Translation notes (verify against a live call before relying on these in production):
/// - Claude's per-block "cache_control" has no Gemini equivalent at this message size (Gemini's
///   explicit context-caching API has a multi-thousand-token minimum); system blocks are just
///   concatenated into one system_instruction, caching is dropped for this provider.
/// - Claude tool_use/tool_result round-trips via a shared "id"; Gemini's functionCall has no id,
///   so this client mints one when translating a model response and relies on it only ever being
///   read back from the same conversation history it was written into.
/// - The function-response turn is sent with role "user" (matching Claude's tool_result
///   convention and the common Gemini SDK pattern). If Gemini rejects multi-turn tool replies,
///   this is the first thing to check against the current API docs.
/// </summary>
public class GeminiClient : ILlmClient
{
    private readonly HttpClient _http;
    private readonly GeminiOptions _opt;

    public GeminiClient(HttpClient http, IOptions<GeminiOptions> opt)
    {
        _http = http;
        _opt = opt.Value;
        _http.BaseAddress = new Uri(_opt.BaseUrl.TrimEnd('/') + "/");
        _http.Timeout = TimeSpan.FromSeconds(60);
    }

    public async Task<JsonObject> CreateMessageAsync(JsonArray systemBlocks, JsonArray messages, JsonArray tools, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_opt.ApiKey))
            throw new InvalidOperationException("Gemini:ApiKey is not configured (set env var Gemini__ApiKey).");

        var body = new JsonObject
        {
            ["contents"] = BuildContents(messages),
            ["system_instruction"] = new JsonObject
            {
                ["parts"] = new JsonArray(new JsonObject { ["text"] = CombineSystemText(systemBlocks) })
            },
            ["generationConfig"] = new JsonObject { ["maxOutputTokens"] = _opt.MaxTokens }
        };
        var geminiTools = BuildTools(tools);
        if (geminiTools is not null) body["tools"] = geminiTools;

        using var req = new HttpRequestMessage(HttpMethod.Post, $"models/{_opt.Model}:generateContent")
        {
            Content = new StringContent(body.ToJsonString(), System.Text.Encoding.UTF8, "application/json")
        };
        req.Headers.Add("x-goog-api-key", _opt.ApiKey);

        using var res = await _http.SendAsync(req, ct);
        var text = await res.Content.ReadAsStringAsync(ct);
        if (!res.IsSuccessStatusCode)
            throw new HttpRequestException($"Gemini API {(int)res.StatusCode}: {text}");

        return ToClaudeShapedResponse(JsonNode.Parse(text)!.AsObject());
    }

    // ---------- request translation: Claude-shaped -> Gemini-shaped ----------

    private static string CombineSystemText(JsonArray systemBlocks) =>
        string.Join("\n", systemBlocks
            .Select(b => b?["text"]?.GetValue<string>())
            .Where(t => !string.IsNullOrEmpty(t)));

    private static JsonArray BuildContents(JsonArray messages)
    {
        var toolNameById = new Dictionary<string, string>();
        var contents = new JsonArray();

        foreach (var message in messages)
        {
            if (message is null) continue;
            var role = message["role"]!.GetValue<string>() == "assistant" ? "model" : "user";
            var parts = new JsonArray();
            var content = message["content"];

            if (content is JsonValue textValue)
            {
                parts.Add(new JsonObject { ["text"] = textValue.GetValue<string>() });
            }
            else
            {
                foreach (var block in content!.AsArray())
                {
                    switch (block?["type"]?.GetValue<string>())
                    {
                        case "text":
                            parts.Add(new JsonObject { ["text"] = block["text"]!.GetValue<string>() });
                            break;
                        case "tool_use":
                        {
                            var id = block["id"]!.GetValue<string>();
                            var name = block["name"]!.GetValue<string>();
                            toolNameById[id] = name;
                            parts.Add(new JsonObject
                            {
                                ["functionCall"] = new JsonObject { ["name"] = name, ["args"] = block["input"]!.DeepClone() }
                            });
                            break;
                        }
                        case "tool_result":
                        {
                            var toolUseId = block["tool_use_id"]!.GetValue<string>();
                            var name = toolNameById.GetValueOrDefault(toolUseId, toolUseId);
                            parts.Add(new JsonObject
                            {
                                ["functionResponse"] = new JsonObject
                                {
                                    ["name"] = name,
                                    ["response"] = new JsonObject { ["result"] = block["content"]!.DeepClone() }
                                }
                            });
                            break;
                        }
                    }
                }
            }

            contents.Add(new JsonObject { ["role"] = role, ["parts"] = parts });
        }

        return contents;
    }

    private static JsonArray? BuildTools(JsonArray tools)
    {
        if (tools.Count == 0) return null;
        var declarations = new JsonArray();
        foreach (var tool in tools)
        {
            declarations.Add(new JsonObject
            {
                ["name"] = tool!["name"]!.GetValue<string>(),
                ["description"] = tool["description"]!.GetValue<string>(),
                ["parameters"] = ConvertSchema(tool["input_schema"]!.DeepClone())
            });
        }
        return new JsonArray(new JsonObject { ["functionDeclarations"] = declarations });
    }

    /// <summary>JSON Schema ("object"/"string"/...) -> Gemini's Schema proto enum names ("OBJECT"/"STRING"/...).</summary>
    private static JsonNode ConvertSchema(JsonNode schema)
    {
        if (schema is JsonObject obj)
        {
            if (obj["type"] is JsonValue t && t.GetValue<string>() is { } type)
                obj["type"] = type.ToUpperInvariant();
            if (obj["properties"] is JsonObject props)
                foreach (var key in props.Select(p => p.Key).ToList())
                    props[key] = ConvertSchema(props[key]!.DeepClone());
            if (obj["items"] is JsonNode items)
                obj["items"] = ConvertSchema(items.DeepClone());
        }
        return schema;
    }

    // ---------- response translation: Gemini-shaped -> Claude-shaped ----------

    private static JsonObject ToClaudeShapedResponse(JsonObject gemini)
    {
        var candidate = gemini["candidates"]?.AsArray().FirstOrDefault();
        if (candidate is null)
        {
            var blockReason = gemini["promptFeedback"]?["blockReason"]?.GetValue<string>() ?? "unknown";
            throw new HttpRequestException($"Gemini returned no candidates (blockReason: {blockReason}).");
        }

        var content = new JsonArray();
        var sawFunctionCall = false;
        foreach (var part in candidate["content"]?["parts"]?.AsArray() ?? new JsonArray())
        {
            if (part?["text"] is JsonValue text)
            {
                content.Add(new JsonObject { ["type"] = "text", ["text"] = text.GetValue<string>() });
            }
            else if (part?["functionCall"] is JsonObject call)
            {
                sawFunctionCall = true;
                content.Add(new JsonObject
                {
                    ["type"] = "tool_use",
                    ["id"] = Guid.NewGuid().ToString("N"),
                    ["name"] = call["name"]!.GetValue<string>(),
                    ["input"] = call["args"]?.DeepClone() ?? new JsonObject()
                });
            }
        }

        return new JsonObject
        {
            ["content"] = content,
            ["stop_reason"] = sawFunctionCall ? "tool_use" : "end_turn"
        };
    }
}
