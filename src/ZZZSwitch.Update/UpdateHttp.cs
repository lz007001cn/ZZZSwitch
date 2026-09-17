using System.Net;
using System.Net.Http.Headers;
using System.Text;

namespace ZZZSwitch.Update;

internal static class UpdateHttp
{
    internal static bool IsGitHubAsset(Uri uri) => uri.Scheme == "https" && uri.IsDefaultPort &&
        uri.Host == "github.com" && uri.AbsolutePath.StartsWith("/lz007001cn/ZZZSwitch/releases/download/", StringComparison.Ordinal);

    internal static async Task<HttpResponseMessage> GetAsync(HttpClient http, Uri uri, string version, CancellationToken token)
    {
        var allowRedirect = IsGitHubAsset(uri);
        for (var hop = 0; ; hop++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.UserAgent.Add(new ProductInfoHeaderValue("ZZZSwitch", version));
            if (uri.Host == "api.github.com")
            {
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
                request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
            }
            var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            if (response.RequestMessage?.RequestUri is { } final && final != uri)
            { response.Dispose(); throw new UpdateException(UpdateError.Http, "Implicit redirects are not accepted."); }
            if (response.StatusCode is HttpStatusCode.MovedPermanently or HttpStatusCode.Redirect or HttpStatusCode.SeeOther or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect)
            {
                var location = response.Headers.Location;
                response.Dispose();
                var next = location is null ? null : new Uri(uri, location);
                if (!allowRedirect || hop >= 3 || next is null || next.Scheme != "https" || !next.IsDefaultPort ||
                    !string.IsNullOrEmpty(next.UserInfo) || !string.IsNullOrEmpty(next.Fragment) ||
                    !(IsGitHubAsset(next) || next.Host is "release-assets.githubusercontent.com" or "objects.githubusercontent.com"))
                    throw new UpdateException(UpdateError.Http, "Unsafe or excessive update redirect.");
                uri = next;
                continue;
            }
            if (!response.IsSuccessStatusCode)
            {
                var status = (int)response.StatusCode;
                var limited = status == 429 || status == 403 &&
                    (response.Headers.Contains("Retry-After") || response.Headers.TryGetValues("X-RateLimit-Remaining", out var remaining) && remaining.Contains("0"));
                response.Dispose();
                throw new UpdateException(limited ? UpdateError.RateLimited : UpdateError.Http, limited ? "GitHub API rate limit reached; retry later." : $"Update server returned HTTP {status}.");
            }
            return response;
        }
    }

    internal static async Task<string> ReadTextAsync(HttpClient http, Uri uri, string version, CancellationToken token, int limit = UpdateManifestParser.MaxManifestSize)
    {
        using var response = await GetAsync(http, uri, version, token).ConfigureAwait(false);
        await using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        using var output = new MemoryStream();
        var buffer = new byte[8192]; int count;
        while ((count = await stream.ReadAsync(buffer, token).ConfigureAwait(false)) != 0)
        {
            if (output.Length + count > limit) throw new UpdateException(UpdateError.InvalidManifest, "Update metadata exceeds size limit.");
            output.Write(buffer, 0, count);
        }
        return Encoding.UTF8.GetString(output.ToArray());
    }
}
