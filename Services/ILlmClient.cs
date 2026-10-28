using System.Text.Json.Nodes;

namespace ClinicBot.Services;

/// <summary>
/// Provider-agnostic chat completion contract. BotEngine, ConversationStore and the tool-call
/// loop all speak this shape regardless of which model answers: <paramref name="messages"/> use
/// Claude's wire format (role user/assistant; content either a plain string or an array of
/// blocks with type "text" / "tool_use" / "tool_result"), and the returned JsonObject always has
/// a "content" array of "text"/"tool_use" blocks plus a "stop_reason" that is "tool_use" when a
/// tool call is requested. Non-Claude implementations translate to/from this shape internally so
/// BotEngine never needs to know which provider answered.
/// </summary>
public interface ILlmClient
{
    Task<JsonObject> CreateMessageAsync(JsonArray systemBlocks, JsonArray messages, JsonArray tools, CancellationToken ct);
}
