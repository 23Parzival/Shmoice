using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;

namespace Shmoice.Services;

/// <summary>A newer version published on GitHub.</summary>
/// <param name="InstallerUrl">Download link of the setup exe, or null if the release has none attached.</param>
public record UpdateInfo(Version Version, string Notes, string PageUrl, string? InstallerUrl, string? InstallerName, long InstallerSize);

/// <summary>
/// Updates from GitHub Releases. Each release is tagged "v1.2.3" and has the installer built by
/// publish.ps1 (Shmoice-Setup-1.2.3.exe) attached. Updating downloads that installer, runs it
/// silently over the current install, and it starts the new version when done.
/// </summary>
public static class UpdateChecker
{
    /// <summary>The GitHub repository whose releases are the updates.</summary>
    public const string Repository = "23Parzival/Shmoice";

    public static string ReleasesPage => $"https://github.com/{Repository}/releases/latest";

    private const string InstallerPrefix = "Shmoice-Setup-";

    // Declared before Http: static fields are set in order, and the client's User-Agent uses the version.
    public static Version CurrentVersion { get; } = Normalize(Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(0, 0, 0));

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        // GitHub's API refuses requests without a User-Agent.
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"Shmoice/{CurrentVersion.ToString(3)}");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }

    /// <summary>
    /// True when this exe was put there by the installer (its uninstaller sits next to it). A copy run
    /// from anywhere else can't be updated in place, so it's sent to the download page instead.
    /// </summary>
    public static bool IsInstalled => File.Exists(Path.Combine(AppContext.BaseDirectory, "unins000.exe"));

    /// <summary>The latest release if it's newer than this version, otherwise null. Throws if GitHub can't be reached.</summary>
    public static async Task<UpdateInfo?> CheckAsync(CancellationToken ct = default)
    {
        using var response = await Http.GetAsync($"https://api.github.com/repos/{Repository}/releases/latest", ct);
        if (response.StatusCode == HttpStatusCode.NotFound) return null; // nothing released yet
        if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
            throw new InvalidOperationException("GitHub is limiting requests right now. Try again in a while.");
        response.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        return Parse(doc.RootElement, CurrentVersion);
    }

    /// <summary>Reads GitHub's "latest release" JSON. Null if it isn't newer than <paramref name="current"/>.</summary>
    public static UpdateInfo? Parse(JsonElement release, Version current)
    {
        if (ParseVersion(release.GetProperty("tag_name").GetString()) is not { } version || version <= current) return null;

        string? url = null, name = null;
        long size = 0;
        if (release.TryGetProperty("assets", out var assets))
            foreach (var asset in assets.EnumerateArray())
            {
                var assetName = asset.GetProperty("name").GetString() ?? "";
                var assetUrl = asset.GetProperty("browser_download_url").GetString() ?? "";
                if (!IsInstallerName(assetName) || !IsGitHubDownload(assetUrl)) continue;
                (url, name, size) = (assetUrl, assetName, asset.TryGetProperty("size", out var s) ? s.GetInt64() : 0);
                break;
            }

        return new UpdateInfo(version,
            release.TryGetProperty("body", out var body) ? body.GetString()?.Trim() ?? "" : "",
            release.TryGetProperty("html_url", out var page) && IsGitHubPage(page.GetString()) ? page.GetString()! : ReleasesPage,
            url, name, size);
    }

    /// <summary>"v1.2.3", "1.2" or "v1.2.3-beta" -> 1.2.3. Null if it isn't a version.</summary>
    public static Version? ParseVersion(string? tag)
    {
        var text = (tag ?? "").Trim().TrimStart('v', 'V');
        var dash = text.IndexOfAny(['-', '+', ' ']);
        if (dash >= 0) text = text[..dash];
        if (!text.Contains('.')) text += ".0";
        return Version.TryParse(text, out var version) ? Normalize(version) : null;
    }

    /// <summary>Always three parts, so 1.2 and 1.2.0.0 compare as equal to 1.2.0.</summary>
    private static Version Normalize(Version v) => new(v.Major, v.Minor, Math.Max(v.Build, 0));

    // Only ever run a file that is plainly our installer, served by GitHub over HTTPS for this repository.
    private static bool IsInstallerName(string name) =>
        name.StartsWith(InstallerPrefix, StringComparison.OrdinalIgnoreCase) &&
        name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) &&
        name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;

    private static bool IsGitHubDownload(string url) =>
        url.StartsWith($"https://github.com/{Repository}/releases/download/", StringComparison.OrdinalIgnoreCase);

    private static bool IsGitHubPage(string? url) =>
        url != null && url.StartsWith($"https://github.com/{Repository}/", StringComparison.OrdinalIgnoreCase);

    /// <summary>Downloads the installer to the temp folder and returns its path. Reports progress as 0..1.</summary>
    public static async Task<string> DownloadAsync(UpdateInfo update, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        if (update.InstallerUrl == null || update.InstallerName == null)
            throw new InvalidOperationException("This release has no installer attached.");

        var folder = Path.Combine(Path.GetTempPath(), "Shmoice-Update");
        if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true); // leftovers from earlier updates
        Directory.CreateDirectory(folder);
        var file = Path.Combine(folder, update.InstallerName);

        // Big file: don't let the 20 s API timeout cut it off; the caller's token cancels instead.
        using var request = new HttpRequestMessage(HttpMethod.Get, update.InstallerUrl);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(15));
        using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        response.EnsureSuccessStatusCode();
        var total = response.Content.Headers.ContentLength ?? update.InstallerSize;

        long read = 0;
        await using (var source = await response.Content.ReadAsStreamAsync(timeout.Token))
        await using (var target = File.Create(file))
        {
            var buffer = new byte[81920];
            int count;
            while ((count = await source.ReadAsync(buffer, timeout.Token)) > 0)
            {
                await target.WriteAsync(buffer.AsMemory(0, count), timeout.Token);
                read += count;
                if (total > 0) progress?.Report(Math.Min(1, (double)read / total));
            }
        }

        if (update.InstallerSize > 0 && read != update.InstallerSize)
        {
            File.Delete(file);
            throw new InvalidOperationException("The download was incomplete. Try again.");
        }
        return file;
    }

    /// <summary>
    /// Starts the installer silently. The caller must close Shmoice right after: the installer waits
    /// for it to exit, replaces it, and starts the new version (see installer.iss).
    /// </summary>
    public static void StartInstaller(string installerPath) =>
        Process.Start(new ProcessStartInfo(installerPath, "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /relaunch=1")
        {
            UseShellExecute = true,
        })?.Dispose();
}
