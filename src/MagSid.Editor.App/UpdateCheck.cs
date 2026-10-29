using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using MagSid.Core;

namespace MagSid.Editor.App;

/// <summary>
/// Whether a newer MagSID is out: the latest release on GitHub - the one the website names and
/// links its download to - against the editor running. Quiet when it cannot tell: no network,
/// GitHub not answering, a tag that is not a release number. Nothing is sent but the request.
/// </summary>
public static class UpdateCheck
{
    /// <summary>Where a newer version is downloaded from.</summary>
    public const string Website = "https://magnarharestad.github.io/MagSID/";

    private const string LatestRelease = "https://api.github.com/repos/MagnarHarestad/MagSID/releases/latest";

    /// <summary>Seconds GitHub has to answer before the check gives up.</summary>
    private const int TimeoutSeconds = 8;

    /// <summary>The release number of the editor running, from the project's &lt;Version&gt;.</summary>
    public static decimal? Running => ReleaseVersion.Of(typeof(UpdateCheck).Assembly.GetName().Version);

    /// <summary>The latest release's tag - "v1.64" - when it is newer than the editor running;
    /// null when it is not, or when it cannot be found out.</summary>
    public static async Task<string?> NewerReleaseAsync()
    {
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(TimeoutSeconds) };
            // Invariant: in a Norwegian Windows 1.63 is "1,63", which a header does not allow.
            client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("MagSID-Editor", Running?.ToString(CultureInfo.InvariantCulture) ?? "0"));
            client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            using var response = await client.GetAsync(LatestRelease).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return null;

            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
            string? tag = json.RootElement.TryGetProperty("tag_name", out var name) ? name.GetString() : null;
            return ReleaseVersion.IsNewer(tag, Running) ? tag : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException or FormatException)
        {
            return null;
        }
    }
}
