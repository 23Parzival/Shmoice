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

    private static readonly string[] ActionTypeNames = Enum.GetNames<ActionType>();

    private static readonly string SystemPrompt = $"""
        You turn a user's description of something they want their Windows PC to do into a short
        sequence of automation steps for "Shmoice", a voice-command app. The user will review and test
        your steps before saving, then trigger them by voice.

        Available step types (field "type") and what "parameter" means for each:
        - MediaPlay, MediaPause: start / pause whatever media is playing (never toggles); parameter is "". Prefer these over MediaPlayPause.
        - MediaPlayPause, MediaNext, MediaPrevious, MediaStop: media keys (MediaPlayPause toggles); parameter is "".
        - VolumeUp, VolumeDown: one press changes system volume by 2%; parameter is "". Use "repeat" for bigger changes (e.g. repeat 10 = 20%).
        - VolumeMute: toggles mute; parameter is "".
        - KeyCombo: a keyboard shortcut sent to the focused window. Keys joined by "+", e.g. "ctrl+shift+esc", "alt+tab", "win+d", "ctrl+t", "f5". Modifier names: ctrl, alt, shift, win. Other keys use .NET Key names (A-Z, 0-9, F1-F24, Enter, Escape, Tab, Space, Delete, Back, Home, End, PageUp, PageDown, Left, Right, Up, Down).
        - TypeText: types the given text into the focused window.
        - Open: opens a program, file, folder, or URL via the Windows shell. Optional command-line arguments go after a "|", e.g. "notepad.exe" or "chrome.exe|--incognito https://example.com". Prefer URI schemes and well-known executables (e.g. "spotify:", "ms-settings:display", "calc.exe", "https://...") over guessed install paths.
        - Wait: pauses; parameter is the number of seconds as a string, decimals allowed, e.g. "1.5". Add a Wait after Open when later steps need the opened window to be ready.

        "repeat" is how many times to run the step (1-100); use 1 unless repetition is the point.

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

    public static async Task<AiDraft> GenerateAsync(string apiKey, string description, CancellationToken ct = default)
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
                System = SystemPrompt,
                OutputConfig = new BetaOutputConfig
                {
                    Effort = Effort.Medium,
                    Format = new BetaJsonOutputFormat { Schema = Schema },
                },
                Messages = [new BetaUserMessageParam { Content = description }],
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
            throw new InvalidOperationException("The response was cut off. Try a simpler description.");

        var json = string.Concat(response.Content
            .Select(b => b.TryPickText(out var t) ? t.Text : null)
            .Where(t => t != null));

        return Parse(json);
    }

    private static AiDraft Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var steps = new List<ActionStep>();
        foreach (var s in root.GetProperty("steps").EnumerateArray())
        {
            // Schema guarantees the enum, but skip anything unknown rather than crash.
            if (!Enum.TryParse<ActionType>(s.GetProperty("type").GetString(), out var type)) continue;
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
