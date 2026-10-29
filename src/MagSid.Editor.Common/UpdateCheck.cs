using System.Globalization;
using System.Net.Http.Headers;
using System.Reflection;
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

    /// <summary>Where the Windows editor is released, and how its tags begin: v1.63.</summary>
    public const string Repository = "MagnarHarestad/MagSID";

    public const string TagPrefix = "v";

    /// <summary>Seconds GitHub has to answer before the check gives up.</summary>
    private const int TimeoutSeconds = 8;

    /// <summary>The release number of the editor running, from its project's &lt;Version&gt; - the
    /// program's, not this file's, which a library may be built into.</summary>
    public static decimal? Running => ReleaseVersion.Of((Assembly.GetEntryAssembly() ?? typeof(UpdateCheck).Assembly).GetName().Version);

    /// <summary>The latest release's tag - "v1.64" - when it is newer than the editor running;
    /// null when it is not, or when it cannot be found out.</summary>
    public static Task<string?> NewerReleaseAsync() => NewerReleaseAsync(Repository, TagPrefix);

    /// <summary>The same for an editor released elsewhere: the latest release of
    /// <paramref name="repository"/> whose tag is <paramref name="tagPrefix"/> and a number.</summary>
    public static async Task<string?> NewerReleaseAsync(string repository, string tagPrefix)
    {
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(TimeoutSeconds) };
            // Invariant: in a Norwegian Windows 1.63 is "1,63", which a header does not allow.
            client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("MagSID-Editor", Running?.ToString(CultureInfo.InvariantCulture) ?? "0"));
            client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            using var response = await client.GetAsync($"https://api.github.com/repos/{repository}/releases/latest").ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return null;

            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
            string? tag = json.RootElement.TryGetProperty("tag_name", out var name) ? name.GetString() : null;
            return tag is not null && tag.StartsWith(tagPrefix, StringComparison.OrdinalIgnoreCase)
                   && ReleaseVersion.IsNewer(tag[tagPrefix.Length..], Running) ? tag : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException or FormatException)
        {
            return null;
        }
    }
}
