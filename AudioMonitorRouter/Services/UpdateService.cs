using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AudioMonitorRouter.Services;

/// <summary>
/// Outcome of an update probe. The About page binds its status text to one of
/// these four shapes — success with no update, success with an update, a clean
/// "couldn't reach the server" message, or an unexpected error we want to log.
/// </summary>
public abstract record UpdateCheckResult
{
    /// <summary>Already on the latest published release.</summary>
    public sealed record UpToDate(string CurrentVersion) : UpdateCheckResult;

    /// <summary>A newer release is published on GitHub.</summary>
    public sealed record UpdateAvailable(
        string CurrentVersion,
        string LatestVersion,
        string ReleaseUrl) : UpdateCheckResult;

    /// <summary>Network/HTTP failure — expected when the user is offline.</summary>
    public sealed record NetworkError(string Message) : UpdateCheckResult;

    /// <summary>Anything else (bad JSON, unparseable tag, rate limit, etc).</summary>
    public sealed record Failed(string Message) : UpdateCheckResult;
}

/// <summary>
/// Queries the GitHub Releases API to find out whether a newer version of the
/// app is available. We deliberately do NOT download or apply updates — this is
/// a pointer-only check; the user clicks through to the GitHub release page and
/// runs the installer themselves. Keeping it read-only avoids needing admin
/// elevation, auto-update hosting, and code-signing rotation.
/// </summary>
public class UpdateService
{
    // Public, unauthenticated endpoint — rate-limited to 60 requests/hour per IP,
    // which is plenty for a user-initiated "check for updates" button.
    private const string LatestReleaseApi =
        "https://api.github.com/repos/twibster/AudioMonitorRouter/releases/latest";

    // GitHub requires a User-Agent on every request; the product name is also
    // useful in their server logs if we ever need to correlate a rate-limit bug
    // with a specific release. Fall back to a plain version if the current
    // informational version contains characters ProductInfoHeaderValue rejects
    // (e.g. a future "+shahash" sourcelink suffix we haven't stripped) — we'd
    // rather send a slightly-stale User-Agent than crash the About page.
    private static readonly ProductInfoHeaderValue UserAgent = BuildUserAgent();

    private static ProductInfoHeaderValue BuildUserAgent()
    {
        try { return new ProductInfoHeaderValue("AudioMonitorRouter", GetInformationalVersion()); }
        catch { return new ProductInfoHeaderValue("AudioMonitorRouter", "1.0"); }
    }

    /// <summary>
    /// The version string shown in the About page. Derived from the assembly's
    /// <c>[AssemblyInformationalVersion]</c> (falls back to the file version)
    /// so the value automatically tracks what CI built rather than a hardcoded
    /// literal that drifts from the tag.
    /// </summary>
    public string CurrentVersion => GetInformationalVersion();

    public async Task<UpdateCheckResult> CheckForUpdateAsync(CancellationToken ct = default)
    {
        string current = CurrentVersion;

        try
        {
            // A fresh HttpClient per call is fine for a once-in-a-blue-moon
            // user action — no need for IHttpClientFactory ceremony here, and
            // the socket exhaustion concerns that usually drive it don't apply
            // at this call rate.
            using var http = new HttpClient();
            http.DefaultRequestHeaders.UserAgent.Add(UserAgent);
            http.DefaultRequestHeaders.Accept.Add(
                new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            http.Timeout = TimeSpan.FromSeconds(10);

            using var response = await http.GetAsync(LatestReleaseApi, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                // Treat 403 (rate-limit) specially so the message is actionable
                // rather than a generic HTTP code.
                if ((int)response.StatusCode == 403)
                    return new UpdateCheckResult.Failed(
                        "GitHub rate limit reached — try again in an hour.");
                return new UpdateCheckResult.NetworkError(
                    $"GitHub returned {(int)response.StatusCode} {response.ReasonPhrase}.");
            }

            using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            var release = await JsonSerializer.DeserializeAsync<GitHubRelease>(
                stream, cancellationToken: ct).ConfigureAwait(false);

            if (release == null || string.IsNullOrWhiteSpace(release.TagName))
                return new UpdateCheckResult.Failed("Release metadata was empty.");

            string latest = StripVPrefix(release.TagName);

            if (!Version.TryParse(latest, out var latestVer) ||
                !Version.TryParse(current, out var currentVer))
            {
                // If either side doesn't parse (e.g. "1.2.3-beta"), fall back to
                // a simple string compare. Better to surface uncertainty than
                // silently claim "up to date".
                return string.Equals(latest, current, StringComparison.Ordinal)
                    ? new UpdateCheckResult.UpToDate(current)
                    : new UpdateCheckResult.UpdateAvailable(current, latest,
                        release.HtmlUrl ?? $"https://github.com/twibster/AudioMonitorRouter/releases/tag/{release.TagName}");
            }

            if (latestVer > currentVer)
            {
                return new UpdateCheckResult.UpdateAvailable(
                    CurrentVersion: current,
                    LatestVersion: latest,
                    ReleaseUrl: release.HtmlUrl ??
                        $"https://github.com/twibster/AudioMonitorRouter/releases/tag/{release.TagName}");
            }

            return new UpdateCheckResult.UpToDate(current);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (HttpRequestException ex)
        {
            return new UpdateCheckResult.NetworkError(ex.Message);
        }
        catch (TaskCanceledException)
        {
            // HttpClient timeout surfaces as TaskCanceledException with no
            // cancellation token involvement. Treat it as a network error so
            // the user message makes sense.
            return new UpdateCheckResult.NetworkError("Request timed out.");
        }
        catch (Exception ex)
        {
            return new UpdateCheckResult.Failed(ex.Message);
        }
    }

    /// <summary>
    /// Reads the three-part version from the entry assembly. We prefer
    /// <c>AssemblyInformationalVersion</c> because MSBuild's <c>-p:Version</c>
    /// feeds into it verbatim, whereas <c>AssemblyVersion</c> gets padded to
    /// four parts (1.2.3 → 1.2.3.0) which looks wrong in UI.
    /// </summary>
    private static string GetInformationalVersion()
    {
        var asm = Assembly.GetEntryAssembly() ?? typeof(UpdateService).Assembly;

        var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(info))
        {
            // InformationalVersion can include a '+commithash' SourceLink
            // suffix on CI builds; strip it for display.
            int plus = info.IndexOf('+');
            return plus >= 0 ? info[..plus] : info;
        }

        return asm.GetName().Version?.ToString(3) ?? "0.0.0";
    }

    private static string StripVPrefix(string tag) =>
        tag.StartsWith('v') || tag.StartsWith('V') ? tag[1..] : tag;

    // Minimal shape of the GitHub "latest release" response — only the two
    // fields we actually read. System.Text.Json ignores anything else.
    private sealed class GitHubRelease
    {
        [JsonPropertyName("tag_name")]
        public string? TagName { get; set; }

        [JsonPropertyName("html_url")]
        public string? HtmlUrl { get; set; }
    }
}
