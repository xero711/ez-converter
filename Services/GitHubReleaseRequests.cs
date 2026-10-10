using System.Net.Http;
using System.Net.Http.Headers;

namespace MediaConverter.Services;

internal static class GitHubReleaseRequests
{
    public static void Configure(HttpRequestMessage request, string userAgent)
    {
        if (request.RequestUri is not { Scheme: "https" } uri ||
            !uri.Host.Equals("api.github.com", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("GitHub release credentials may only be sent to api.github.com over HTTPS.");
        }

        request.Headers.UserAgent.ParseAdd(userAgent);
        request.Headers.Accept.ParseAdd("application/vnd.github+json");

        var token = Environment.GetEnvironmentVariable("EZCONVERTER_GITHUB_TOKEN")?.Trim();
        if (!string.IsNullOrEmpty(token))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }
    }
}
