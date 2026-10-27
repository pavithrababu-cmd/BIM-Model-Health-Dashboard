using System.Collections.Generic;
using System.Linq;

namespace BIMModelHealthDashboard.Models
{
    public class CheckResult
    {
        public string CheckId { get; set; } = string.Empty;
        public string CheckName { get; set; } = string.Empty;
        public string Group { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;

        // Position in the check registry; keeps results in a stable order.
        public int Order { get; set; }

        public CheckStatus Status { get; set; } = CheckStatus.NotRun;
        public string Summary { get; set; } = string.Empty;

        // Number of elements/items examined by the check.
        public int CheckedCount { get; set; }
        public long DurationMs { get; set; }

        public List<ValidationIssue> Issues { get; } = new List<ValidationIssue>();

        // Problems found in the rules file while running this check.
        public List<string> ConfigurationWarnings { get; } = new List<string>();

        public int IssueCount => Issues.Count;

        public IssueSeverity? WorstSeverity =>
            Issues.Count == 0
                ? (IssueSeverity?)null
                : Issues.Max(i => i.Severity);

        public string WorstSeverityText =>
            WorstSeverity?.ToString() ?? "—";

        public string StatusText
        {
            get
            {
                switch (Status)
                {
                    case CheckStatus.NotRun: return "Not run";
                    case CheckStatus.Passed: return "Passed";
                    case CheckStatus.Failed: return "Failed";
                    case CheckStatus.NotConfigured: return "Rules needed";
                    case CheckStatus.NotApplicable: return "Not applicable";
                    case CheckStatus.Error: return "Error";
                    default: return Status.ToString();
                }
            }
        }

        public ValidationIssue AddIssue(
            IssueSeverity severity,
            string elementName,
            string category,
            string message,
            IEnumerable<long>? elementIds = null)
        {
            var issue = new ValidationIssue
            {
                CheckId = CheckId,
                CheckName = CheckName,
                Group = Group,
                Severity = severity,
                ElementName = elementName ?? string.Empty,
                Category = category ?? string.Empty,
                Message = message ?? string.Empty,
                ElementIds = elementIds?.Distinct().ToList() ?? new List<long>()
            };

            Issues.Add(issue);
            return issue;
        }

        public void MarkNotConfigured(string summary)
        {
            Status = CheckStatus.NotConfigured;
            Summary = summary;
        }

        public void MarkNotApplicable(string summary)
        {
            Status = CheckStatus.NotApplicable;
            Summary = summary;
        }
    }
}
