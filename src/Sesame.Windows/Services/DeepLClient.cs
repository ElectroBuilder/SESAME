using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Sesame.Services;

/// <summary>
/// Optional DeepL v2 REST. Needs the user's own API key (free or pro).
/// </summary>
public static class DeepLClient
{
    private static readonly HttpClient Http = Create();

    private static HttpClient Create()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return http;
    }

    public static async Task<string?> TranslateAsync(string text, string? context, CancellationToken ct)
    {
        var map = await TranslateManyAsync([text], context, ct);
        return map.TryGetValue(text, out var hit) ? hit : null;
    }

    public static async Task<Dictionary<string, string>> TranslateManyAsync(
        IReadOnlyList<string> texts, string? context, CancellationToken ct)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!TranslateSettings.HasDeepL || texts.Count == 0) return map;

        var key = TranslateSettings.ApiKey;
        var urls = key.EndsWith(":fx", StringComparison.OrdinalIgnoreCase)
            ? new[] { "https://api-free.deepl.com/v2/translate", "https://api.deepl.com/v2/translate" }
            : new[] { "https://api.deepl.com/v2/translate", "https://api-free.deepl.com/v2/translate" };

        foreach (var url in urls)
        {
            var got = await PostAsync(url, key, texts, context, withFormality: true, ct);
            if (got.Count > 0) return got;
            got = await PostAsync(url, key, texts, context, withFormality: false, ct);
            if (got.Count > 0) return got;
        }
        return map;
    }

    private static async Task<Dictionary<string, string>> PostAsync(
        string url, string key, IReadOnlyList<string> texts, string? context, bool withFormality, CancellationToken ct)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        var payload = new Dictionary<string, object?>
        {
            ["text"] = texts.ToArray(),
            ["source_lang"] = "EN",
            ["target_lang"] = "NL",
            ["preserve_formatting"] = true
        };
        if (withFormality) payload["formality"] = "less";
        if (!string.IsNullOrWhiteSpace(context))
            payload["context"] = context.Length > 8000 ? context[..8000] : context;

        using var req = new HttpRequestMessage(HttpMethod.Post, url);
        req.Headers.TryAddWithoutValidation("Authorization", "DeepL-Auth-Key " + key);
        req.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        using var res = await Http.SendAsync(req, ct);
        if (!res.IsSuccessStatusCode) return map;
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
        if (!doc.RootElement.TryGetProperty("translations", out var list) ||
            list.ValueKind != JsonValueKind.Array)
            return map;

        var i = 0;
        foreach (var item in list.EnumerateArray())
        {
            if (i >= texts.Count) break;
            var translated = item.TryGetProperty("text", out var t) ? t.GetString() : null;
            if (!string.IsNullOrWhiteSpace(translated))
                map[texts[i]] = translated!;
            i++;
        }
        return map;
    }
}
