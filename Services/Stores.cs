using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;

namespace ClinicBot.Services;

/// <summary>In-memory chat history per WhatsApp number / web session. Fine for a demo and first pilots;
/// move to SQLite/Postgres when you have more than a handful of clinics.</summary>
public class ConversationStore
{
    private readonly ConcurrentDictionary<string, Conversation> _byUser = new();
    private readonly int _maxTurns;

    public ConversationStore(IOptions<BotOptions> opt) => _maxTurns = opt.Value.MaxHistoryTurns;

    public Conversation Get(string userId) => _byUser.GetOrAdd(userId, _ => new Conversation());
    public void Reset(string userId) => _byUser.TryRemove(userId, out _);

    /// <summary>Drops the oldest turns, cutting only where a plain user text message starts,
    /// so tool_use / tool_result pairs never get split.</summary>
    public void Trim(Conversation c)
    {
        while (c.Messages.Count > _maxTurns * 2)
        {
            var cut = -1;
            for (var i = 1; i < c.Messages.Count; i++)
            {
                var m = c.Messages[i]!;
                if (m["role"]!.GetValue<string>() == "user" && m["content"] is JsonValue) { cut = i; break; }
            }
            if (cut <= 0) break;
            for (var i = 0; i < cut; i++) c.Messages.RemoveAt(0);
        }
    }
}

public class Conversation
{
    public JsonArray Messages { get; } = new();
    public SemaphoreSlim Lock { get; } = new(1, 1);
    public bool HandedOff { get; set; }
}

public record AppointmentRequest(
    string Id, DateTimeOffset CreatedAt, string Channel, string UserId,
    string PatientName, string Phone, string Treatment, string PreferredDates,
    string Language, string? Country, string? Notes, string Status);

/// <summary>Appointment requests persisted to data/appointments.json. The clinic coordinator confirms them;
/// the bot never promises a slot. Real calendar integration comes after the first paying clinic asks for it.</summary>
public class AppointmentStore
{
    private readonly string _path;
    private readonly object _gate = new();
    private readonly List<AppointmentRequest> _items;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public AppointmentStore(IWebHostEnvironment env)
    {
        var dir = Path.Combine(env.ContentRootPath, "data");
        Directory.CreateDirectory(dir);
        _path = Path.Combine(dir, "appointments.json");
        _items = File.Exists(_path)
            ? JsonSerializer.Deserialize<List<AppointmentRequest>>(File.ReadAllText(_path), Json) ?? new()
            : new();
    }

    public AppointmentRequest Add(AppointmentRequest a)
    {
        lock (_gate)
        {
            _items.Add(a);
            File.WriteAllText(_path, JsonSerializer.Serialize(_items, Json));
        }
        return a;
    }

    public IReadOnlyList<AppointmentRequest> All()
    {
        lock (_gate) return _items.OrderByDescending(a => a.CreatedAt).ToList();
    }
}
