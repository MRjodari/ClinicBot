namespace ClinicBot.Services;

public class BotOptions
{
    /// <summary>Folder (under knowledge/) holding this client's markdown files. One folder per customer.</summary>
    public string KnowledgeFolder { get; set; } = "knowledge/demo-clinic";
    /// <summary>Below this many characters the whole knowledge base goes into the prompt (cached). Above it, top-K retrieval.</summary>
    public int FullContextMaxChars { get; set; } = 60_000;
    public int RetrievalTopK { get; set; } = 6;
    public int MaxHistoryTurns { get; set; } = 20;
    /// <summary>Optional WhatsApp number of the clinic coordinator, notified on new requests / handoffs (E.164 without +).</summary>
    public string? StaffNotifyNumber { get; set; }
}

public class ClaudeOptions
{
    public string ApiKey { get; set; } = "";
    public string Model { get; set; } = "claude-haiku-5-5";
    public int MaxTokens { get; set; } = 800;
    public string BaseUrl { get; set; } = "https://api.anthropic.com/";
}

public class WhatsAppOptions
{
    public string AccessToken { get; set; } = "";
    public string PhoneNumberId { get; set; } = "";
    public string VerifyToken { get; set; } = "change-me";
    /// <summary>Meta app secret; used to validate X-Hub-Signature-256. Leave empty only for local testing.</summary>
    public string AppSecret { get; set; } = "";
    public string GraphVersion { get; set; } = "v23.0";
}
