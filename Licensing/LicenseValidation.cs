using System.Net;
using System.Net.Sockets;
using System.Globalization;
using System.Reflection;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ChessKit
{
    internal enum LicenseValidationState
    {
        Licensed,
        NotLicensed,
        NetworkError,
        InvalidResponse
    }

    internal sealed class LicenseValidationResult
    {
        public LicenseValidationState State { get; init; }
        public string HardwareId { get; init; } = "";
        public string Status { get; init; } = "";
        public string Plan { get; init; } = "";
        public DateTime? ExpiresAtUtc { get; init; }
        public DateTime? ServerTimeUtc { get; init; }
        public string Message { get; init; } = "";

        public bool IsLicensed => State == LicenseValidationState.Licensed;
    }

    internal static class LicenseValidator
    {
        private const string LicenseEndpoint = "https://chesskit.ai/api/license/verify";

        private const string LicenseSignaturePublicKeyPem = """
-----BEGIN PUBLIC KEY-----
MIIBIjANBgkqhkiG9w0BAQEFAAOCAQ8AMIIBCgKCAQEAyNVFTKjgRDQYOGtTLmOD
bDsclSRlaK3w8z4hvOSNbBvlD9tylswsCa6IUK37eKoYDKm2av/P2Jr7Rr/0N4O+
LV33tNAMYbdSfmfBevHvflbiXzDCs2ona7h/kLgnaWRZLgKer4ATwUtbScFrDM/b
QTavGxrXksI9qDCHRnH7c8vUQtfZHNUNME2mD1g/TREJCUB7sDgiy/02t7c/mGQh
EKGmqTmVKiWuTgrhjW7AXIne1pJUlU5PUDa0m68Kj+wNV4zn+fxul78fahuUbmH0
/zEkII391Qfrn1muz1SvcUqJoSYA94pVOg7Y5GbM0dXQDVG1MtJT8c7/3qVIofI2
7wIDAQAB
-----END PUBLIC KEY-----
""";

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            PropertyNameCaseInsensitive = true
        };

        public static async Task<LicenseValidationResult> ValidateFullVersionAsync(CancellationToken cancellationToken = default)
        {
            string build =
#if DEBUG
                "Debug";
#else
                "Release";
#endif
            return await ValidateProductAsync("ChessKit", build, cancellationToken).ConfigureAwait(false);
        }

        public static async Task<LicenseValidationResult> ValidateProductAsync(string product, string build, CancellationToken cancellationToken = default)
        {
            string hardwareId = HardwareIdentity.GetHardwareId();

#if DEBUG
            if (string.Equals(product, "HumanChessEngine", StringComparison.OrdinalIgnoreCase))
            {
                return new LicenseValidationResult
                {
                    State = LicenseValidationState.Licensed,
                    HardwareId = hardwareId,
                    Status = "debug",
                    Plan = "debug",
                    ServerTimeUtc = DateTime.UtcNow,
                    Message = "Debug build skips Human Chess Engine license validation."
                };
            }
#endif

            string nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));

            var request = new LicenseVerifyRequest
            {
                Hwid = hardwareId,
                Product = string.IsNullOrWhiteSpace(product) ? "ChessKit" : product.Trim(),
                Build = build,
                AppVersion = GetAppVersion(),
                Nonce = nonce,
                TimestampUtc = DateTime.UtcNow
            };

            try
            {
                using var http = new HttpClient
                {
                    Timeout = TimeSpan.FromSeconds(12)
                };

                string requestJson = JsonSerializer.Serialize(request, JsonOptions);
                using var content = new StringContent(requestJson, Encoding.UTF8, "application/json");
                using HttpResponseMessage response = await http.PostAsync(LicenseEndpoint, content, cancellationToken).ConfigureAwait(false);
                string responseJson = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

                LicenseVerifyResponse? license = null;
                try
                {
                    license = JsonSerializer.Deserialize<LicenseVerifyResponse>(responseJson, JsonOptions);
                }
                catch (JsonException)
                {
                    return NetworkIssue(hardwareId, "The license server returned an unreadable response. Please try again later.");
                }

                if (license == null)
                    return NetworkIssue(hardwareId, "The license server returned an empty response. Please try again later.");

                if (IsTransientHttpStatus(response.StatusCode))
                {
                    return NetworkIssue(
                        hardwareId,
                        $"The license service is temporarily unavailable (HTTP {(int)response.StatusCode}). Please try again later.");
                }

                if (!string.Equals(license.Nonce, nonce, StringComparison.Ordinal))
                    return Invalid(hardwareId, "The license response did not match this verification request.");

                if (IsSignatureVerificationConfigured() && !VerifySignature(license, request))
                    return Invalid(hardwareId, "The license response signature is invalid.");

                if (!response.IsSuccessStatusCode)
                {
                    return new LicenseValidationResult
                    {
                        State = LicenseValidationState.NotLicensed,
                        HardwareId = hardwareId,
                        Status = NormalizeStatus(license.Status),
                        Message = FirstNonEmpty(license.Message, $"License server rejected this HWID ({(int)response.StatusCode}).")
                    };
                }

                if (!license.Ok)
                {
                    return new LicenseValidationResult
                    {
                        State = LicenseValidationState.NotLicensed,
                        HardwareId = hardwareId,
                        Status = NormalizeStatus(license.Status),
                        Plan = license.Plan ?? "",
                        ExpiresAtUtc = license.GetExpiresAtUtc(),
                        ServerTimeUtc = license.GetServerTimeUtc(),
                        Message = FirstNonEmpty(license.Message, "The license server did not approve this computer.")
                    };
                }

                string status = NormalizeStatus(license.Status);
                DateTime? expiresAtUtc = license.GetExpiresAtUtc();
                DateTime effectiveNowUtc = license.GetServerTimeUtc() ?? DateTime.UtcNow;
                if (string.Equals(status, "active", StringComparison.Ordinal))
                {
                    if (expiresAtUtc.HasValue && expiresAtUtc.Value <= effectiveNowUtc)
                    {
                        return new LicenseValidationResult
                        {
                            State = LicenseValidationState.NotLicensed,
                            HardwareId = hardwareId,
                            Status = "expired",
                            Plan = license.Plan ?? "",
                            ExpiresAtUtc = expiresAtUtc,
                            ServerTimeUtc = license.GetServerTimeUtc(),
                            Message = "This license has expired."
                        };
                    }

                    return new LicenseValidationResult
                    {
                        State = LicenseValidationState.Licensed,
                        HardwareId = hardwareId,
                        Status = status,
                        Plan = license.Plan ?? "",
                        ExpiresAtUtc = expiresAtUtc,
                        ServerTimeUtc = license.GetServerTimeUtc(),
                        Message = "License active."
                    };
                }

                return new LicenseValidationResult
                {
                    State = LicenseValidationState.NotLicensed,
                    HardwareId = hardwareId,
                    Status = status,
                    Plan = license.Plan ?? "",
                    ExpiresAtUtc = expiresAtUtc,
                    ServerTimeUtc = license.GetServerTimeUtc(),
                    Message = FirstNonEmpty(license.Message, $"License status: {status}.")
                };
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return NetworkIssue(hardwareId, "The license server did not respond in time. Please check your connection and try again.");
            }
            catch (Exception ex) when (IsLikelyNetworkException(ex))
            {
                return NetworkIssue(hardwareId, BuildNetworkFailureMessage(ex));
            }
            catch (Exception ex)
            {
                return Invalid(hardwareId, $"License validation failed: {ex.Message}");
            }
        }

        private static LicenseValidationResult NetworkIssue(string hardwareId, string message) => new()
        {
            State = LicenseValidationState.NetworkError,
            HardwareId = hardwareId,
            Message = message
        };

        private static LicenseValidationResult Invalid(string hardwareId, string message) => new()
        {
            State = LicenseValidationState.InvalidResponse,
            HardwareId = hardwareId,
            Message = message
        };

        private static bool IsTransientHttpStatus(HttpStatusCode statusCode)
        {
            int code = (int)statusCode;
            return code >= 500 ||
                statusCode == HttpStatusCode.RequestTimeout ||
                statusCode == HttpStatusCode.TooManyRequests ||
                statusCode == HttpStatusCode.BadGateway ||
                statusCode == HttpStatusCode.ServiceUnavailable ||
                statusCode == HttpStatusCode.GatewayTimeout;
        }

        private static bool IsLikelyNetworkException(Exception ex)
        {
            for (Exception? current = ex; current != null; current = current.InnerException)
            {
                if (current is HttpRequestException ||
                    current is IOException ||
                    current is SocketException ||
                    current is AuthenticationException ||
                    current is TimeoutException)
                {
                    return true;
                }
            }

            return false;
        }

        private static string BuildNetworkFailureMessage(Exception ex)
        {
            string detail = "";
            for (Exception? current = ex; current != null; current = current.InnerException)
            {
                if (!string.IsNullOrWhiteSpace(current.Message))
                {
                    detail = current.Message.Trim();
                    break;
                }
            }

            return string.IsNullOrWhiteSpace(detail)
                ? "Could not reach chesskit.ai. Please check your internet connection and try again."
                : $"Could not reach chesskit.ai: {detail}";
        }

        private static bool IsSignatureVerificationConfigured() =>
            !string.IsNullOrWhiteSpace(LicenseSignaturePublicKeyPem);

        private static bool VerifySignature(LicenseVerifyResponse response, LicenseVerifyRequest request)
        {
            if (string.IsNullOrWhiteSpace(response.Signature))
                return false;

            try
            {
                byte[] signature = Convert.FromBase64String(response.Signature);
                byte[] payload = Encoding.UTF8.GetBytes(BuildSignaturePayload(response, request));

                using RSA rsa = RSA.Create();
                try
                {
                    rsa.ImportFromPem(LicenseSignaturePublicKeyPem);
                    return rsa.VerifyData(payload, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
                }
                catch (CryptographicException)
                {
                }

                using ECDsa ecdsa = ECDsa.Create();
                ecdsa.ImportFromPem(LicenseSignaturePublicKeyPem);
                return ecdsa.VerifyData(payload, signature, HashAlgorithmName.SHA256);
            }
            catch
            {
                return false;
            }
        }

        private static string BuildSignaturePayload(LicenseVerifyResponse response, LicenseVerifyRequest request)
        {
            string expires = NormalizeSignatureValue(FirstNonEmpty(response.ExpiresAtUtc, response.ExpiresAt, response.ExpiresAtSnake));
            string serverTime = NormalizeSignatureValue(response.ServerTimeUtc);
            return string.Join("\n",
                $"hwid={NormalizeSignatureValue(request.Hwid).ToLowerInvariant()}",
                $"ok={response.Ok.ToString().ToLowerInvariant()}",
                $"status={NormalizeStatus(response.Status)}",
                $"plan={NormalizeSignatureValue(response.Plan)}",
                $"expiresAtUtc={expires}",
                $"serverTimeUtc={serverTime}",
                $"nonce={NormalizeSignatureValue(response.Nonce)}");
        }

        private static string GetAppVersion()
        {
            try
            {
                return Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.0.0";
            }
            catch
            {
                return "0.0.0";
            }
        }

        private static string NormalizeStatus(string? status) =>
            (status ?? "").Trim().ToLowerInvariant();

        private static string FirstNonEmpty(params string?[] values) =>
            values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? "";

        private static string NormalizeSignatureValue(string? value) =>
            (value ?? "").Trim();

        private sealed class LicenseVerifyRequest
        {
            [JsonPropertyName("hwid")]
            public string Hwid { get; init; } = "";

            [JsonPropertyName("product")]
            public string Product { get; init; } = "";

            [JsonPropertyName("build")]
            public string Build { get; init; } = "";

            [JsonPropertyName("appVersion")]
            public string AppVersion { get; init; } = "";

            [JsonPropertyName("nonce")]
            public string Nonce { get; init; } = "";

            [JsonPropertyName("timestampUtc")]
            public DateTime TimestampUtc { get; init; }
        }

        private sealed class LicenseVerifyResponse
        {
            [JsonPropertyName("ok")]
            public bool Ok { get; init; }

            [JsonPropertyName("status")]
            public string? Status { get; init; }

            [JsonPropertyName("plan")]
            public string? Plan { get; init; }

            [JsonPropertyName("expiresAtUtc")]
            public string? ExpiresAtUtc { get; init; }

            [JsonPropertyName("expiresAt")]
            public string? ExpiresAt { get; init; }

            [JsonPropertyName("serverTimeUtc")]
            public string? ServerTimeUtc { get; init; }

            [JsonPropertyName("nonce")]
            public string? Nonce { get; init; }

            [JsonPropertyName("signature")]
            public string? Signature { get; init; }

            [JsonPropertyName("message")]
            public string? Message { get; init; }

            [JsonPropertyName("expires_at")]
            public string? ExpiresAtSnake { get; init; }

            public DateTime? GetExpiresAtUtc()
            {
                string value = FirstNonEmpty(ExpiresAtUtc, ExpiresAt, ExpiresAtSnake);
                return ParseUtcDateTime(value);
            }

            public DateTime? GetServerTimeUtc()
            {
                return ParseUtcDateTime(ServerTimeUtc);
            }

            private static DateTime? ParseUtcDateTime(string? value)
            {
                if (string.IsNullOrWhiteSpace(value))
                    return null;

                return DateTime.TryParse(
                    value,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                    out DateTime parsed)
                    ? parsed
                    : null;
            }
        }
    }
}
