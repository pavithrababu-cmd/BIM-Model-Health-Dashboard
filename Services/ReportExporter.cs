using BIMModelHealthDashboard.Models;
using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;

namespace BIMModelHealthDashboard.Services
{
    public static class ReportExporter
    {
        private const int MaxIssuesPerCheckInHtml = 500;

        public static void Export(ValidationReport report, string path)
        {
            string extension = Path.GetExtension(path).ToLowerInvariant();

            string content = extension == ".csv"
                ? BuildCsv(report)
                : BuildHtml(report);

            File.WriteAllText(path, content, new UTF8Encoding(true));
        }

        public static string BuildCsv(ValidationReport report)
        {
            var sb = new StringBuilder();
            sb.AppendLine("Group,Check,Status,Severity,Element,Category,Issue,ElementIds");

            foreach (CheckResult result in report.Results)
            {
                if (result.Issues.Count == 0)
                {
                    sb.AppendLine(string.Join(",",
                        Csv(result.Group), Csv(result.CheckName), Csv(result.StatusText),
                        "", "", "", Csv(result.Summary), ""));
                    continue;
                }

                foreach (ValidationIssue issue in result.Issues)
                {
                    sb.AppendLine(string.Join(",",
                        Csv(result.Group), Csv(result.CheckName), Csv(result.StatusText),
                        Csv(issue.SeverityText), Csv(issue.ElementName), Csv(issue.Category),
                        Csv(issue.Message), Csv(string.Join(";", issue.ElementIds))));
                }
            }

            return sb.ToString();
        }

        public static string BuildHtml(ValidationReport report)
        {
            string E(string? s) => WebUtility.HtmlEncode(s ?? string.Empty);

            var sb = new StringBuilder();

            sb.AppendLine("<!DOCTYPE html><html><head><meta charset=\"utf-8\">");
            sb.AppendLine($"<title>BIM Model Health Report – {E(report.ModelTitle)}</title>");
            sb.AppendLine("<style>");
            sb.AppendLine("body{font-family:Segoe UI,Arial,sans-serif;color:#172B4D;margin:32px;background:#F4F6FA}");
            sb.AppendLine("h1{font-size:22px;margin:0 0 4px}h2{font-size:16px;margin:28px 0 8px}");
            sb.AppendLine(".meta{color:#61738E;font-size:12px}.card{background:#fff;border:1px solid #DCE3ED;border-radius:6px;padding:16px;margin-top:16px}");
            sb.AppendLine(".score{font-size:40px;font-weight:700}table{border-collapse:collapse;width:100%;font-size:12px;background:#fff}");
            sb.AppendLine("th,td{border:1px solid #DCE3ED;padding:6px 8px;text-align:left;vertical-align:top}th{background:#EEF2F8}");
            sb.AppendLine(".Passed{color:#159447;font-weight:600}.Failed{color:#D64545;font-weight:600}.sev-Critical{color:#9B1C1C;font-weight:700}");
            sb.AppendLine(".sev-High{color:#D64545;font-weight:600}.sev-Medium{color:#DD6B20}.sev-Low{color:#61738E}.ids{color:#61738E;font-family:Consolas,monospace}");
            sb.AppendLine("</style></head><body>");

            sb.AppendLine("<h1>BIM Model Health Report</h1>");
            sb.AppendLine($"<div class=\"meta\">{E(report.ModelTitle)}<br>{E(report.ModelPath)}<br>");
            sb.AppendLine($"Scanned {report.ScannedAt:yyyy-MM-dd HH:mm} · Rules: {E(report.RulesSource)}</div>");

            sb.AppendLine("<div class=\"card\">");
            sb.AppendLine($"<div class=\"score\">{(report.HealthScore.HasValue ? report.HealthScore.Value + " / 100" : "--")}</div>");
            sb.AppendLine($"<div>{E(report.HealthStatus)} · {report.TotalIssues:N0} issue(s) in {report.Results.Count} check(s)</div>");
            sb.AppendLine("</div>");

            sb.AppendLine("<h2>Summary</h2><table><tr><th>Group</th><th>Check</th><th>Status</th><th>Issues</th><th>Worst</th><th>Summary</th></tr>");

            foreach (CheckResult r in report.Results)
            {
                sb.AppendLine($"<tr><td>{E(r.Group)}</td><td>{E(r.CheckName)}</td>" +
                              $"<td class=\"{r.Status}\">{E(r.StatusText)}</td><td>{r.IssueCount:N0}</td>" +
                              $"<td class=\"sev-{r.WorstSeverityText}\">{E(r.WorstSeverityText)}</td><td>{E(r.Summary)}</td></tr>");
            }

            sb.AppendLine("</table>");

            foreach (CheckResult r in report.Results.Where(x => x.Issues.Count > 0))
            {
                sb.AppendLine($"<h2>{E(r.Group)} – {E(r.CheckName)} ({r.IssueCount:N0})</h2>");
                sb.AppendLine("<table><tr><th>Severity</th><th>Element</th><th>Issue</th><th>Element IDs</th></tr>");

                foreach (ValidationIssue issue in r.Issues
                    .OrderByDescending(i => i.Severity)
                    .Take(MaxIssuesPerCheckInHtml))
                {
                    sb.AppendLine($"<tr><td class=\"sev-{issue.Severity}\">{issue.Severity}</td>" +
                                  $"<td>{E(issue.ElementName)}</td><td>{E(issue.Message)}</td>" +
                                  $"<td class=\"ids\">{E(issue.ElementIdsText)}</td></tr>");
                }

                sb.AppendLine("</table>");

                if (r.IssueCount > MaxIssuesPerCheckInHtml)
                {
                    sb.AppendLine($"<div class=\"meta\">Showing the first {MaxIssuesPerCheckInHtml} of {r.IssueCount:N0} issues. " +
                                  "Export to CSV for the full list.</div>");
                }
            }

            sb.AppendLine("</body></html>");
            return sb.ToString();
        }

        private static string Csv(string? value)
        {
            value ??= string.Empty;

            return value.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0
                ? "\"" + value.Replace("\"", "\"\"") + "\""
                : value;
        }
    }
}
