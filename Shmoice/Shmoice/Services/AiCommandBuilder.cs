using System.Text.Json;
using Anthropic;
using Anthropic.Exceptions;
using Anthropic.Models.Beta;
using Anthropic.Models.Beta.Messages;
using Shmoice.Models;

namespace Shmoice.Services;

/// <summary>What the AI produced: a suggested phrase, the steps, and a short explanation for the user.</summary>
public record AiDraft(string Phrase, string Explanation, List<ActionStep> Steps);

/// <summary>
/// Turns a plain-English description ("open Spotify and skip to the next song") into ActionSteps
/// using Claude. Structured outputs force the reply to match our step schema, so the result can be
/// loaded straight into the editor. Nothing runs automatically — the user reviews and tests first.
/// </summary>
public static class AiCommandBuilder
{
    private const string Model = "claude-opus-5-5";

    private static readonly string[] ActionTypeNames = ActionCatalog.All.Select(a => a.Type.ToString()).ToArray();

    /// <summary>The action list for the prompt, built from the same catalog the app's picker uses.</summary>
    private static string DescribeActions() => string.Join("\n", ActionCatalog.All
        .GroupBy(a => a.Category)
        .Select(g => $"{g.Key}:\n" + string.Join("\n", g.Select(a =>
            $"- {a.Type}: {a.Description}" + a.Param switch
            {
                ParamKind.None => " Parameter is \"\".",
                ParamKind.Keys => " Parameter: keys joined by \"+\", e.g. \"ctrl+shift+esc\".",
                _ => $" Parameter: {a.Hint}.",
            }))));

    private static readonly string SystemPrompt = $$"""
        You turn a user's description of something they want their Windows PC to do into a short
        sequence of automation steps for "Shmoice", a voice-command app. The user will review and test
        your steps before saving, then trigger them by voice.

        Available step types (field "type"), grouped by category, and what "parameter" means for each:
        {{DescribeActions()}}

        Notes on specific types:
        - Prefer MediaPlay / MediaPause / Mute / Unmute / MicMute / MicUnmute over the toggles when the user says which way.
        - Prefer a dedicated type (Copy, NewTab, SnapLeft, OpenSettings…) over KeyCombo when one exists.
        - ListenForWords lets a command take spoken input: it waits for the user to say something after the command, and any later step's parameter can include {heard}, which is replaced by those words (URL-encoded when inside an OpenUrl link). E.g. "search for" = ListenForWords, then SearchWeb with parameter "{heard}". Its own parameter is optional seconds to wait (usually "").
        - Use OpenUrl for websites (parameter is the link, e.g. "youtube.com"); it opens in the default browser, so no Open/Wait/NewTab steps are needed first.
        - KeyCombo keys are joined by "+", e.g. "ctrl+shift+esc", "alt+tab", "f5". Modifier names: ctrl, alt, shift, win. Other keys use .NET Key names (A-Z, 0-9, F1-F24, Enter, Escape, Tab, Space, Delete, Back, Home, End, PageUp, PageDown, Left, Right, Up, Down, OemPlus, OemMinus).
        - Open: optional command-line arguments go after a "|", e.g. "chrome.exe|--incognito https://example.com". Prefer URI schemes and well-known executables (e.g. "spotify:", "ms-settings:display", "calc.exe", "https://...") over guessed install paths. Add a Wait after Open when later steps need the opened window to be ready.
        - SwitchToApp / CloseApp / ForceQuitApp only work on apps that are already running; use Open to start apps.
        - Placeholders usable inside any later step's parameter: {heard} (ListenForWords), {result} (output of the last HttpRequest, RunPowerShell, AskAi or PickJson step), {clipboard} (clipboard text), {date} (yyyy-MM-dd), {time} (HH:mm, 24-hour), {app} (process name of the app in front, e.g. "chrome").
        - HttpRequest parameter is "METHOD url | Header: value | body"; only the url is required, there can be several headers, and everything after the headers is the body. Method defaults to GET, or POST when there's a body. A body starting with { or [ is sent as JSON, and placeholders inside it are JSON-escaped, so write e.g. POST https://example.com/hook | {"text": "{heard}"}. Never invent URLs, tokens or API keys: if the user didn't give them, use an obvious stand-in like YOUR_TOKEN and say so in the explanation.
        - PickJson narrows a JSON {result} to one value by path, e.g. "main.temp" or "items[0].name". Typical chain: HttpRequest, PickJson, then Speak "It's {result} degrees".
        - RunPowerShell waits for the script and keeps its output as {result}; RunCommand doesn't wait and keeps nothing. Use RunPowerShell only when no dedicated type does the job.
        - AskAi sends its parameter to Claude as a question and keeps a short spoken-style answer as {result}. E.g. "ask claude" = ListenForWords, AskAi "{heard}", Speak "{result}".
        - ContinueIf stops the command unless its condition is true. Form: "left operator right" with operator one of: is, is not, contains, does not contain, starts with, ends with, >, <, >=, <=; or "left is empty" / "left is not empty". E.g. "{app} is chrome", "{time} >= 18:00".
        - RunVoiceCommand runs another existing command by its exact phrase; only use it when the user names one.
        - AppendToFile parameter is "file | text", e.g. "%USERPROFILE%\Documents\notes.txt | {date} {time} {heard}".
        - PasteText is better than TypeText for long or multi-line text such as {result} from AskAi. HoldKeys keeps keys down until a ReleaseKeys step (possibly in another command), so only use it when the user wants a key held.
        - SetVolume parameter is a whole percent as a string, e.g. "30". Wait parameter is seconds as a string, e.g. "1.5". MoveMouseTo parameter is "x, y" in screen pixels.

        "repeat" is how many times to run the step (1-100); use 1 unless repetition is the point (e.g. VolumeUp repeat 10 = +20%, ScrollDown repeat 5).

        Guidelines:
        - Use the fewest steps that reliably do the job.
        - "phrase" is a short, natural voice trigger (2-4 words, lowercase, no punctuation) — easy to say and unlikely to be said by accident.
        - "explanation" is 1-3 plain sentences telling the user what the command does and any assumptions (e.g. which app must be focused, or that a program must be installed).
        - If the request can't be done with these step types, return an empty "steps" array and say why in "explanation".
        - If a step could lose data or disrupt the user (closing apps, shutting down, deleting), still produce it if asked, but call that out clearly in the explanation.
        """;

    private static readonly Dictionary<string, JsonElement> Schema = new()
    {
        ["type"] = JsonSerializer.SerializeToElement("object"),
        ["additionalProperties"] = JsonSerializer.SerializeToElement(false),
        ["required"] = JsonSerializer.SerializeToElement(new[] { "phrase", "explanation", "steps" }),
        ["properties"] = JsonSerializer.SerializeToElement(new Dictionary<string, object>
        {
            ["phrase"] = new { type = "string" },
            ["explanation"] = new { type = "string" },
            ["steps"] = new
            {
                type = "array",
                items = new Dictionary<string, object>
                {
                    ["type"] = "object",
                    ["additionalProperties"] = false,
                    ["required"] = new[] { "type", "parameter", "repeat" },
                    ["properties"] = new Dictionary<string, object>
                    {
                        ["type"] = new { type = "string", @enum = ActionTypeNames },
                        ["parameter"] = new { type = "string" },
                        ["repeat"] = new { type = "integer" },
                    },
                },
            },
        }),
    };

    public static async Task<AiDraft> GenerateAsync(string apiKey, string description, CancellationToken ct = default) =>
        Parse(await SendAsync(apiKey, SystemPrompt, description, new BetaOutputConfig
        {
            Effort = Effort.Medium,
            Format = new BetaJsonOutputFormat { Schema = Schema },
        }, ct));

    private const string AnswerPrompt = """
        You answer questions for "Shmoice", a voice-command app on the user's Windows PC. Your answer is
        used directly by the next step of a voice command: usually read aloud by text-to-speech, sometimes
        typed or pasted into a window. Reply with the answer only, in plain text: no markdown, no lists,
        no preamble, no follow-up questions. Keep it to one or two short sentences unless the request
        clearly asks for longer text (e.g. "write an email saying…" or "summarize this").
        """;

    /// <summary>For "Ask Claude" steps: a short plain-text answer to use as {result}.</summary>
    public static async Task<string> AskAsync(string apiKey, string question, CancellationToken ct = default) =>
        (await SendAsync(apiKey, AnswerPrompt, question, new BetaOutputConfig { Effort = Effort.Low }, ct)).Trim();

    /// <summary>One request to Claude; returns the text of the reply.</summary>
    private static async Task<string> SendAsync(string apiKey, string system, string user, BetaOutputConfig output, CancellationToken ct)
    {
        var client = new AnthropicClient { ApiKey = apiKey };

        BetaMessage response;
        try
        {
            response = await client.Beta.Messages.Create(new MessageCreateParams
            {
                Model = Model,
                MaxTokens = 16000,
                // If a safety classifier declines, the API retries on a suitable fallback model in the same call.
                Betas = [AnthropicBeta.ServerSideFallback2026_07_01],
                Fallbacks = new Default(),
                System = system,
                OutputConfig = output,
                Messages = [new BetaUserMessageParam { Content = user }],
            }, ct);
        }
        catch (AnthropicUnauthorizedException)
        {
            throw new InvalidOperationException("The API key was rejected. Check it in the AI Builder tab.");
        }
        catch (AnthropicRateLimitException)
        {
            throw new InvalidOperationException("Rate limited by the API — wait a moment and try again.");
        }

        if (response.StopReason == "refusal")
            throw new InvalidOperationException("The model declined this request. Try describing it differently.");
        if (response.StopReason == "max_tokens")
            throw new InvalidOperationException("The response was cut off. Try a simpler request.");

        return string.Concat(response.Content
            .Select(b => b.TryPickText(out var t) ? t.Text : null)
            .Where(t => t != null));
    }

    private static AiDraft Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var steps = new List<ActionStep>();
        foreach (var s in root.GetProperty("steps").EnumerateArray())
        {
            // Schema guarantees the enum, but skip anything unknown rather than crash.
            if (!Enum.TryParse<ActionType>(s.GetProperty("type").GetString(), out var type) || type == ActionType.None) continue;
            steps.Add(new ActionStep
            {
                Type = type,
                Parameter = s.GetProperty("parameter").GetString() ?? "",
                Repeat = s.GetProperty("repeat").GetInt32(),
            });
        }
        return new AiDraft(
            root.GetProperty("phrase").GetString()?.Trim() ?? "",
            root.GetProperty("explanation").GetString()?.Trim() ?? "",
            steps);
    }
}
