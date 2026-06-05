using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AntigravityQuota
{
    public class GlobalConfig
    {
        public string version { get; set; } = "2.0";
        public string? activeAccount { get; set; }
    }

    public class AccountTokenInfo
    {
        public string? accessToken { get; set; }
        public string? refreshToken { get; set; }
        public long expiresAt { get; set; }
        public string? email { get; set; }
        public string? projectId { get; set; } // Cached project ID if resolved
    }

    public class AccountMetadataInfo
    {
        public string? email { get; set; }
        public string? addedAt { get; set; }
        public string? lastUsed { get; set; }
    }

    public class AccountProfile
    {
        public string Email { get; set; } = "";
        public bool IsActive { get; set; }
    }

    public class ConfigService
    {
        private static string GetConfigDir()
        {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            return Path.Combine(appData, "antigravity-usage");
        }

        public static string GetAccountsDir()
        {
            return Path.Combine(GetConfigDir(), "accounts");
        }

        private static string GetAccountDir(string email)
        {
            string safeName = Regex.Replace(email, @"[^a-zA-Z0-9@._-]", "_");
            return Path.Combine(GetAccountsDir(), safeName);
        }

        public static string GetGlobalConfigPath()
        {
            return Path.Combine(GetConfigDir(), "config.json");
        }

        public static GlobalConfig LoadGlobalConfig()
        {
            string path = GetGlobalConfigPath();
            if (!File.Exists(path))
            {
                return new GlobalConfig();
            }

            try
            {
                string json = File.ReadAllText(path);
                return JsonSerializer.Deserialize<GlobalConfig>(json) ?? new GlobalConfig();
            }
            catch
            {
                return new GlobalConfig();
            }
        }

        public static void SaveGlobalConfig(GlobalConfig config)
        {
            string path = GetGlobalConfigPath();
            string? dir = Path.GetDirectoryName(path);
            if (dir != null && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }
            string json = JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(path, json);
        }

        public static AccountTokenInfo? LoadAccountTokens(string email)
        {
            string dir = GetAccountDir(email);
            string path = Path.Combine(dir, "tokens.json");
            if (!File.Exists(path)) return null;

            try
            {
                string json = File.ReadAllText(path);
                return JsonSerializer.Deserialize<AccountTokenInfo>(json);
            }
            catch
            {
                return null;
            }
        }

        public static void SaveAccountTokens(string email, AccountTokenInfo tokens)
        {
            string dir = GetAccountDir(email);
            if (!Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }
            string path = Path.Combine(dir, "tokens.json");
            string json = JsonSerializer.Serialize(tokens, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(path, json);
        }

        public static void SaveAccountMetadata(string email, AccountMetadataInfo metadata)
        {
            string dir = GetAccountDir(email);
            if (!Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }
            string path = Path.Combine(dir, "metadata.json");
            string json = JsonSerializer.Serialize(metadata, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(path, json);
        }

        public static List<AccountProfile> ListAccounts()
        {
            var list = new List<AccountProfile>();
            string accountsDir = GetAccountsDir();
            if (!Directory.Exists(accountsDir)) return list;

            var globalConfig = LoadGlobalConfig();
            string? active = globalConfig.activeAccount;

            foreach (string dir in Directory.GetDirectories(accountsDir))
            {
                string tokensPath = Path.Combine(dir, "tokens.json");
                string metadataPath = Path.Combine(dir, "metadata.json");

                if (File.Exists(tokensPath))
                {
                    string email = Path.GetFileName(dir);
                    // Attempt to load metadata to get exact case email
                    if (File.Exists(metadataPath))
                    {
                        try
                        {
                            var meta = JsonSerializer.Deserialize<AccountMetadataInfo>(File.ReadAllText(metadataPath));
                            if (!string.IsNullOrEmpty(meta?.email))
                            {
                                email = meta.email;
                            }
                        }
                        catch {}
                    }

                    list.Add(new AccountProfile
                    {
                        Email = email,
                        IsActive = (email.Equals(active, StringComparison.OrdinalIgnoreCase))
                    });
                }
            }

            return list;
        }

        public static void SwitchAccount(string email)
        {
            var config = LoadGlobalConfig();
            config.activeAccount = email;
            SaveGlobalConfig(config);

            // Update metadata lastUsed timestamp
            string dir = GetAccountDir(email);
            string metaPath = Path.Combine(dir, "metadata.json");
            if (File.Exists(metaPath))
            {
                try
                {
                    var meta = JsonSerializer.Deserialize<AccountMetadataInfo>(File.ReadAllText(metaPath)) ?? new AccountMetadataInfo();
                    meta.email = email;
                    meta.lastUsed = DateTime.UtcNow.ToString("o");
                    SaveAccountMetadata(email, meta);
                }
                catch {}
            }
        }

        public static void RemoveAccount(string email)
        {
            string dir = GetAccountDir(email);
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, true);
            }

            var config = LoadGlobalConfig();
            if (config.activeAccount == email)
            {
                config.activeAccount = null;
                var remaining = ListAccounts();
                if (remaining.Count > 0)
                {
                    config.activeAccount = remaining[0].Email;
                }
                SaveGlobalConfig(config);
            }
        }
    }
}
