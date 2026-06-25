using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ChessKit
{
    internal sealed class UpdateCheckResult
    {
        public bool IsUpdateAvailable { get; init; }
        public bool IsRequired { get; init; }
        public string CurrentVersion { get; init; } = "";
        public string LatestVersion { get; init; } = "";
        public string DownloadUrl { get; init; } = "https://chesskit.ai/downloads.php";
        public string ReleaseNotes { get; init; } = "";
        public string Message { get; init; } = "";
    }

    internal static class UpdateChecker
    {
        private const string UpdateEndpoint = "https://chesskit.ai/api/app/update";
        private const string DefaultDownloadUrl = "https://chesskit.ai/downloads.php";
        private const string SiteBaseUrl = "https://chesskit.ai";

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNameCaseInsensitive = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        public static string CurrentVersion =>
            Assembly.GetExecutingAssembly()
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                ?.InformationalVersion
                ?.Split('+')[0]
                .Trim() ?? "1.0.0";

        public static async Task<UpdateCheckResult> CheckForUpdateAsync(CancellationToken cancellationToken = default)
        {
            string currentVersion = CurrentVersion;
            var request = new UpdateCheckRequest
            {
                Product = "ChessKit",
                Build = "FullRelease",
                CurrentVersion = currentVersion,
                AppVersion = currentVersion,
                Channel = "stable",
                TimestampUtc = DateTime.UtcNow.ToString("O")
            };

            try
            {
                using var client = new HttpClient
                {
                    Timeout = TimeSpan.FromSeconds(8)
                };

                string json = JsonSerializer.Serialize(request, JsonOptions);
                using var content = new StringContent(json, Encoding.UTF8, "application/json");
                using HttpResponseMessage response = await client.PostAsync(UpdateEndpoint, content, cancellationToken).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    return new UpdateCheckResult
                    {
                        CurrentVersion = currentVersion,
                        Message = $"Update server returned HTTP {(int)response.StatusCode}."
                    };
                }

                string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                UpdateCheckResponse? update = JsonSerializer.Deserialize<UpdateCheckResponse>(body, JsonOptions);
                if (update == null || string.IsNullOrWhiteSpace(update.LatestVersion))
                {
                    return new UpdateCheckResult
                    {
                        CurrentVersion = currentVersion,
                        Message = "Update server response was missing latestVersion."
                    };
                }

                bool updateAvailable = update.UpdateAvailable || IsNewerVersion(update.LatestVersion, currentVersion);
                return new UpdateCheckResult
                {
                    IsUpdateAvailable = updateAvailable,
                    IsRequired = update.Required || IsMinimumVersionBlocked(update.GetMinimumSupportedVersion(), currentVersion),
                    CurrentVersion = currentVersion,
                    LatestVersion = update.LatestVersion.Trim(),
                    DownloadUrl = NormalizeUrl(update.DownloadUrl, DefaultDownloadUrl),
                    ReleaseNotes = FirstNonEmpty(update.ReleaseNotes, update.ReleaseNotesUrl, update.Message),
                    Message = update.Message?.Trim() ?? ""
                };
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
            {
                return new UpdateCheckResult
                {
                    CurrentVersion = currentVersion,
                    Message = ex is TaskCanceledException ? "Update check timed out." : ex.Message
                };
            }
        }

        public static void OpenDownloadPage(string url)
        {
            string target = string.IsNullOrWhiteSpace(url) ? DefaultDownloadUrl : url;
            target = NormalizeUrl(target, DefaultDownloadUrl);
            Process.Start(new ProcessStartInfo
            {
                FileName = target,
                UseShellExecute = true
            });
        }

        private static bool IsMinimumVersionBlocked(string? minimumSupportedVersion, string currentVersion) =>
            !string.IsNullOrWhiteSpace(minimumSupportedVersion) &&
            IsNewerVersion(minimumSupportedVersion, currentVersion);

        private static string NormalizeUrl(string? url, string fallback)
        {
            string target = string.IsNullOrWhiteSpace(url) ? fallback : url.Trim();
            if (Uri.TryCreate(target, UriKind.Absolute, out Uri? absolute))
                return absolute.ToString();

            if (target.StartsWith("/", StringComparison.Ordinal))
                return SiteBaseUrl + target;

            return fallback;
        }

        private static string FirstNonEmpty(params string?[] values) =>
            values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? "";

        private static bool IsNewerVersion(string candidate, string current)
        {
            if (TryParseVersion(candidate, out Version? candidateVersion) &&
                TryParseVersion(current, out Version? currentVersion))
            {
                return candidateVersion > currentVersion;
            }

            return string.Compare(candidate?.Trim(), current?.Trim(), StringComparison.OrdinalIgnoreCase) > 0;
        }

        private static bool TryParseVersion(string value, out Version? version)
        {
            version = null;
            if (string.IsNullOrWhiteSpace(value))
                return false;

            string cleaned = value.Trim();
            int suffixIndex = cleaned.IndexOfAny(['-', '+']);
            if (suffixIndex >= 0)
                cleaned = cleaned[..suffixIndex];

            string[] parts = cleaned.Split('.', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0)
                return false;

            while (parts.Length < 2)
                parts = [.. parts, "0"];

            return Version.TryParse(string.Join('.', parts), out version);
        }

        private sealed class UpdateCheckRequest
        {
            [JsonPropertyName("product")]
            public string Product { get; init; } = "";
            [JsonPropertyName("build")]
            public string Build { get; init; } = "";
            [JsonPropertyName("currentVersion")]
            public string CurrentVersion { get; init; } = "";
            [JsonPropertyName("appVersion")]
            public string AppVersion { get; init; } = "";
            [JsonPropertyName("channel")]
            public string Channel { get; init; } = "";
            [JsonPropertyName("timestampUtc")]
            public string TimestampUtc { get; init; } = "";
        }

        private sealed class UpdateCheckResponse
        {
            [JsonPropertyName("ok")]
            public bool Ok { get; init; }
            [JsonPropertyName("updateAvailable")]
            public bool UpdateAvailable { get; init; }
            [JsonPropertyName("latestVersion")]
            public string LatestVersion { get; init; } = "";
            [JsonPropertyName("minimumSupportedVersion")]
            public string? MinimumSupportedVersion { get; init; }
            [JsonPropertyName("minSupportedVersion")]
            public string? MinSupportedVersion { get; init; }
            [JsonPropertyName("downloadUrl")]
            public string? DownloadUrl { get; init; }
            [JsonPropertyName("releaseNotes")]
            public string? ReleaseNotes { get; init; }
            [JsonPropertyName("releaseNotesUrl")]
            public string? ReleaseNotesUrl { get; init; }
            [JsonPropertyName("message")]
            public string? Message { get; init; }
            [JsonPropertyName("required")]
            public bool Required { get; init; }

            public string? GetMinimumSupportedVersion() =>
                string.IsNullOrWhiteSpace(MinSupportedVersion) ? MinimumSupportedVersion : MinSupportedVersion;
        }
    }
}
