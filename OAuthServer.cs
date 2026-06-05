using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace AntigravityQuota
{
    public class OAuthServer
    {
        private const int Port = 4500;
        private static string ClientId => OAuthConfig.ClientId;
        private static string ClientSecret => OAuthConfig.ClientSecret;
        private const string AuthUrl = "https://accounts.google.com/o/oauth2/v2/auth";
        private const string TokenUrl = "https://oauth2.googleapis.com/token";

        private HttpListener? _listener;
        private bool _isRunning;
        private readonly HashSet<string> _oauthStates = new();
        private readonly HttpClient _httpClient = new();
        private readonly Action _onLoginSuccess;

        public OAuthServer(Action onLoginSuccess)
        {
            _onLoginSuccess = onLoginSuccess;
        }

        public void Start()
        {
            if (_isRunning) return;

            try
            {
                _listener = new HttpListener();
                _listener.Prefixes.Add($"http://127.0.0.1:{Port}/");
                _listener.Start();
                _isRunning = true;
                Task.Run(ListenLoop);
            }
            catch (HttpListenerException ex)
            {
                Debug.WriteLine($"OAuth listener failed to start on port {Port}: {ex.Message}");
                // App continues without the listener — user can retry later
            }
        }

        public void Stop()
        {
            _isRunning = false;
            _listener?.Stop();
            _listener?.Close();
        }

        public void TriggerLoginFlow()
        {
            string state = Guid.NewGuid().ToString("N");
            lock (_oauthStates)
            {
                _oauthStates.Add(state);
            }

            string redirectUri = $"http://127.0.0.1:{Port}/callback";
            string scopes = "https://www.googleapis.com/auth/cloud-platform https://www.googleapis.com/auth/userinfo.email";

            string authParams = $"?client_id={Uri.EscapeDataString(ClientId)}" +
                               $"&redirect_uri={Uri.EscapeDataString(redirectUri)}" +
                               $"&response_type=code" +
                               $"&scope={Uri.EscapeDataString(scopes)}" +
                               $"&access_type=offline" +
                               $"&prompt=consent" +
                               $"&state={Uri.EscapeDataString(state)}";

            string loginUrl = AuthUrl + authParams;

            try
            {
                Process.Start(new ProcessStartInfo(loginUrl) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                Debug.WriteLine("Failed to open browser: " + ex.Message);
            }
        }

        private async Task ListenLoop()
        {
            while (_isRunning && _listener != null)
            {
                try
                {
                    var context = await _listener.GetContextAsync();
                    _ = Task.Run(() => HandleRequestAsync(context));
                }
                catch
                {
                    // Listener stopped or encountered error
                    break;
                }
            }
        }

        private async Task HandleRequestAsync(HttpListenerContext context)
        {
            var request = context.Request;
            var response = context.Response;

            string path = request.Url?.AbsolutePath ?? "/";

            if (path == "/login")
            {
                TriggerLoginFlow();
                response.Redirect("/");
                response.Close();
                return;
            }

            if (path == "/callback")
            {
                await HandleCallbackAsync(context);
                return;
            }

            // Fallback: 404 or redirect back
            response.StatusCode = 404;
            response.Close();
        }

        private async Task HandleCallbackAsync(HttpListenerContext context)
        {
            var request = context.Request;
            var response = context.Response;

            string? code = request.QueryString["code"];
            string? state = request.QueryString["state"];
            string? error = request.QueryString["error"];

            if (!string.IsNullOrEmpty(error))
            {
                SendHtmlResponse(response, $"<h1>Authentication Failed</h1><p>{error}</p>", true);
                return;
            }

            bool validState;
            lock (_oauthStates)
            {
                validState = state != null && _oauthStates.Remove(state);
            }

            if (!validState)
            {
                response.StatusCode = (int)HttpStatusCode.BadRequest;
                byte[] buffer = Encoding.UTF8.GetBytes("State mismatch or session expired");
                response.ContentLength64 = buffer.Length;
                response.OutputStream.Write(buffer, 0, buffer.Length);
                response.Close();
                return;
            }

            string redirectUri = $"http://127.0.0.1:{Port}/callback";

            try
            {
                // 1. Exchange authorization code for tokens
                var tokenRequestParams = new Dictionary<string, string>
                {
                    { "code", code ?? "" },
                    { "client_id", ClientId },
                    { "client_secret", ClientSecret },
                    { "redirect_uri", redirectUri },
                    { "grant_type", "authorization_code" }
                };

                var tokenResponse = await _httpClient.PostAsync(TokenUrl, new FormUrlEncodedContent(tokenRequestParams));
                if (!tokenResponse.IsSuccessStatusCode)
                {
                    string errText = await tokenResponse.Content.ReadAsStringAsync();
                    throw new Exception($"Token exchange failed: {errText}");
                }

                string tokenJson = await tokenResponse.Content.ReadAsStringAsync();
                using var tokenDoc = JsonDocument.Parse(tokenJson);
                var tokenRoot = tokenDoc.RootElement;

                string accessToken = tokenRoot.GetProperty("access_token").GetString() ?? "";
                string refreshToken = tokenRoot.TryGetProperty("refresh_token", out var rt) ? (rt.GetString() ?? "") : "";
                int expiresIn = tokenRoot.GetProperty("expires_in").GetInt32();
                long expiresAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + (expiresIn * 1000);

                // 2. Fetch user email
                _httpClient.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
                var userInfoResponse = await _httpClient.GetAsync("https://www.googleapis.com/oauth2/v2/userinfo");
                _httpClient.DefaultRequestHeaders.Authorization = null;

                if (!userInfoResponse.IsSuccessStatusCode)
                {
                    throw new Exception("Failed to retrieve user info");
                }

                string userInfoJson = await userInfoResponse.Content.ReadAsStringAsync();
                using var userInfoDoc = JsonDocument.Parse(userInfoJson);
                string email = userInfoDoc.RootElement.GetProperty("email").GetString() ?? "";

                if (string.IsNullOrEmpty(email))
                {
                    throw new Exception("No email found in user info response");
                }

                // 3. Save tokens and metadata using ConfigService
                var storedTokens = new AccountTokenInfo
                {
                    accessToken = accessToken,
                    refreshToken = refreshToken,
                    expiresAt = expiresAt,
                    email = email
                };

                var storedMetadata = new AccountMetadataInfo
                {
                    email = email,
                    addedAt = DateTime.UtcNow.ToString("o"),
                    lastUsed = DateTime.UtcNow.ToString("o")
                };

                ConfigService.SaveAccountTokens(email, storedTokens);
                ConfigService.SaveAccountMetadata(email, storedMetadata);

                // 4. Update config activeAccount
                var config = ConfigService.LoadGlobalConfig();
                config.activeAccount = email;
                ConfigService.SaveGlobalConfig(config);

                // 5. Success page redirect
                string successHtml = $@"
      <html>
        <body style=""font-family: system-ui; background: #0b0914; color: #fff; padding: 40px; text-align: center; display: flex; flex-direction: column; justify-content: center; align-items: center; height: 80vh;"">
          <div style=""background: rgba(255, 255, 255, 0.05); padding: 40px; border-radius: 16px; border: 1px solid rgba(255, 255, 255, 0.1); backdrop-filter: blur(10px); max-width: 400px; box-shadow: 0 8px 32px rgba(0,0,0,0.5);"">
            <h1 style=""color: #10b981; margin-bottom: 16px;"">Success!</h1>
            <p style=""font-size: 1.1rem; line-height: 1.6; margin-bottom: 24px;"">You have logged in successfully as <strong>{email}</strong>.</p>
            <p style=""color: #a78bfa; font-size: 0.9rem;"">You can now close this browser tab and return to the application.</p>
            <button onclick=""window.close()"" style=""margin-top: 24px; background: linear-gradient(135deg, #8b5cf6, #6366f1); border: none; padding: 12px 24px; border-radius: 8px; color: #fff; font-weight: bold; cursor: pointer; transition: opacity 0.2s;"">Close Window</button>
          </div>
        </body>
      </html>";

                SendHtmlResponse(response, successHtml, false);
                _onLoginSuccess?.Invoke();
            }
            catch (Exception ex)
            {
                SendHtmlResponse(response, $"<h1>Authentication Error</h1><p>{ex.Message}</p>", true);
            }
        }

        private void SendHtmlResponse(HttpListenerResponse response, string html, bool isError)
        {
            response.ContentType = "text/html";
            response.StatusCode = isError ? 500 : 200;

            byte[] buffer = Encoding.UTF8.GetBytes(html);
            response.ContentLength64 = buffer.Length;
            response.OutputStream.Write(buffer, 0, buffer.Length);
            response.Close();
        }
    }
}
