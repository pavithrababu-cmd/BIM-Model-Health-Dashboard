using System;
using System.Collections.Generic;
using System.Linq;

namespace BIMModelHealthDashboard.Models
{
    // All check results for one model. Results from separate checker runs
    // (e.g. Element Checker, then Wall Checker) are merged into one report.
    public class ValidationReport
    {
        public string ModelKey { get; set; } = string.Empty;
        public string ModelTitle { get; set; } = string.Empty;
        public string ModelPath { get; set; } = string.Empty;

        public DateTime ScannedAt { get; set; } = DateTime.Now;

        public string RulesSource { get; set; } = string.Empty;
        public string? RulesWarning { get; set; }

        public List<CheckResult> Results { get; } = new List<CheckResult>();

        public int? HealthScore { get; set; }
        public string HealthStatus { get; set; } = "Checks not yet run";
        public bool IsComplete { get; set; }

        public int TotalIssues => Results.Sum(r => r.IssueCount);

        public CheckResult? Find(string checkId) =>
            Results.FirstOrDefault(r => r.CheckId == checkId);

        public void Merge(IEnumerable<CheckResult> newResults)
        {
            foreach (CheckResult result in newResults)
            {
                Results.RemoveAll(r => r.CheckId == result.CheckId);
                Results.Add(result);
            }

            Results.Sort((a, b) => a.Order.CompareTo(b.Order));
        }
    }
}
