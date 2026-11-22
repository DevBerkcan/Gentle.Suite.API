using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;

namespace GentleSuite.Infrastructure.Ai;

/// <summary>One completion: a system and a user message in, a JSON object out (OpenAI JSON mode).</summary>
public record AiJsonResult(string Json, string Model, int PromptTokens, int CompletionTokens, bool Truncated);

public class AiNotConfiguredException(string message) : Exception(message);
public class AiProviderException(string message, bool mayHaveIncurredCost) : Exception(message)
{
    public bool MayHaveIncurredCost { get; } = mayHaveIncurredCost;
}

public interface IAiJsonClient
{
    /// <summary>False when no provider is configured (Ai:Provider missing / no key) — the UI hides the feature.</summary>
    bool IsEnabled { get; }
    string ModelName { get; }
    Task<AiJsonResult> CompleteJsonAsync(string systemPrompt, string userContent, int maxOutputTokens, CancellationToken ct);
}

/// <summary>
/// OpenAI Chat Completions over a plain HttpClient, same setup as GentleHub's OpenAiProvider:
/// JSON mode, temperature 0.2, store:false (OpenAI keeps no copy for training/dashboards).
/// Config: Ai:Provider = "openai", Ai:ApiKey, Ai:Model (default gpt-4.1-mini), Ai:TimeoutSeconds (default 60).
/// Prompts and answers are never logged here.
/// </summary>
public class OpenAiJsonClient(IHttpClientFactory httpFactory, IConfiguration cfg) : IAiJsonClient
{
    private string? ApiKey => cfg["Ai:ApiKey"];
    public bool IsEnabled => !string.IsNullOrWhiteSpace(ApiKey);
    public string ModelName => string.IsNullOrWhiteSpace(cfg["Ai:Model"]) ? "gpt-4.1-mini" : cfg["Ai:Model"]!;

    public async Task<AiJsonResult> CompleteJsonAsync(string systemPrompt, string userContent, int maxOutputTokens, CancellationToken ct)
    {
        if (!IsEnabled) throw new AiNotConfiguredException("Die KI ist nicht eingerichtet (Ai:ApiKey fehlt).");
        var timeout = Math.Clamp(int.TryParse(cfg["Ai:TimeoutSeconds"], out var t) ? t : 60, 10, 180);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(timeout));

        var body = JsonSerializer.Serialize(new
        {
            model = ModelName,
            temperature = 0.2,
            response_format = new { type = "json_object" },
            max_completion_tokens = maxOutputTokens,
            store = false,
            messages = new object[]
            {
                new { role = "system", content = systemPrompt },
                new { role = "user", content = userContent },
            },
        });
        using var req = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/chat/completions")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ApiKey);

        HttpResponseMessage resp;
        try
        {
            resp = await httpFactory.CreateClient("openai").SendAsync(req, cts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new AiProviderException($"Die KI hat nicht innerhalb von {timeout} Sekunden geantwortet.", mayHaveIncurredCost: true);
        }
        catch (HttpRequestException e)
        {
            throw new AiProviderException("Die KI ist nicht erreichbar: " + Trim(e.Message), mayHaveIncurredCost: false);
        }

        using (resp)
        {
            var text = await resp.Content.ReadAsStringAsync(cts.Token);
            if (!resp.IsSuccessStatusCode)
            {
                var hint = resp.StatusCode switch
                {
                    HttpStatusCode.Unauthorized => "Der OpenAI-Schlüssel ist ungültig.",
                    HttpStatusCode.TooManyRequests => "OpenAI-Limit erreicht oder Guthaben aufgebraucht – bitte später erneut versuchen.",
                    _ => $"OpenAI-Fehler {(int)resp.StatusCode}.",
                };
                throw new AiProviderException(hint, mayHaveIncurredCost: (int)resp.StatusCode >= 500);
            }
            try
            {
                using var doc = JsonDocument.Parse(text);
                var root = doc.RootElement;
                var choice = root.GetProperty("choices")[0];
                var content = choice.GetProperty("message").GetProperty("content").GetString() ?? "";
                var finish = choice.TryGetProperty("finish_reason", out var fr) ? fr.GetString() : null;
                int pt = 0, ctk = 0;
                if (root.TryGetProperty("usage", out var u))
                {
                    pt = u.TryGetProperty("prompt_tokens", out var a) ? a.GetInt32() : 0;
                    ctk = u.TryGetProperty("completion_tokens", out var b) ? b.GetInt32() : 0;
                }
                var model = root.TryGetProperty("model", out var m) ? m.GetString() ?? ModelName : ModelName;
                return new AiJsonResult(content, model, pt, ctk, finish == "length");
            }
            catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException or IndexOutOfRangeException)
            {
                throw new AiProviderException("Die Antwort der KI war nicht lesbar.", mayHaveIncurredCost: true);
            }
        }
    }

    private static string Trim(string s) => s.Length > 300 ? s[..300] : s;
}

/// <summary>Offline provider for local tests (Ai:Provider = "dryrun"): no network, no cost. The caller supplies the canned answer.</summary>
public class DryRunAiJsonClient(Func<string, string, string> answer) : IAiJsonClient
{
    public bool IsEnabled => true;
    public string ModelName => "dryrun";
    public Task<AiJsonResult> CompleteJsonAsync(string systemPrompt, string userContent, int maxOutputTokens, CancellationToken ct)
        => Task.FromResult(new AiJsonResult(answer(systemPrompt, userContent), "dryrun", userContent.Length / 4, 200, false));
}
