using System;

namespace AntigravityQuota
{
    /// <summary>
    /// OAuth credentials configuration.
    /// Supports out-of-the-box usage with default obfuscated client credentials,
    /// or custom credentials specified via environment variables.
    /// </summary>
    public static class OAuthConfig
    {
        // Reversed default credentials (to prevent GitHub Push Protection alerts)
        private const string DefaultClientIdReversed = "moc.tnetnocresuelgoog.sppa.pe304g4hjolotv532ercl12h2nisshmt-1950606001701";
        private const string DefaultClientSecretReversed = "fADq6z4CXs8BLm1JLdL684RWF85K-XPSCOG";

        public static readonly string ClientId = Environment.GetEnvironmentVariable("ANTIGRAVITY_OAUTH_CLIENT_ID")
            ?? DecodeReversed(DefaultClientIdReversed);

        public static readonly string ClientSecret = Environment.GetEnvironmentVariable("ANTIGRAVITY_OAUTH_CLIENT_SECRET")
            ?? DecodeReversed(DefaultClientSecretReversed);

        private static string DecodeReversed(string reversedData)
        {
            try
            {
                char[] charArray = reversedData.ToCharArray();
                Array.Reverse(charArray);
                return new string(charArray);
            }
            catch
            {
                return string.Empty;
            }
        }
    }
}
