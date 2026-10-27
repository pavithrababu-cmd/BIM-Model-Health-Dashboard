using System;
using System.Collections.Generic;
using System.Linq;

namespace BIMModelHealthDashboard.Models
{
    // A frozen copy of the model's health at one moment
    // (taken just before Fix All Issues runs).
    public sealed class HealthSnapshot
    {
        public string ModelKey { get; private set; } = string.Empty;
        public DateTime TakenAt { get; private set; }
        public int? HealthScore { get; private set; }
        public string HealthStatus { get; private set; } = string.Empty;
        public int TotalIssues { get; private set; }

        // CheckId -> issue count / status text at that moment.
        public Dictionary<string, int> IssuesByCheck { get; } = new Dictionary<string, int>();
        public Dictionary<string, string> StatusByCheck { get; } = new Dictionary<string, string>();

        public List<HealthIndicator> ProjectReadiness { get; } = new List<HealthIndicator>();
        public List<HealthIndicator> ModelOrganization { get; } = new List<HealthIndicator>();
        public List<HealthIndicator> Dependencies { get; } = new List<HealthIndicator>();

        public static HealthSnapshot Capture(ValidationReport report, DashboardSummary? summary)
        {
            var snapshot = new HealthSnapshot
            {
                ModelKey = report.ModelKey,
                TakenAt = DateTime.Now,
                HealthScore = report.HealthScore,
                HealthStatus = report.HealthStatus,
                TotalIssues = report.TotalIssues
            };

            foreach (CheckResult result in report.Results)
            {
                snapshot.IssuesByCheck[result.CheckId] = result.IssueCount;
                snapshot.StatusByCheck[result.CheckId] = result.StatusText;
            }

            if (summary != null && summary.ModelKey == report.ModelKey)
            {
                snapshot.ProjectReadiness.AddRange(summary.ProjectReadiness);
                snapshot.ModelOrganization.AddRange(summary.ModelOrganization);
                snapshot.Dependencies.AddRange(summary.Dependencies);
            }

            return snapshot;
        }

        public static string? ValueOf(IEnumerable<HealthIndicator> indicators, string label) =>
            indicators.FirstOrDefault(i => i.Label == label)?.Value;
    }
}
