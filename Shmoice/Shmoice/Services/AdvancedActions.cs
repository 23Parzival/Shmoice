using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Shmoice.Services;

/// <summary>
/// The "Advanced" actions used by <see cref="ActionExecutor"/>: web requests, PowerShell, JSON picking,
/// conditions and file logging. Most of them produce the text that later steps see as {result}.
/// </summary>
public static class AdvancedActions
{
    /// <summary>Fills in {heard}, {result}… in a piece of a parameter; the second argument encodes each value (null = as is).</summary>
    public delegate string Filler(string template, Func<string, string>? encode = null);

    private const int MaxResultLength = 100_000;

    // ---- Web requests -----------------------------------------------------------------

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

    private static readonly string[] Methods = ["GET", "POST", "PUT", "PATCH", "DELETE", "HEAD"];

    /// <summary>"Name: value", but not a link like "http://…" or JSON.</summary>
    private static readonly Regex HeaderPattern = new(@"^([A-Za-z][A-Za-z0-9\-]*):\s+(\S.*)$", RegexOptions.Singleline);

    public record WebRequest(string Method, string Url, List<(string Name, string Value)> Headers, string? Body);

    /// <summary>
    /// Reads "METHOD link | Header: value | body". The method, headers and body are optional; everything
    /// after the headers is the body (so it may contain "|"). Placeholders are URL-encoded in the link
    /// and JSON-escaped in a JSON body, so spoken words can't break either.
    /// </summary>
    public static WebRequest ParseRequest(string parameter, Filler fill)
    {
        var rest = parameter.Trim();
        string Next()
        {
            var bar = rest.IndexOf('|');
            var part = bar < 0 ? rest : rest[..bar];
            rest = bar < 0 ? "" : rest[(bar + 1)..];
            return part.Trim();
        }

        var first = Next();
        string? method = null;
        var space = first.IndexOf(' ');
        if (space > 0 && Methods.Contains(first[..space].ToUpperInvariant()))
        {
            method = first[..space].ToUpperInvariant();
            first = first[(space + 1)..].Trim();
        }
        if (first.Length == 0) throw new ArgumentException("Enter a link to send the request to, e.g. https://example.com/hook.");
        // A link that is nothing but a placeholder (e.g. {result}) is a whole link already, so leave it as is.
        var wholeLink = Regex.IsMatch(first, @"^\{\w+\}$");
        var url = ActionExecutor.NormalizeUrl(fill(first, wholeLink ? null : Uri.EscapeDataString));

        var headers = new List<(string, string)>();
        while (rest.Trim().Length > 0)
        {
            var bar = rest.IndexOf('|');
            var peek = (bar < 0 ? rest : rest[..bar]).Trim();
            if (HeaderPattern.Match(peek) is not { Success: true } m) break;
            headers.Add((m.Groups[1].Value, fill(m.Groups[2].Value.Trim())));
            Next();
        }

        string? body = null;
        if (rest.Trim().Length > 0)
        {
            var template = rest.Trim();
            body = LooksLikeJson(template) ? fill(template, JsonEscape) : fill(template);
        }
        return new WebRequest(method ?? (body == null ? "GET" : "POST"), url, headers, body);
    }

    /// <summary>Sends the request and returns the reply text. Throws if the server answers with an error status.</summary>
    public static async Task<string> SendAsync(WebRequest request)
    {
        using var message = new HttpRequestMessage(new HttpMethod(request.Method), request.Url);
        var contentType = request.Body != null && LooksLikeJson(request.Body) ? "application/json" : "text/plain";
        foreach (var (name, value) in request.Headers)
        {
            if (name.Equals("Content-Type", StringComparison.OrdinalIgnoreCase)) contentType = value;
            else if (!message.Headers.TryAddWithoutValidation(name, value))
                throw new ArgumentException($"\"{name}\" can't be used as a request header.");
        }
        if (request.Body != null)
        {
            message.Content = new StringContent(request.Body, Encoding.UTF8);
            message.Content.Headers.Remove("Content-Type");
            message.Content.Headers.TryAddWithoutValidation("Content-Type", contentType);
        }

        var host = new Uri(request.Url).Host;
        try
        {
            using var response = await Http.SendAsync(message);
            var text = Limit((await response.Content.ReadAsStringAsync()).Trim());
            if (!response.IsSuccessStatusCode)
            {
                var detail = text.Length > 0 ? ": " + (text.Length > 200 ? text[..200] + "…" : text) : "";
                throw new InvalidOperationException($"{host} answered {(int)response.StatusCode} {response.ReasonPhrase}{detail}");
            }
            return text;
        }
        catch (TaskCanceledException)
        {
            throw new InvalidOperationException($"{host} didn't answer within 30 seconds.");
        }
        catch (HttpRequestException ex)
        {
            throw new InvalidOperationException($"Couldn't reach {host}: {ex.Message}");
        }
    }

    private static bool LooksLikeJson(string text) => text.StartsWith('{') || text.StartsWith('[');

    /// <summary>Escapes text for use inside a JSON string (without the surrounding quotes).</summary>
    private static string JsonEscape(string value) => JsonSerializer.Serialize(value)[1..^1];

    // ---- JSON -------------------------------------------------------------------------

    /// <summary>Follows a path like "main.temp" or "items[0].name" into JSON and returns that value as text.</summary>
    public static string PickJson(string json, string path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("Enter which value to pick, e.g. main.temp.");
        if (json.Trim().Length == 0) throw new InvalidOperationException("There's no result to pick from yet. Put this after a web request or script step.");
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (JsonException) { throw new InvalidOperationException("The result isn't JSON, so there's nothing to pick from."); }

        using (doc)
        {
            var current = doc.RootElement;
            foreach (Match token in Regex.Matches(path.Trim(), @"\[(\d+)\]|[^.\[\]]+"))
            {
                if (token.Groups[1].Success)
                {
                    var index = int.Parse(token.Groups[1].Value);
                    if (current.ValueKind != JsonValueKind.Array || index >= current.GetArrayLength())
                        throw new InvalidOperationException($"The result has no item {token.Value} (looking for \"{path.Trim()}\").");
                    current = current[index];
                }
                else
                {
                    var name = token.Value.Trim();
                    if (current.ValueKind != JsonValueKind.Object || !TryGetProperty(current, name, out current))
                        throw new InvalidOperationException($"The result has no \"{name}\" (looking for \"{path.Trim()}\").");
                }
            }
            return current.ValueKind switch
            {
                JsonValueKind.String => current.GetString() ?? "",
                JsonValueKind.Null => "",
                _ => current.GetRawText(),
            };
        }
    }

    /// <summary>Exact name first, then ignoring case.</summary>
    private static bool TryGetProperty(JsonElement obj, string name, out JsonElement value)
    {
        if (obj.TryGetProperty(name, out value)) return true;
        foreach (var property in obj.EnumerateObject())
        {
            if (!property.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) continue;
            value = property.Value;
            return true;
        }
        return false;
    }

    // ---- PowerShell -------------------------------------------------------------------

    /// <summary>Runs a PowerShell script with no window, waits (up to 60 s) and returns what it printed.</summary>
    public static async Task<string> RunPowerShellAsync(string script)
    {
        if (string.IsNullOrWhiteSpace(script)) throw new ArgumentException("Enter a PowerShell script to run.");
        // Encoded so quotes and special characters in the script survive the command line.
        // UTF-8 output so accented letters and other languages come through intact.
        const string setup = "$ProgressPreference = 'SilentlyContinue'; [Console]::OutputEncoding = [Text.Encoding]::UTF8; ";
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(setup + script));
        using var process = Process.Start(new ProcessStartInfo("powershell.exe",
            $"-NoProfile -NonInteractive -OutputFormat Text -EncodedCommand {encoded}")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        }) ?? throw new InvalidOperationException("Couldn't start PowerShell.");
        process.StandardInput.Close(); // nothing to read, so a script that asks for input fails instead of hanging

        var output = process.StandardOutput.ReadToEndAsync();
        var errors = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* already gone */ }
            throw new InvalidOperationException("The script was stopped because it ran for more than 60 seconds.");
        }

        var error = ReadableErrors((await errors).Trim());
        if (process.ExitCode != 0 || error.Length > 0)
        {
            var firstLine = error.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
            throw new InvalidOperationException("The script failed: " + (firstLine ?? $"exit code {process.ExitCode}"));
        }
        return Limit((await output).Trim());
    }

    /// <summary>
    /// PowerShell writes errors to a redirected stream as XML ("#&lt; CLIXML …"); pull the message text out of it.
    /// </summary>
    private static string ReadableErrors(string stderr)
    {
        if (!stderr.StartsWith("#< CLIXML")) return stderr;
        var parts = Regex.Matches(stderr, "<S S=\"Error\">(.*?)</S>", RegexOptions.Singleline).Select(m => m.Groups[1].Value);
        return System.Net.WebUtility.HtmlDecode(string.Concat(parts).Replace("_x000D_", "").Replace("_x000A_", "\n")).Trim();
    }

    // ---- Files ------------------------------------------------------------------------

    /// <summary>"file | text": adds the text as a new line at the end of the file.</summary>
    public static void AppendToFile(string parameter)
    {
        var bar = parameter.IndexOf('|');
        if (bar < 0) throw new ArgumentException("Write the file, a |, then the text, e.g. C:\\notes.txt | {heard}.");
        var path = Environment.ExpandEnvironmentVariables(parameter[..bar].Trim().Trim('"'));
        if (path.Length == 0) throw new ArgumentException("Enter which file to add the line to.");
        if (Path.GetDirectoryName(Path.GetFullPath(path)) is { Length: > 0 } folder) Directory.CreateDirectory(folder);
        File.AppendAllText(path, parameter[(bar + 1)..].Trim() + Environment.NewLine);
    }

    // ---- Conditions -------------------------------------------------------------------

    // Longer operators first, so " is not " wins over " is " when both start at the same place.
    private static readonly string[] Operators =
    [
        " does not contain ", " doesn't contain ", " starts with ", " ends with ", " contains ",
        " is not ", " is ", " >= ", " <= ", " > ", " < ",
    ];

    /// <summary>
    /// Evaluates e.g. "{app} is chrome" or "{result} contains rain". The operator is found in the text
    /// as written, before placeholders are filled in, so what was heard can't change its meaning.
    /// </summary>
    public static bool Evaluate(string condition, Filler fill)
    {
        var text = condition.Trim();
        if (text.Length == 0) throw new ArgumentException("Enter a condition, e.g. {app} is chrome.");

        if (text.EndsWith(" is not empty", StringComparison.OrdinalIgnoreCase)) return fill(text[..^13]).Trim().Length > 0;
        if (text.EndsWith(" is empty", StringComparison.OrdinalIgnoreCase)) return fill(text[..^9]).Trim().Length == 0;

        var padded = text + " ";
        var (at, op) = Operators
            .Select(o => (At: padded.IndexOf(o, StringComparison.OrdinalIgnoreCase), Op: o))
            .Where(x => x.At > 0)
            .OrderBy(x => x.At).ThenByDescending(x => x.Op.Length)
            .FirstOrDefault();
        if (op == null)
            throw new ArgumentException($"\"{text}\" isn't a condition. Use something like: {{app}} is chrome, or {{result}} contains rain.");

        var left = fill(padded[..at]).Trim();
        var right = fill(padded[(at + op.Length)..]).Trim();
        const StringComparison ignoreCase = StringComparison.OrdinalIgnoreCase;
        return op.Trim().ToLowerInvariant() switch
        {
            "is" => left.Equals(right, ignoreCase),
            "is not" => !left.Equals(right, ignoreCase),
            "contains" => left.Contains(right, ignoreCase),
            "does not contain" or "doesn't contain" => !left.Contains(right, ignoreCase),
            "starts with" => left.StartsWith(right, ignoreCase),
            "ends with" => left.EndsWith(right, ignoreCase),
            ">" => Compare(left, right) > 0,
            "<" => Compare(left, right) < 0,
            ">=" => Compare(left, right) >= 0,
            _ => Compare(left, right) <= 0,
        };
    }

    /// <summary>As numbers when both are numbers ("9" &lt; "10"); otherwise as text, which also orders times like 18:00.</summary>
    private static int Compare(string left, string right)
    {
        static bool Number(string s, out double n) => double.TryParse(s.Replace(',', '.'),
            System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out n);
        return Number(left, out var a) && Number(right, out var b)
            ? a.CompareTo(b)
            : string.Compare(left, right, StringComparison.OrdinalIgnoreCase);
    }

    private static string Limit(string text) => text.Length > MaxResultLength ? text[..MaxResultLength] : text;
}
