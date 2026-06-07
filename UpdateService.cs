using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace AntigravityQuota
{
    public class GitHubRelease
    {
        public string tag_name { get; set; } = "";
        public string html_url { get; set; } = "";
        public string name { get; set; } = "";
        public string body { get; set; } = "";
    }

    public class UpdateService
    {
        private readonly HttpClient _httpClient;

        public UpdateService()
        {
            _httpClient = new HttpClient();
            // GitHub API requires a User-Agent header
            _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("AntigravityQuotaUpdater/1.0");
        }

        public async Task<GitHubRelease?> CheckForUpdatesAsync(string repoOwner, string repoName)
        {
            string url = $"https://api.github.com/repos/{repoOwner}/{repoName}/releases/latest";
            try
            {
                string json = await _httpClient.GetStringAsync(url);
                var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                return JsonSerializer.Deserialize<GitHubRelease>(json, options);
            }
            catch (Exception)
            {
                // Return null if request fails (e.g. offline, rate limit, API change)
                return null;
            }
        }

        public static bool IsNewerVersion(string currentVersionStr, string latestVersionStr)
        {
            if (string.IsNullOrEmpty(currentVersionStr) || string.IsNullOrEmpty(latestVersionStr))
                return false;

            string cleanCurrent = currentVersionStr.TrimStart('v', 'V', ' ');
            string cleanLatest = latestVersionStr.TrimStart('v', 'V', ' ');

            if (Version.TryParse(cleanCurrent, out Version? currentVersion) &&
                Version.TryParse(cleanLatest, out Version? latestVersion))
            {
                return latestVersion > currentVersion;
            }

            // Fallback simple comparison if parsing fails (e.g. custom tagging)
            return string.Compare(cleanLatest, cleanCurrent, StringComparison.OrdinalIgnoreCase) > 0;
        }
    }
}
