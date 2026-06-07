using System;
using System.IO;
using Microsoft.Win32;

namespace AntigravityQuota
{
    public static class AutostartService
    {
        private const string RegistryRunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string AppKeyName = "AntigravityQuota";

        public static void SetAutostart(bool enable)
        {
            try
            {
                using (RegistryKey? key = Registry.CurrentUser.OpenSubKey(RegistryRunKey, true))
                {
                    if (key == null) return;

                    if (enable)
                    {
                        string processPath = Environment.ProcessPath ?? "";
                        if (!string.IsNullOrEmpty(processPath) && File.Exists(processPath))
                        {
                            // Wrap path in quotes to handle spaces in directory names
                            key.SetValue(AppKeyName, $"\"{processPath}\"");
                        }
                    }
                    else
                    {
                        key.DeleteValue(AppKeyName, false);
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to set autostart: {ex.Message}");
            }
        }

        public static void VerifyAndUpdateAutostart()
        {
            try
            {
                var config = ConfigService.LoadGlobalConfig();
                if (!config.startWithWindows) return;

                string processPath = Environment.ProcessPath ?? "";
                if (string.IsNullOrEmpty(processPath) || !File.Exists(processPath)) return;

                string expectedValue = $"\"{processPath}\"";

                using (RegistryKey? key = Registry.CurrentUser.OpenSubKey(RegistryRunKey, true))
                {
                    if (key == null) return;

                    object? existingValue = key.GetValue(AppKeyName);
                    if (existingValue == null || existingValue.ToString() != expectedValue)
                    {
                        // Path is missing or mismatched (user moved the app); update it!
                        key.SetValue(AppKeyName, expectedValue);
                        System.Diagnostics.Debug.WriteLine("Autostart path healed successfully.");
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to verify/update autostart: {ex.Message}");
            }
        }
    }
}
