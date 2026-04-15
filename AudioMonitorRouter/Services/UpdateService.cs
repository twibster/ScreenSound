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

    /// <summary>
    /// Copyright line surfaced in the About page footer. Read from the assembly's
    /// <c>[AssemblyCopyright]</c> attribute, which MSBuild fills from the csproj's
    /// <c>&lt;Copyright&gt;</c> property — so bumping the year in one place updates
    /// both the Win32 file-properties dialog and the UI. The verbatim form uses
    /// "(c)" for ASCII compatibility with those Win32 consumers; the VM rewrites
    /// that to the © glyph for display.
    /// </summary>
    public string Copyright => GetAssemblyCopyright();

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

            // html_url should always be present on a GitHub release, but the
            // API technically permits nulls. Fall back to composing the tag
            // URL ourselves — Uri.EscapeDataString because tag names can
            // legally contain characters (/, #, spaces) that would otherwise
            // break the path segment.
            string releaseUrl = release.HtmlUrl ??
                $"https://github.com/twibster/AudioMonitorRouter/releases/tag/{Uri.EscapeDataString(release.TagName)}";

            return CompareSemVer(latest, current) > 0
                ? new UpdateCheckResult.UpdateAvailable(current, latest, releaseUrl)
                : new UpdateCheckResult.UpToDate(current);
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

    /// <summary>
    /// Compares two version strings "SemVer-enough" for an update probe.
    /// Splits each into a numeric core (compared via <see cref="Version"/>)
    /// and an optional prerelease tail after the first '-'. On a numeric
    /// tie, a final release (no tail) outranks any prerelease — so
    /// "1.2.3" &gt; "1.2.3-beta". If the numeric core on either side fails
    /// to parse we fall back to an ordinal string compare of the original
    /// input, which may false-positive an update but will never silently
    /// hide a real one.
    /// </summary>
    private static int CompareSemVer(string a, string b)
    {
        var (coreA, preA) = SplitPrerelease(a);
        var (coreB, preB) = SplitPrerelease(b);

        if (Version.TryParse(coreA, out var va) && Version.TryParse(coreB, out var vb))
        {
            int byCore = va.CompareTo(vb);
            if (byCore != 0) return byCore;

            // Numeric cores are equal — prerelease < release on the same core.
            return (preA.Length, preB.Length) switch
            {
                (0, 0) => 0,
                (0, _) => 1,   // a is release, b is prerelease → a > b
                (_, 0) => -1,  // a is prerelease, b is release → a < b
                _      => string.Compare(preA, preB, StringComparison.Ordinal),
            };
        }

        // Numeric parse failed somewhere; don't pretend we know the ordering.
        return string.Compare(a, b, StringComparison.Ordinal);
    }

    private static (string core, string prerelease) SplitPrerelease(string v)
    {
        int dash = v.IndexOf('-');
        return dash < 0 ? (v, "") : (v[..dash], v[(dash + 1)..]);
    }

    private static string GetAssemblyCopyright()
    {
        var asm = Assembly.GetEntryAssembly() ?? typeof(UpdateService).Assembly;
        var value = asm.GetCustomAttribute<AssemblyCopyrightAttribute>()?.Copyright;
        return string.IsNullOrWhiteSpace(value)
            ? $"Copyright (c) {DateTime.Now.Year} Omar Omran"
            : value;
    }

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
