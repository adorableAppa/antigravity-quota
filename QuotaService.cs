using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Management;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
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
            if (method == "local")
            {
                return await FetchLocalQuotaAsync();
            }

            if (method == "auto")
            {
                try
                {
                    var lsp = await FindActiveLanguageServerAsync();
                    if (lsp.pid != 0 && !string.IsNullOrEmpty(lsp.baseUrl) && lsp.extensionServerPort.HasValue)
                    {
                        WriteDebugLog($"[FetchQuotaAsync] Auto-detect: Found active LSP (PID {lsp.pid}) on port {lsp.extensionServerPort}.");
                        return await FetchLocalQuotaAsync(lsp.pid, lsp.csrfToken, lsp.extensionServerPort.Value, lsp.baseUrl);
                    }
                }
                catch (Exception ex)
                {
                    WriteDebugLog($"[FetchQuotaAsync] Auto-detect: Local LSP fetch failed, falling back to Google Cloud. Exception: {ex.Message}");
                }
            }

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
            var lsp = await FindActiveLanguageServerAsync();
            if (lsp.pid == 0 || string.IsNullOrEmpty(lsp.baseUrl) || !lsp.extensionServerPort.HasValue)
            {
                throw new Exception("Antigravity Language Server process not found or not responding. Verify it is running in your IDE.");
            }
            return await FetchLocalQuotaAsync(lsp.pid, lsp.csrfToken, lsp.extensionServerPort.Value, lsp.baseUrl);
        }

        private async Task<QuotaSnapshot> FetchLocalQuotaAsync(int pid, string? csrfToken, int port, string baseUrl)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, baseUrl + "/exa.language_server_pb.LanguageServerService/GetUserStatus");
            request.Headers.Add("Connect-Protocol-Version", "1");
            if (!string.IsNullOrEmpty(csrfToken))
            {
                request.Headers.Add("X-Codeium-Csrf-Token", csrfToken);
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
                WriteDebugLog($"[LoadCodeAssist] HTTP {(int)resAssist.StatusCode} {resAssist.StatusCode}");
                throw new Exception($"LoadCodeAssist failed: {resAssist.StatusCode}");
            }

            string assistJson = await resAssist.Content.ReadAsStringAsync();
            WriteDebugLog($"[LoadCodeAssist] Response:\n{FormatJson(assistJson)}");

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
            WriteDebugLog($"[LoadCodeAssist] Extracted projectId: {projectId ?? "(null)"}");

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
                WriteDebugLog($"[FetchAvailableModels] HTTP {(int)resModels.StatusCode} {resModels.StatusCode}");
                throw new Exception($"FetchAvailableModels failed: {resModels.StatusCode}");
            }

            string modelsJson = await resModels.Content.ReadAsStringAsync();
            WriteDebugLog($"[FetchAvailableModels] Response:\n{FormatJson(modelsJson)}");

            return ParseGoogleQuota(assistJson, modelsJson, email);
        }

        private async Task<string?> ProbeLocalServerAsync(int port, string? csrfToken)
        {
            string[] urls = { $"http://127.0.0.1:{port}", $"https://127.0.0.1:{port}" };
            foreach (string url in urls)
            {
                try
                {
                    WriteDebugLog($"[ProbeLocalServerAsync] Probing: {url} with csrfToken={(string.IsNullOrEmpty(csrfToken) ? "null" : csrfToken.Substring(0, Math.Min(csrfToken.Length, 6)) + "...")}");
                    var req = new HttpRequestMessage(HttpMethod.Post, url + "/exa.language_server_pb.LanguageServerService/GetUserStatus");
                    req.Headers.Add("Connect-Protocol-Version", "1");
                    if (!string.IsNullOrEmpty(csrfToken))
                    {
                        req.Headers.Add("X-Codeium-Csrf-Token", csrfToken);
                    }
                    req.Content = new StringContent("{}", Encoding.UTF8, "application/json");

                    var cts = new System.Threading.CancellationTokenSource(400);
                    var res = await _localHttpClient.SendAsync(req, cts.Token);
                    WriteDebugLog($"[ProbeLocalServerAsync] Response from {url}: HTTP {(int)res.StatusCode} {res.StatusCode}");
                    if (res.IsSuccessStatusCode) return url;
                }
                catch (Exception ex)
                {
                    WriteDebugLog($"[ProbeLocalServerAsync] Error probing {url}: {ex.Message}");
                }
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

            string rawModelId = idProp.GetString() ?? "unknown";
            string label = m.TryGetProperty("label", out var l) ? (l.GetString() ?? rawModelId) : rawModelId;
            string modelId = CleanModelId(rawModelId, label);

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

            bool foundPlanInfo = assistRoot.TryGetProperty("planInfo", out var pi);
            WriteDebugLog($"[ParseGoogleQuota] planInfo found: {foundPlanInfo}");

            if (foundPlanInfo)
            {
                planType = pi.TryGetProperty("planType", out var pt) ? (pt.GetString() ?? planType) : planType;
                WriteDebugLog($"[ParseGoogleQuota] planType: {planType}");
                
                bool hasMonthly = pi.TryGetProperty("monthlyPromptCredits", out var monthProp);
                bool hasAvailable = assistRoot.TryGetProperty("availablePromptCredits", out var availProp);
                WriteDebugLog($"[ParseGoogleQuota] monthlyPromptCredits found: {hasMonthly}, availablePromptCredits found: {hasAvailable}");

                if (hasMonthly && hasAvailable)
                {
                    int monthly = monthProp.GetInt32();
                    int available = availProp.GetInt32();
                    WriteDebugLog($"[ParseGoogleQuota] monthly={monthly}, available={available}");
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

            bool hasModelsMap = modelsRoot.TryGetProperty("models", out var modelsMap) && modelsMap.ValueKind == JsonValueKind.Object;
            WriteDebugLog($"[ParseGoogleQuota] 'models' map found: {hasModelsMap}");
            if (!hasModelsMap)
            {
                // Log all top-level keys so we can see if the structure changed
                var topKeys = new List<string>();
                foreach (var p in modelsRoot.EnumerateObject()) topKeys.Add(p.Name);
                WriteDebugLog($"[ParseGoogleQuota] Top-level keys in FetchAvailableModels response: [{string.Join(", ", topKeys)}]");
            }

            if (hasModelsMap)
            {
                foreach (var prop in modelsMap.EnumerateObject())
                {
                    string modelId = prop.Name;
                    var modelInfo = prop.Value;

                    if (ShouldShowModel(modelId, modelInfo))
                    {
                        var model = ParseGoogleModel(modelId, modelInfo);
                        WriteDebugLog($"[ParseGoogleQuota] Model '{modelId}': remainingPct={model.RemainingPercentage?.ToString() ?? "null"}, exhausted={model.IsExhausted}, resetTime={model.ResetTime ?? "null"}");
                        snapshot.Models.Add(model);
                    }
                }
            }

            WriteDebugLog($"[ParseGoogleQuota] Total models tracked: {snapshot.Models.Count}");
            snapshot.Models.Sort((a, b) => a.Label.CompareTo(b.Label));
            return snapshot;
        }

        private ModelQuota ParseGoogleModel(string rawModelId, JsonElement info)
        {
            string label = info.TryGetProperty("displayName", out var dn) 
                ? (dn.GetString() ?? rawModelId) 
                : (info.TryGetProperty("label", out var l) ? (l.GetString() ?? rawModelId) : rawModelId);
            string modelId = CleanModelId(rawModelId, label);

            double? remainingFraction = null;
            string? resetTime = null;
            double timeUntilResetMs = 0;

            bool hasQuotaInfo = info.TryGetProperty("quotaInfo", out var qi);
            if (hasQuotaInfo)
            {
                // Log all keys inside quotaInfo so we can see if the field names changed
                var quotaKeys = new List<string>();
                foreach (var qp in qi.EnumerateObject()) quotaKeys.Add($"{qp.Name}={qp.Value}");
                WriteDebugLog($"[ParseGoogleModel] '{rawModelId}' quotaInfo keys: [{string.Join(", ", quotaKeys)}]");

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
            else
            {
                WriteDebugLog($"[ParseGoogleModel] '{rawModelId}' has NO quotaInfo property!");
            }

            WriteDebugLog($"[ParseGoogleModel] '{rawModelId}' remainingFraction={remainingFraction?.ToString() ?? "null"}, resetTime={resetTime ?? "null"}");

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

        [DllImport("iphlpapi.dll", SetLastError = true)]
        private static extern uint GetExtendedTcpTable(
            IntPtr pTcpTable,
            ref int pdwSize,
            bool bOrder,
            int ulAf,
            TcpTableClass tableClass,
            uint reserved = 0);

        private enum TcpTableClass
        {
            TcpTableBasicListener,
            TcpTableBasicConnections,
            TcpTableBasicAll,
            TcpTableOwnerPidListener,
            TcpTableOwnerPidConnections,
            TcpTableOwnerPidAll,
            TcpTableOwnerModuleListener,
            TcpTableOwnerModuleConnections,
            TcpTableOwnerModuleAll
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MIB_TCPROW_OWNER_PID
        {
            public uint dwState;
            public uint dwLocalAddr;
            public uint dwLocalPort;
            public uint dwRemoteAddr;
            public uint dwRemotePort;
            public uint dwOwningPid;
        }

        private const int AF_INET = 2;

        private static List<int> GetListeningPortsForPid(int pid)
        {
            var ports = new List<int>();
            int bufferSize = 0;
            
            // Get size needed
            uint ret = GetExtendedTcpTable(IntPtr.Zero, ref bufferSize, true, AF_INET, TcpTableClass.TcpTableOwnerPidAll);
            IntPtr buffer = Marshal.AllocHGlobal(bufferSize);
            try
            {
                ret = GetExtendedTcpTable(buffer, ref bufferSize, true, AF_INET, TcpTableClass.TcpTableOwnerPidAll);
                if (ret == 0)
                {
                    int numEntries = Marshal.ReadInt32(buffer);
                    IntPtr rowPtr = IntPtr.Add(buffer, 4);
                    for (int i = 0; i < numEntries; i++)
                    {
                        var row = Marshal.PtrToStructure<MIB_TCPROW_OWNER_PID>(rowPtr);
                        // dwState 2 = MIB_TCP_STATE_LISTEN (listening)
                        if (row.dwOwningPid == pid && row.dwState == 2)
                        {
                            int port = (int)(((row.dwLocalPort & 0x0000FF00) >> 8) | ((row.dwLocalPort & 0x000000FF) << 8));
                            if (!ports.Contains(port))
                            {
                                ports.Add(port);
                            }
                        }
                        rowPtr = IntPtr.Add(rowPtr, Marshal.SizeOf<MIB_TCPROW_OWNER_PID>());
                    }
                }
            }
            catch (Exception ex)
            {
                WriteDebugLog($"[GetListeningPortsForPid] PInvoke Exception: {ex.Message}");
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }

            // Fallback if PInvoke returned nothing
            if (ports.Count == 0)
            {
                ports = GetListeningPortsForPidFallback(pid);
            }

            return ports;
        }

        private static List<int> GetListeningPortsForPidFallback(int pid)
        {
            var ports = new List<int>();
            try
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = "netstat.exe",
                    Arguments = "-ano",
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using (var process = Process.Start(startInfo))
                {
                    if (process != null)
                    {
                        using (var reader = process.StandardOutput)
                        {
                            string? line;
                            while ((line = reader.ReadLine()) != null)
                            {
                                line = line.Trim();
                                if (string.IsNullOrEmpty(line)) continue;
                                
                                if (line.EndsWith(pid.ToString()))
                                {
                                    var parts = line.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                                    if (parts.Length >= 4)
                                    {
                                        string pidPart = parts[parts.Length - 1];
                                        if (pidPart == pid.ToString())
                                        {
                                            var localAddress = parts[1];
                                            int colonIndex = localAddress.LastIndexOf(':');
                                            if (colonIndex >= 0 && colonIndex < localAddress.Length - 1)
                                            {
                                                var portStr = localAddress.Substring(colonIndex + 1);
                                                if (int.TryParse(portStr, out int port) && !ports.Contains(port))
                                                {
                                                    ports.Add(port);
                                                }
                                            }
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                WriteDebugLog($"[GetListeningPortsForPidFallback] Exception: {ex.Message}");
            }
            return ports;
        }

        private class LanguageServerCandidate
        {
            public int Pid { get; set; }
            public string? LsCsrfToken { get; set; }
            public string? ExtCsrfToken { get; set; }
            public int? ExtPort { get; set; }
        }

        private async Task<(int pid, string? csrfToken, int? extensionServerPort, string? baseUrl)> FindActiveLanguageServerAsync()
        {
            var candidates = DetectLanguageServers();
            foreach (var candidate in candidates)
            {
                var ports = GetListeningPortsForPid(candidate.Pid);
                WriteDebugLog($"[FindActiveLanguageServerAsync] PID {candidate.Pid} is listening on ports: [{string.Join(", ", ports)}]");
                
                foreach (int port in ports)
                {
                    if (candidate.ExtPort.HasValue && port == candidate.ExtPort.Value)
                    {
                        continue;
                    }
                    
                    string? baseUrl = await ProbeLocalServerAsync(port, candidate.LsCsrfToken);
                    if (!string.IsNullOrEmpty(baseUrl))
                    {
                        WriteDebugLog($"[FindActiveLanguageServerAsync] Successfully connected to LSP on {baseUrl} (PID {candidate.Pid})");
                        return (candidate.Pid, candidate.LsCsrfToken, port, baseUrl);
                    }
                }
            }
            return (0, null, null, null);
        }

        private static List<LanguageServerCandidate> DetectLanguageServers()
        {
            var list = new List<LanguageServerCandidate>();
            try
            {
                WriteDebugLog("[DetectLanguageServers] Querying processes via WMI...");
                int count = 0;
                using (var searcher = new ManagementObjectSearcher(
                    "SELECT ProcessId, Name, CommandLine FROM Win32_Process WHERE Name LIKE '%antigravity%' OR CommandLine LIKE '%antigravity%'"))
                {
                    foreach (var obj in searcher.Get())
                    {
                        count++;
                        string? commandLine = obj["CommandLine"]?.ToString();
                        string? processIdStr = obj["ProcessId"]?.ToString();
                        string? name = obj["Name"]?.ToString();
                        if (string.IsNullOrEmpty(commandLine) || string.IsNullOrEmpty(processIdStr)) continue;

                        int pid = int.Parse(processIdStr);
                        string? lsCsrfToken = ExtractArgument(commandLine, "--csrf_token");
                        string? extCsrfToken = ExtractArgument(commandLine, "--extension_server_csrf_token");
                        string? extPortStr = ExtractArgument(commandLine, "--extension_server_port");
                        int? extPort = string.IsNullOrEmpty(extPortStr) ? null : (int?)int.Parse(extPortStr);

                        WriteDebugLog($"[DetectLanguageServers] Match: PID={pid}, Name={name ?? "null"}, extPort={extPort?.ToString() ?? "null"}, CommandLine={commandLine}");

                        if (commandLine.ToLower().Contains("language_server") || commandLine.ToLower().Contains("lsp") || commandLine.ToLower().Contains("codeium"))
                        {
                            WriteDebugLog($"[DetectLanguageServers] Candidate: PID={pid}, extPort={extPort?.ToString() ?? "null"}");
                            list.Add(new LanguageServerCandidate
                            {
                                Pid = pid,
                                LsCsrfToken = lsCsrfToken,
                                ExtCsrfToken = extCsrfToken,
                                ExtPort = extPort
                            });
                        }
                    }
                }
                WriteDebugLog($"[DetectLanguageServers] Query finished, processed {count} matching processes. Found {list.Count} candidates.");
            }
            catch (Exception ex)
            {
                WriteDebugLog($"[DetectLanguageServers] WMI query exception: {ex.Message}\n{ex.StackTrace}");
            }
            return list;
        }

        // Keep DetectLanguageServer for potential other usages or status updates
        private static (int pid, string? csrfToken, int? extensionServerPort) DetectLanguageServer()
        {
            var list = DetectLanguageServers();
            if (list.Count > 0)
            {
                return (list[0].Pid, list[0].LsCsrfToken, list[0].ExtPort);
            }
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

        // ── Debug Logging ──────────────────────────────────────────────────

        private static readonly string DebugLogPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "antigravity-usage", "debug.log");

        private static void WriteDebugLog(string message)
        {
            try
            {
                string? dir = Path.GetDirectoryName(DebugLogPath);
                if (dir != null && !Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                string timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
                string line = $"[{timestamp}] {message}{Environment.NewLine}";
                File.AppendAllText(DebugLogPath, line);
            }
            catch { /* Don't let logging break the app */ }
        }

        private static string FormatJson(string json)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                return JsonSerializer.Serialize(doc.RootElement, new JsonSerializerOptions { WriteIndented = true });
            }
            catch
            {
                return json;
            }
        }

        private static string CleanModelId(string modelId, string label)
        {
            if (string.IsNullOrEmpty(modelId)) return "unknown";
            
            // If it's a placeholder or raw uppercase enum ID, derive from label
            if (modelId.StartsWith("MODEL_PLACEHOLDER_") || modelId.StartsWith("MODEL_"))
            {
                if (string.IsNullOrEmpty(label)) return modelId.ToLowerInvariant().Replace("_", "-");
                
                // Convert label to kebab-case
                string clean = label.ToLowerInvariant();
                clean = Regex.Replace(clean, @"[^a-z0-9]+", "-");
                return clean.Trim('-');
            }
            
            return modelId;
        }
    }
}
