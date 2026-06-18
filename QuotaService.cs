using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Management;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace AntigravityQuota
{
    public class QuotaService
    {
        // Strict SSL validation for all Google API calls
        private readonly HttpClient _googleHttpClient;
        // Allows self-signed certificates on loopback only (local Language Server)
        private readonly HttpClient _localHttpClient;

        public QuotaService()
        {
            _googleHttpClient = new HttpClient();

            var localHandler = new HttpClientHandler
            {
                ServerCertificateCustomValidationCallback = (message, cert, chain, errors) =>
                {
                    if (errors == System.Net.Security.SslPolicyErrors.None) return true;
                    // Allow self-signed only for loopback addresses
                    var host = message.RequestUri?.Host;
                    return host == "127.0.0.1" || host == "localhost" || host == "::1";
                }
            };
            _localHttpClient = new HttpClient(localHandler);
        }

        private async Task<string?> GetValidAccessTokenAsync(string email)
        {
            var tokens = ConfigService.LoadAccountTokens(email);
            if (tokens == null) return null;

            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            if (tokens.expiresAt > now + 300000 && !string.IsNullOrEmpty(tokens.accessToken))
            {
                return tokens.accessToken;
            }

            if (string.IsNullOrEmpty(tokens.refreshToken)) return null;

            try
            {
                var refreshParams = new Dictionary<string, string>
                {
                    { "client_id", OAuthConfig.ClientId },
                    { "client_secret", OAuthConfig.ClientSecret },
                    { "refresh_token", tokens.refreshToken },
                    { "grant_type", "refresh_token" }
                };

                var res = await _googleHttpClient.PostAsync("https://oauth2.googleapis.com/token", new FormUrlEncodedContent(refreshParams));
                if (!res.IsSuccessStatusCode) return null;

                string json = await res.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                string accessToken = root.GetProperty("access_token").GetString() ?? "";
                int expiresIn = root.GetProperty("expires_in").GetInt32();
                tokens.accessToken = accessToken;
                tokens.expiresAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + (expiresIn * 1000);

                ConfigService.SaveAccountTokens(email, tokens);
                return accessToken;
            }
            catch
            {
                return null;
            }
        }

        public async Task<QuotaSnapshot> FetchQuotaAsync(string method, string? activeEmail)
        {
            if (string.IsNullOrEmpty(activeEmail))
            {
                var accounts = ConfigService.ListAccounts();
                var activeAcc = accounts.Find(a => a.IsActive);
                activeEmail = activeAcc?.Email;
            }

            if (string.IsNullOrEmpty(activeEmail))
            {
                throw new Exception("No logged-in Google account found. Please connect an account first.");
            }

            return await FetchGoogleQuotaAsync(activeEmail);
        }

        private async Task<QuotaSnapshot> FetchLocalQuotaAsync()
        {
            var lsp = DetectLanguageServer();
            if (lsp.pid == 0 || !lsp.extensionServerPort.HasValue)
            {
                throw new Exception("Antigravity Language Server process not found. Verify it is running in your IDE.");
            }

            string? baseUrl = await ProbeLocalServerAsync(lsp.extensionServerPort.Value, lsp.csrfToken);
            if (string.IsNullOrEmpty(baseUrl))
            {
                throw new Exception($"Failed to connect to local Connect API on port {lsp.extensionServerPort}.");
            }

            var request = new HttpRequestMessage(HttpMethod.Post, baseUrl + "/exa.language_server_pb.LanguageServerService/GetUserStatus");
            request.Headers.Add("Connect-Protocol-Version", "1");
            if (!string.IsNullOrEmpty(lsp.csrfToken))
            {
                request.Headers.Add("X-Codeium-Csrf-Token", lsp.csrfToken);
            }
            request.Content = new StringContent("{}", Encoding.UTF8, "application/json");

            var response = await _localHttpClient.SendAsync(request);
            if (!response.IsSuccessStatusCode)
            {
                throw new Exception($"Connect RPC status failed: {response.StatusCode}");
            }

            string json = await response.Content.ReadAsStringAsync();
            return ParseLocalUserStatus(json);
        }

        private async Task<QuotaSnapshot> FetchGoogleQuotaAsync(string email)
        {
            string? token = await GetValidAccessTokenAsync(email);
            if (string.IsNullOrEmpty(token))
            {
                throw new Exception($"Authentication expired for {email}. Please log in again.");
            }

            // 1. Call LoadCodeAssist to get project ID & plan info
            var reqAssist = new HttpRequestMessage(HttpMethod.Post, "https://cloudcode-pa.googleapis.com/v1internal:loadCodeAssist");
            reqAssist.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            reqAssist.Headers.UserAgent.ParseAdd("antigravity");
            reqAssist.Content = new StringContent("{\"metadata\":{\"ideType\":\"ANTIGRAVITY\",\"platform\":\"PLATFORM_UNSPECIFIED\",\"pluginType\":\"GEMINI\"}}", Encoding.UTF8, "application/json");

            var resAssist = await _googleHttpClient.SendAsync(reqAssist);
            if (!resAssist.IsSuccessStatusCode)
            {
                throw new Exception($"LoadCodeAssist failed: {resAssist.StatusCode}");
            }

            string assistJson = await resAssist.Content.ReadAsStringAsync();
            using var assistDoc = JsonDocument.Parse(assistJson);
            var assistRoot = assistDoc.RootElement;

            // Extract project ID
            string? projectId = null;
            if (assistRoot.TryGetProperty("cloudaicompanionProject", out var projProp))
            {
                if (projProp.ValueKind == JsonValueKind.String)
                {
                    projectId = projProp.GetString();
                }
                else if (projProp.ValueKind == JsonValueKind.Object && projProp.TryGetProperty("id", out var idProp))
                {
                    projectId = idProp.GetString();
                }
            }

            // Cache project ID if found
            if (!string.IsNullOrEmpty(projectId))
            {
                var tokens = ConfigService.LoadAccountTokens(email);
                if (tokens != null)
                {
                    tokens.projectId = projectId;
                    ConfigService.SaveAccountTokens(email, tokens);
                }
            }

            // 2. Call FetchAvailableModels to get actual limits
            var reqModels = new HttpRequestMessage(HttpMethod.Post, "https://cloudcode-pa.googleapis.com/v1internal:fetchAvailableModels");
            reqModels.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            reqModels.Headers.UserAgent.ParseAdd("antigravity");
            reqModels.Content = new StringContent(string.IsNullOrEmpty(projectId) ? "{}" : $"{{\"project\":\"{projectId}\"}}", Encoding.UTF8, "application/json");

            var resModels = await _googleHttpClient.SendAsync(reqModels);
            if (!resModels.IsSuccessStatusCode)
            {
                throw new Exception($"FetchAvailableModels failed: {resModels.StatusCode}");
            }

            string modelsJson = await resModels.Content.ReadAsStringAsync();
            return ParseGoogleQuota(assistJson, modelsJson, email);
        }

        private async Task<string?> ProbeLocalServerAsync(int port, string? csrfToken)
        {
            string[] urls = { $"http://127.0.0.1:{port}", $"https://127.0.0.1:{port}" };
            foreach (string url in urls)
            {
                try
                {
                    var req = new HttpRequestMessage(HttpMethod.Post, url + "/exa.language_server_pb.LanguageServerService/GetUserStatus");
                    req.Headers.Add("Connect-Protocol-Version", "1");
                    if (!string.IsNullOrEmpty(csrfToken))
                    {
                        req.Headers.Add("X-Codeium-Csrf-Token", csrfToken);
                    }
                    req.Content = new StringContent("{}", Encoding.UTF8, "application/json");

                    var cts = new System.Threading.CancellationTokenSource(400);
                    var res = await _localHttpClient.SendAsync(req, cts.Token);
                    if (res.IsSuccessStatusCode) return url;
                }
                catch {}
            }
            return null;
        }

        private QuotaSnapshot ParseLocalUserStatus(string json)
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var userStatus = root.TryGetProperty("userStatus", out var us) ? us : root;

            string email = userStatus.TryGetProperty("email", out var em) ? (em.GetString() ?? "") : "";
            string planType = "Standard Plan";

            var snapshot = new QuotaSnapshot
            {
                Timestamp = DateTime.UtcNow.ToString("o"),
                Method = "local",
                Email = email
            };

            // Parse plan credits
            if (userStatus.TryGetProperty("planStatus", out var ps))
            {
                if (ps.TryGetProperty("planInfo", out var pi) && pi.TryGetProperty("planType", out var pt))
                {
                    planType = pt.GetString() ?? planType;
                }

                if (ps.TryGetProperty("availablePromptCredits", out var availProp) &&
                    pi.TryGetProperty("monthlyPromptCredits", out var monthProp))
                {
                    int available = availProp.GetInt32();
                    int monthly = monthProp.GetInt32();
                    if (monthly > 0)
                    {
                        int used = monthly - available;
                        snapshot.PromptCredits = new PromptCredits
                        {
                            Available = available,
                            Monthly = monthly,
                            UsedPercentage = (double)used / monthly,
                            RemainingPercentage = (double)available / monthly
                        };
                    }
                }
            }
            snapshot.PlanType = planType;

            // Parse models
            if (userStatus.TryGetProperty("cascadeModelConfigData", out var cascade) &&
                cascade.TryGetProperty("clientModelConfigs", out var configs) &&
                configs.ValueKind == JsonValueKind.Array)
            {
                foreach (var config in configs.EnumerateArray())
                {
                    var model = ParseLocalModel(config);
                    if (model != null)
                    {
                        snapshot.Models.Add(model);
                    }
                }
            }

            snapshot.Models.Sort((a, b) => a.Label.CompareTo(b.Label));
            return snapshot;
        }

        private ModelQuota? ParseLocalModel(JsonElement m)
        {
            if (!m.TryGetProperty("modelOrAlias", out var moa) || !moa.TryGetProperty("model", out var idProp))
            {
                return null;
            }

            string modelId = idProp.GetString() ?? "unknown";
            string label = m.TryGetProperty("label", out var l) ? (l.GetString() ?? modelId) : modelId;

            double? remainingFraction = null;
            string? resetTime = null;
            double timeUntilResetMs = 0;

            if (m.TryGetProperty("quotaInfo", out var qi))
            {
                if (qi.TryGetProperty("remainingFraction", out var rf) && rf.ValueKind == JsonValueKind.Number)
                {
                    remainingFraction = rf.GetDouble();
                }
                if (qi.TryGetProperty("resetTime", out var rt) && rt.ValueKind == JsonValueKind.String)
                {
                    resetTime = rt.GetString();
                    if (resetTime != null)
                    {
                        timeUntilResetMs = GetTimeUntilResetMs(resetTime);
                    }
                }
            }

            bool hasResetTime = !string.IsNullOrEmpty(resetTime) && timeUntilResetMs > 0;
            double? remainingPercentage = remainingFraction.HasValue 
                ? remainingFraction.Value 
                : (hasResetTime ? 0.0 : (double?)null);

            bool isExhausted = remainingPercentage.HasValue && remainingPercentage.Value == 0.0;

            return new ModelQuota
            {
                Label = label,
                ModelId = modelId,
                RemainingPercentage = remainingPercentage,
                IsExhausted = isExhausted,
                ResetTime = resetTime,
                TimeUntilResetMs = timeUntilResetMs,
                IsAutocompleteOnly = modelId.Contains("gemini-2.5") || label.Contains("Gemini 2.5")
            };
        }

        private QuotaSnapshot ParseGoogleQuota(string assistJson, string modelsJson, string email)
        {
            using var assistDoc = JsonDocument.Parse(assistJson);
            var assistRoot = assistDoc.RootElement;

            string planType = "Standard Plan";
            PromptCredits? credits = null;

            if (assistRoot.TryGetProperty("planInfo", out var pi))
            {
                planType = pi.TryGetProperty("planType", out var pt) ? (pt.GetString() ?? planType) : planType;
                
                if (pi.TryGetProperty("monthlyPromptCredits", out var monthProp) &&
                    assistRoot.TryGetProperty("availablePromptCredits", out var availProp))
                {
                    int monthly = monthProp.GetInt32();
                    int available = availProp.GetInt32();
                    if (monthly > 0)
                    {
                        int used = monthly - available;
                        credits = new PromptCredits
                        {
                            Available = available,
                            Monthly = monthly,
                            UsedPercentage = (double)used / monthly,
                            RemainingPercentage = (double)available / monthly
                        };
                    }
                }
            }

            var snapshot = new QuotaSnapshot
            {
                Timestamp = DateTime.UtcNow.ToString("o"),
                Method = "google",
                Email = email,
                PlanType = planType,
                PromptCredits = credits
            };

            using var modelsDoc = JsonDocument.Parse(modelsJson);
            var modelsRoot = modelsDoc.RootElement;

            if (modelsRoot.TryGetProperty("models", out var modelsMap) && modelsMap.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in modelsMap.EnumerateObject())
                {
                    string modelId = prop.Name;
                    var modelInfo = prop.Value;

                    if (ShouldShowModel(modelId, modelInfo))
                    {
                        var model = ParseGoogleModel(modelId, modelInfo);
                        snapshot.Models.Add(model);
                    }
                }
            }

            snapshot.Models.Sort((a, b) => a.Label.CompareTo(b.Label));
            return snapshot;
        }

        private ModelQuota ParseGoogleModel(string modelId, JsonElement info)
        {
            string label = info.TryGetProperty("displayName", out var dn) 
                ? (dn.GetString() ?? modelId) 
                : (info.TryGetProperty("label", out var l) ? (l.GetString() ?? modelId) : modelId);

            double? remainingFraction = null;
            string? resetTime = null;
            double timeUntilResetMs = 0;

            if (info.TryGetProperty("quotaInfo", out var qi))
            {
                if (qi.TryGetProperty("remainingFraction", out var rf) && rf.ValueKind == JsonValueKind.Number)
                {
                    remainingFraction = rf.GetDouble();
                }
                if (qi.TryGetProperty("resetTime", out var rt) && rt.ValueKind == JsonValueKind.String)
                {
                    resetTime = rt.GetString();
                    if (resetTime != null)
                    {
                        timeUntilResetMs = GetTimeUntilResetMs(resetTime);
                    }
                }
            }

            bool hasResetTime = !string.IsNullOrEmpty(resetTime) && timeUntilResetMs > 0;
            double? remainingPercentage = remainingFraction.HasValue 
                ? remainingFraction.Value 
                : (hasResetTime ? 0.0 : (double?)null);

            bool isExhausted = remainingPercentage.HasValue && remainingPercentage.Value == 0.0;

            return new ModelQuota
            {
                Label = label,
                ModelId = modelId,
                RemainingPercentage = remainingPercentage,
                IsExhausted = isExhausted,
                ResetTime = resetTime,
                TimeUntilResetMs = timeUntilResetMs,
                IsAutocompleteOnly = modelId.Contains("gemini-2.5") || label.Contains("Gemini 2.5")
            };
        }

        private bool ShouldShowModel(string modelId, JsonElement info)
        {
            if (modelId.StartsWith("chat_") || modelId.StartsWith("tab_")) return false;
            if (modelId.Contains("image")) return false;
            if (modelId.StartsWith("rev")) return false;
            if (modelId.Contains("mquery") || modelId.Contains("lite")) return false;
            if (!info.TryGetProperty("quotaInfo", out _)) return false;
            return true;
        }

        private double GetTimeUntilResetMs(string resetTime)
        {
            try
            {
                var date = DateTime.Parse(resetTime, null, System.Globalization.DateTimeStyles.RoundtripKind);
                var diff = date - DateTime.UtcNow;
                return diff.TotalMilliseconds > 0 ? diff.TotalMilliseconds : 0;
            }
            catch
            {
                return 0;
            }
        }

        private static (int pid, string? csrfToken, int? extensionServerPort) DetectLanguageServer()
        {
            try
            {
                using (var searcher = new ManagementObjectSearcher(
                    "SELECT ProcessId, CommandLine FROM Win32_Process WHERE Name LIKE '%antigravity%' OR CommandLine LIKE '%antigravity%'"))
                {
                    foreach (var obj in searcher.Get())
                    {
                        string? commandLine = obj["CommandLine"]?.ToString();
                        string? processIdStr = obj["ProcessId"]?.ToString();
                        if (string.IsNullOrEmpty(commandLine) || string.IsNullOrEmpty(processIdStr)) continue;

                        int pid = int.Parse(processIdStr);
                        string? csrfToken = ExtractArgument(commandLine, "--csrf_token");
                        string? extPortStr = ExtractArgument(commandLine, "--extension_server_port");
                        int? extPort = string.IsNullOrEmpty(extPortStr) ? null : (int?)int.Parse(extPortStr);

                        if (commandLine.ToLower().Contains("language_server") || commandLine.ToLower().Contains("lsp") || commandLine.ToLower().Contains("codeium"))
                        {
                            return (pid, csrfToken, extPort);
                        }
                    }
                }
            }
            catch {}
            return (0, null, null);
        }

        private static string? ExtractArgument(string commandLine, string argName)
        {
            var matchEq = Regex.Match(commandLine, argName + @"=([^\s""']+|""[^""]*""|'[^']*')", RegexOptions.IgnoreCase);
            if (matchEq.Success)
            {
                return matchEq.Groups[1].Value.Trim('"', '\'');
            }

            var matchSpace = Regex.Match(commandLine, argName + @"\s+([^\s""']+|""[^""]*""|'[^']*')", RegexOptions.IgnoreCase);
            if (matchSpace.Success)
            {
                return matchSpace.Groups[1].Value.Trim('"', '\'');
            }

            return null;
        }
    }
}
