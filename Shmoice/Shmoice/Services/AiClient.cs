using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Anthropic;
using Anthropic.Exceptions;
using Anthropic.Models.Beta;
using Anthropic.Models.Beta.Messages;

namespace Shmoice.Services;

/// <summary>
/// One question in, one answer out, against whichever AI service the user chose: Anthropic through
/// its SDK, everything else through the OpenAI-compatible chat API over plain HTTP.
/// </summary>
public static class AiClient
{
    /// <summary>Local models can take a while to load and answer, so this is generous; the caller's token cancels sooner.</summary>
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(5) };

    /// <summary>
    /// Sends a system prompt and a user message and returns the reply text.
    /// When <paramref name="jsonSchema"/> is given the service is asked to reply with JSON of that shape
    /// where it can; not every service or model supports that, so the prompt must also ask for JSON and
    /// the caller must still parse leniently (see <see cref="ExtractJson"/>).
    /// </summary>
    /// <param name="quick">A short, fast answer is wanted (spoken replies) rather than careful work.</param>
    public static Task<string> CompleteAsync(AiConnection ai, string system, string user,
        Dictionary<string, JsonElement>? jsonSchema = null, bool quick = false, CancellationToken ct = default) =>
        ai.Kind == AiApiKind.Anthropic
            ? AnthropicAsync(ai, system, user, jsonSchema, quick, ct)
            : OpenAiCompatibleAsync(ai, system, user, jsonSchema, ct);

    // ---- Anthropic --------------------------------------------------------------------

    private static async Task<string> AnthropicAsync(AiConnection ai, string system, string user,
        Dictionary<string, JsonElement>? jsonSchema, bool quick, CancellationToken ct)
    {
        var client = new AnthropicClient { ApiKey = ai.ApiKey };

        MessageCreateParams Request(bool full) => full
            ? new()
            {
                Model = ai.Model,
                MaxTokens = 16000,
                // If a safety classifier declines, the API retries on a suitable fallback model in the same call.
                Betas = [AnthropicBeta.ServerSideFallback2026_07_01],
                Fallbacks = new Default(),
                System = system,
                OutputConfig = new BetaOutputConfig
                {
                    Effort = quick ? Effort.Low : Effort.Medium,
                    Format = jsonSchema == null ? null : new BetaJsonOutputFormat { Schema = jsonSchema },
                },
                Messages = [new BetaUserMessageParam { Content = user }],
            }
            : new()
            {
                Model = ai.Model,
                MaxTokens = 16000,
                System = system,
                Messages = [new BetaUserMessageParam { Content = user }],
            };

        BetaMessage response;
        try
        {
            try
            {
                response = await client.Beta.Messages.Create(Request(full: true), ct);
            }
            catch (AnthropicBadRequestException)
            {
                // Older Claude models reject effort, fallbacks or structured output. Ask again plainly;
                // the prompt itself still says what shape of answer is wanted.
                response = await client.Beta.Messages.Create(Request(full: false), ct);
            }
        }
        catch (AnthropicUnauthorizedException)
        {
            throw new InvalidOperationException("The API key was rejected. Check it in Settings → AI.");
        }
        catch (AnthropicRateLimitException)
        {
            throw new InvalidOperationException("Rate limited by the API. Wait a moment and try again.");
        }
        catch (AnthropicBadRequestException ex)
        {
            throw new InvalidOperationException("Anthropic didn't accept the request: " + ex.Message);
        }

        if (response.StopReason == "refusal")
            throw new InvalidOperationException("The model declined this request. Try describing it differently.");
        if (response.StopReason == "max_tokens")
            throw new InvalidOperationException("The response was cut off. Try a simpler request.");

        return string.Concat(response.Content
            .Select(b => b.TryPickText(out var t) ? t.Text : null)
            .Where(t => t != null));
    }

    // ---- OpenAI-compatible ------------------------------------------------------------

    private static async Task<string> OpenAiCompatibleAsync(AiConnection ai, string system, string user,
        Dictionary<string, JsonElement>? jsonSchema, CancellationToken ct)
    {
        var body = new Dictionary<string, object>
        {
            ["model"] = ai.Model,
            ["messages"] = new object[]
            {
                new { role = "system", content = system },
                new { role = "user", content = user },
            },
        };
        if (jsonSchema != null)
            body["response_format"] = new
            {
                type = "json_schema",
                json_schema = new { name = "shmoice_command", strict = true, schema = jsonSchema },
            };

        var (status, text) = await PostAsync(ai, "/chat/completions", body, ct);
        if (jsonSchema != null && status is HttpStatusCode.BadRequest or HttpStatusCode.UnprocessableEntity or HttpStatusCode.NotImplemented)
        {
            // This service or model doesn't do structured output. Ask again without it; the prompt
            // still tells the model to answer in JSON.
            body.Remove("response_format");
            (status, text) = await PostAsync(ai, "/chat/completions", body, ct);
        }
        if ((int)status is < 200 or >= 300) throw new InvalidOperationException(DescribeError(ai, status, text));

        try
        {
            using var doc = JsonDocument.Parse(text);
            var choice = doc.RootElement.GetProperty("choices")[0];
            var content = choice.GetProperty("message").GetProperty("content");
            var reply = content.ValueKind switch
            {
                JsonValueKind.String => content.GetString() ?? "",
                // Some services return the reply as a list of parts.
                JsonValueKind.Array => string.Concat(content.EnumerateArray()
                    .Select(p => p.ValueKind == JsonValueKind.String ? p.GetString()
                        : p.TryGetProperty("text", out var t) ? t.GetString() : null)),
                _ => "",
            };
            reply = StripThinking(reply);
            if (reply.Length == 0)
            {
                var cutOff = choice.TryGetProperty("finish_reason", out var reason) && reason.GetString() == "length";
                throw new InvalidOperationException(cutOff
                    ? "The model ran out of room before answering. Try a simpler request or a different model."
                    : "The model sent back an empty answer. Try again or pick a different model.");
            }
            return reply;
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or IndexOutOfRangeException)
        {
            throw new InvalidOperationException($"{ai.ProviderName} answered in a way Shmoice doesn't understand. Is the address an OpenAI-compatible API?");
        }
    }

    /// <summary>The names of the models the service offers, for the picker in Settings. OpenAI-compatible services only.</summary>
    public static async Task<List<string>> ListModelsAsync(AiConnection ai, CancellationToken ct = default)
    {
        var (status, text) = await SendAsync(ai, HttpMethod.Get, "/models", null, ct);
        if ((int)status is < 200 or >= 300) throw new InvalidOperationException(DescribeError(ai, status, text));
        try
        {
            using var doc = JsonDocument.Parse(text);
            // OpenAI shape is {"data":[{"id":…}]}; a few services return a bare list or {"models":[…]}.
            var list = doc.RootElement.ValueKind == JsonValueKind.Array ? doc.RootElement
                : doc.RootElement.TryGetProperty("data", out var data) ? data
                : doc.RootElement.GetProperty("models");
            return list.EnumerateArray()
                .Select(m => m.ValueKind == JsonValueKind.String ? m.GetString()
                    : m.TryGetProperty("id", out var id) ? id.GetString()
                    : m.TryGetProperty("name", out var name) ? name.GetString() : null)
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Select(id => id!)
                .Distinct()
                .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            throw new InvalidOperationException($"{ai.ProviderName} didn't send a list of models. Type the model name instead.");
        }
    }

    private static Task<(HttpStatusCode, string)> PostAsync(AiConnection ai, string path, object body, CancellationToken ct) =>
        SendAsync(ai, HttpMethod.Post, path, JsonSerializer.Serialize(body), ct);

    private static async Task<(HttpStatusCode Status, string Text)> SendAsync(AiConnection ai, HttpMethod method, string path, string? json, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, ai.BaseUrl + path);
        if (ai.ApiKey.Length > 0) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ai.ApiKey);
        if (json != null) request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        var host = new Uri(ai.BaseUrl).Authority;
        try
        {
            using var response = await Http.SendAsync(request, ct);
            return (response.StatusCode, await response.Content.ReadAsStringAsync(ct));
        }
        catch (HttpRequestException ex)
        {
            throw new InvalidOperationException(ex.InnerException is System.Net.Sockets.SocketException
                ? $"Couldn't reach {host}. Is it running, and is the address right?"
                : $"Couldn't reach {host}: {ex.Message}");
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new InvalidOperationException($"{host} took more than 5 minutes to answer.");
        }
    }

    /// <summary>Turns an error reply into one readable line, using the service's own message when it sent one.</summary>
    private static string DescribeError(AiConnection ai, HttpStatusCode status, string text)
    {
        var detail = "";
        try
        {
            using var doc = JsonDocument.Parse(text);
            if (doc.RootElement.ValueKind == JsonValueKind.Array && doc.RootElement.GetArrayLength() > 0)
                detail = Message(doc.RootElement[0]);
            else detail = Message(doc.RootElement);

            static string Message(JsonElement root)
            {
                if (root.ValueKind != JsonValueKind.Object) return "";
                if (root.TryGetProperty("error", out var error))
                    return error.ValueKind == JsonValueKind.String ? error.GetString() ?? ""
                        : error.ValueKind == JsonValueKind.Object && error.TryGetProperty("message", out var m) ? m.GetString() ?? "" : "";
                return root.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.String ? message.GetString() ?? "" : "";
            }
        }
        catch (JsonException) { /* not JSON: fall back to the status alone */ }
        if (detail.Length > 300) detail = detail[..300] + "…";
        var suffix = detail.Length > 0 ? ": " + detail : ".";

        return status switch
        {
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => $"{ai.ProviderName} rejected the API key{suffix}",
            HttpStatusCode.NotFound => $"{ai.ProviderName} couldn't find that model or address{suffix}",
            HttpStatusCode.TooManyRequests => $"{ai.ProviderName} is limiting requests (or the account is out of credit){suffix}",
            _ => $"{ai.ProviderName} answered {(int)status} {status}{suffix}",
        };
    }

    // ---- Reading replies --------------------------------------------------------------

    /// <summary>Reasoning models from local runners often put their thinking in &lt;think&gt; tags before the answer.</summary>
    public static string StripThinking(string reply) =>
        Regex.Replace(reply, @"<think(ing)?>.*?</think(ing)?>", "", RegexOptions.Singleline | RegexOptions.IgnoreCase).Trim();

    /// <summary>
    /// Finds the JSON object in a reply that may wrap it in a ```json fence or a sentence of chat.
    /// Returns the text unchanged if it holds no braces.
    /// </summary>
    public static string ExtractJson(string reply)
    {
        var text = StripThinking(reply);
        int start = text.IndexOf('{'), end = text.LastIndexOf('}');
        return start >= 0 && end > start ? text[start..(end + 1)] : text;
    }
}
