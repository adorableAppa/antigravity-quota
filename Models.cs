using System;
using System.Collections.Generic;

namespace AntigravityQuota
{
    public class ModelQuota
    {
        public string Label { get; set; } = "";
        public string ModelId { get; set; } = "";
        public double? RemainingPercentage { get; set; }
        public bool IsExhausted { get; set; }
        public string? ResetTime { get; set; }
        public double TimeUntilResetMs { get; set; }
        public bool IsAutocompleteOnly { get; set; }
    }

    public class PromptCredits
    {
        public int Available { get; set; }
        public int Monthly { get; set; }
        public double UsedPercentage { get; set; }
        public double RemainingPercentage { get; set; }
    }

    public class QuotaSnapshot
    {
        public string Timestamp { get; set; } = "";
        public string Method { get; set; } = ""; // "local" or "google"
        public string Email { get; set; } = "";
        public string PlanType { get; set; } = "Standard Plan";
        public PromptCredits? PromptCredits { get; set; }
        public List<ModelQuota> Models { get; set; } = new();
    }
}
