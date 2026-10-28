using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;

namespace ClinicBot.Services;

public record Chunk(string Source, string Heading, string Text);

/// <summary>
/// Loads a customer's markdown files and serves context to the bot.
/// Small knowledge bases (a clinic's FAQ, prices, hours) go into the prompt whole and get cached —
/// cheaper and more accurate than retrieval. Large ones (law firm / customs regulations) fall back to
/// BM25 top-K. Swap <see cref="Retrieve"/> for your embedding-based retriever when you take on that niche.
/// </summary>
public class KnowledgeBase
{
    private readonly List<Chunk> _chunks = new();
    private readonly BotOptions _opt;
    private readonly Dictionary<string, int>[] _tf;
    private readonly Dictionary<string, int> _df = new();
    private readonly double _avgLen;

    public string ClinicName { get; }
    public string FullText { get; }
    public int ChunkCount => _chunks.Count;
    public bool UseFullContext => FullText.Length <= _opt.FullContextMaxChars;

    public KnowledgeBase(IOptions<BotOptions> opt, IWebHostEnvironment env, ILogger<KnowledgeBase> log)
    {
        _opt = opt.Value;
        var dir = Path.Combine(env.ContentRootPath, _opt.KnowledgeFolder);
        if (!Directory.Exists(dir)) dir = Path.Combine(AppContext.BaseDirectory, _opt.KnowledgeFolder);

        var sb = new StringBuilder();
        foreach (var file in Directory.GetFiles(dir, "*.md").OrderBy(f => f))
        {
            var text = File.ReadAllText(file);
            sb.AppendLine($"<document source=\"{Path.GetFileName(file)}\">\n{text.Trim()}\n</document>\n");
            _chunks.AddRange(SplitByHeading(Path.GetFileName(file), text));
        }
        FullText = sb.ToString();

        var nameMatch = Regex.Match(FullText, @"^#\s+(.+)$", RegexOptions.Multiline);
        ClinicName = nameMatch.Success ? nameMatch.Groups[1].Value.Trim() : "the clinic";

        _tf = _chunks.Select(c => TermFreq(c.Heading + " " + c.Text)).ToArray();
        foreach (var tf in _tf)
            foreach (var term in tf.Keys)
                _df[term] = _df.GetValueOrDefault(term) + 1;
        _avgLen = _tf.Length == 0 ? 1 : _tf.Average(t => t.Values.Sum());

        log.LogInformation("Knowledge base '{Name}': {Chunks} chunks, {Chars} chars, mode={Mode}",
            ClinicName, _chunks.Count, FullText.Length, UseFullContext ? "full-context" : "retrieval");
    }

    /// <summary>Context for the system prompt: everything (small KB) or the top-K chunks for this query.</summary>
    public string GetContext(string query)
    {
        if (UseFullContext) return FullText;
        var top = Retrieve(query, _opt.RetrievalTopK);
        return string.Join("\n\n", top.Select(c => $"<document source=\"{c.Source}\" section=\"{c.Heading}\">\n{c.Text}\n</document>"));
    }

    public IReadOnlyList<Chunk> Retrieve(string query, int k)
    {
        const double k1 = 1.5, b = 0.75;
        var q = Tokenize(query).Distinct().ToList();
        var n = _chunks.Count;
        return _chunks
            .Select((c, i) =>
            {
                var len = _tf[i].Values.Sum();
                double score = 0;
                foreach (var term in q)
                {
                    if (!_tf[i].TryGetValue(term, out var f)) continue;
                    var idf = Math.Log(1 + (n - _df[term] + 0.5) / (_df[term] + 0.5));
                    score += idf * f * (k1 + 1) / (f + k1 * (1 - b + b * len / _avgLen));
                }
                return (c, score);
            })
            .Where(x => x.score > 0)
            .OrderByDescending(x => x.score)
            .Take(k)
            .Select(x => x.c)
            .ToList();
    }

    private static IEnumerable<Chunk> SplitByHeading(string source, string text)
    {
        var parts = Regex.Split(text, @"(?=^##\s)", RegexOptions.Multiline);
        foreach (var p in parts.Where(p => !string.IsNullOrWhiteSpace(p)))
        {
            var first = p.Split('\n')[0].TrimStart('#', ' ').Trim();
            yield return new Chunk(source, first, p.Trim());
        }
    }

    private static Dictionary<string, int> TermFreq(string text) =>
        Tokenize(text).GroupBy(t => t).ToDictionary(g => g.Key, g => g.Count());

    /// <summary>Lower-cases with Turkish-safe folding (İ/ı/ş/ğ/ü/ö/ç → ascii) so "İmplant" matches "implant".</summary>
    private static IEnumerable<string> Tokenize(string text)
    {
        var lower = text.ToLower(new CultureInfo("tr-TR"));
        var folded = new StringBuilder(lower.Length);
        foreach (var ch in lower.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark) continue;
            folded.Append(ch == 'ı' ? 'i' : ch);
        }
        return Regex.Matches(folded.ToString(), @"[\p{L}\p{N}]{2,}").Select(m => m.Value);
    }
}
