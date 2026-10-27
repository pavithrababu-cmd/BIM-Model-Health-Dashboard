using BIMModelHealthDashboard.Models;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace BIMModelHealthDashboard.Services
{
    // Builds the "BIM Model Health Report" PDF:
    //   1. Health score before and after Fix All Issues
    //   2. What was fixed automatically
    //   3. Issues per check: before -> after
    //   4. Project readiness / Model organization / Dependencies: before -> now
    //   5. Remaining issues that need a person
    public static class PdfHealthReport
    {
        private static readonly PdfColor Dark = PdfColor.FromHex("#172B4D");
        private static readonly PdfColor Muted = PdfColor.FromHex("#61738E");
        private static readonly PdfColor Border = PdfColor.FromHex("#DCE3ED");
        private static readonly PdfColor Light = PdfColor.FromHex("#F4F6FA");
        private static readonly PdfColor White = PdfColor.FromHex("#FFFFFF");
        private static readonly PdfColor Green = PdfColor.FromHex("#159447");
        private static readonly PdfColor Red = PdfColor.FromHex("#D64545");
        private static readonly PdfColor Amber = PdfColor.FromHex("#B7791F");
        private static readonly PdfColor Blue = PdfColor.FromHex("#2864E8");

        private const double Left = 40;
        private const double Right = SimplePdf.PageWidth - 40;
        private const double Width = Right - Left;
        private const double Bottom = SimplePdf.PageHeight - 50;

        public static byte[] Build(
            ValidationReport report,
            DashboardSummary? summary,
            HealthSnapshot? before,
            IReadOnlyList<FixResult> fixes)
        {
            var w = new Writer(report.ModelTitle);

            Header(w, report);
            ScoreSection(w, report, before, fixes);

            if (fixes.Count > 0)
                FixesSection(w, fixes);

            ChecksSection(w, report, before);

            if (summary != null)
                IndicatorsSection(w, summary, before);

            RemainingSection(w, report);

            return w.Pdf.Build();
        }

        // ================================================================
        // SECTIONS
        // ================================================================

        private static void Header(Writer w, ValidationReport report)
        {
            w.Pdf.Rect(0, 0, SimplePdf.PageWidth, 92, Dark);
            w.Pdf.Text(Left, 22, "BIM MODEL HEALTH REPORT", 19, true, White);
            w.Pdf.Text(Left, 52, SimplePdf.Fit(report.ModelTitle, 11, false, 360), 11, false, White);
            w.Pdf.TextRight(Right, 24, DateTime.Now.ToString("dd MMM yyyy  HH:mm", CultureInfo.InvariantCulture), 9, false, White);
            w.Pdf.TextRight(Right, 40, "Revit 2025 · BIM Model Health Dashboard", 9, false, White);

            w.Y = 108;
            w.Label("Model file", report.ModelPath);
            w.Label("Last scan", report.ScannedAt.ToString("dd MMM yyyy  HH:mm", CultureInfo.InvariantCulture) +
                                 $"  ·  {report.Results.Count} of {ValidationService.AllChecks.Count} checks");
            w.Label("Rules", report.RulesSource);
            w.Y += 6;
        }

        private static void ScoreSection(Writer w, ValidationReport report, HealthSnapshot? before, IReadOnlyList<FixResult> fixes)
        {
            w.Section("1. Health score");

            double boxWidth = (Width - 20) / 3;
            const double boxHeight = 78;
            w.Ensure(boxHeight + 70);

            if (before != null)
            {
                int scoreBefore = before.HealthScore ?? 0;
                int scoreAfter = report.HealthScore ?? 0;
                int issuesRemoved = before.TotalIssues - report.TotalIssues;
                double percent = before.TotalIssues == 0 ? 0 : issuesRemoved * 100.0 / before.TotalIssues;

                ScoreBox(w, Left, "BEFORE FIX ALL ISSUES", $"{scoreBefore} / 100", before.HealthStatus,
                    $"{F(before.TotalIssues)} issues", ScoreColor(scoreBefore));

                ScoreBox(w, Left + boxWidth + 10, "AFTER FIX ALL ISSUES", $"{scoreAfter} / 100", report.HealthStatus,
                    $"{F(report.TotalIssues)} issues", ScoreColor(scoreAfter));

                int gained = scoreAfter - scoreBefore;
                ScoreBox(w, Left + 2 * (boxWidth + 10), "IMPROVEMENT",
                    (gained >= 0 ? "+" : "") + gained + " points",
                    $"{F(issuesRemoved)} fewer issues",
                    $"{percent:0.#}% of all issues resolved", gained >= 0 ? Green : Red);

                w.Y += boxHeight + 14;

                ScoreBar(w, "Before", scoreBefore);
                ScoreBar(w, "After", scoreAfter);

                int totalFixed = fixes.Sum(f => f.Fixed);

                w.Paragraph(
                    $"Fix All Issues changed {F(totalFixed)} item(s) in the model. Total issues went from " +
                    $"{F(before.TotalIssues)} to {F(report.TotalIssues)} ({percent:0.#}% fewer) and the health score " +
                    $"moved from {scoreBefore} to {scoreAfter} ({before.HealthStatus} -> {report.HealthStatus}). " +
                    "The remaining issues need a person and are listed at the end of this report.",
                    10, Dark);
            }
            else
            {
                int score = report.HealthScore ?? 0;

                ScoreBox(w, Left, "CURRENT HEALTH", $"{score} / 100", report.HealthStatus,
                    $"{F(report.TotalIssues)} issues", ScoreColor(score));

                w.Y += boxHeight + 14;
                ScoreBar(w, "Current", score);

                w.Paragraph(
                    "No automatic fixes have been applied yet. Use Fix All Issues in the dashboard and export again " +
                    "to get a before/after comparison.",
                    10, Muted);
            }

            w.Paragraph(
                "How the score works: each check is worth an equal share of 100 points. A failing check loses part " +
                "of its share depending on the severity of its issues and how much of the model is affected.",
                8.5, Muted);
        }

        private static void FixesSection(Writer w, IReadOnlyList<FixResult> fixes)
        {
            w.Section("2. What was fixed automatically");

            w.TableHeader(new[] { ("Fix", 0.0), ("Fixed", 360.0), ("Skipped", 440.0) });

            foreach (FixResult fix in fixes)
            {
                List<string> notes = fix.Notes.ToList();

                if (fix.Error != null)
                    notes.Insert(0, "Not applied: " + fix.Error);

                List<string> noteLines = notes
                    .SelectMany(n => SimplePdf.Wrap(n, 8, false, 340))
                    .ToList();

                double rowHeight = 17 + noteLines.Count * 10;
                w.Ensure(rowHeight);

                w.RowBackground(rowHeight);
                w.Pdf.Text(Left + 6, w.Y + 4, SimplePdf.Fit(fix.Name, 9.5, true, 340), 9.5, true, Dark);
                w.Pdf.Text(Left + 360, w.Y + 4, F(fix.Fixed), 9.5, true, fix.Fixed > 0 ? Green : Muted);
                w.Pdf.Text(Left + 440, w.Y + 4, F(fix.Skipped), 9.5, false, Muted);

                double y = w.Y + 17;

                foreach (string line in noteLines)
                {
                    w.Pdf.Text(Left + 14, y, line, 8, false, Muted);
                    y += 10;
                }

                w.Y += rowHeight;
            }

            w.Y += 6;
        }

        private static void ChecksSection(Writer w, ValidationReport report, HealthSnapshot? before)
        {
            w.Section(before != null ? "3. Issues per check: before and after" : "3. Issues per check");

            if (before != null)
                w.TableHeader(new[] { ("Group", 0.0), ("Check", 120.0), ("Before", 355.0), ("After", 410.0), ("Change", 460.0) });
            else
                w.TableHeader(new[] { ("Group", 0.0), ("Check", 120.0), ("Status", 355.0), ("Issues", 440.0) });

            foreach (CheckResult check in report.Results)
            {
                w.Ensure(16);
                w.RowBackground(16);

                w.Pdf.Text(Left + 6, w.Y + 3.5, SimplePdf.Fit(check.Group, 8.5, false, 110), 8.5, false, Muted);
                w.Pdf.Text(Left + 120, w.Y + 3.5, SimplePdf.Fit(check.CheckName, 8.5, false, 228), 8.5, false, Dark);

                if (before != null)
                {
                    before.IssuesByCheck.TryGetValue(check.CheckId, out int was);
                    int now = check.IssueCount;
                    int change = now - was;

                    w.Pdf.Text(Left + 355, w.Y + 3.5, F(was), 8.5, false, Dark);
                    w.Pdf.Text(Left + 410, w.Y + 3.5, F(now), 8.5, true, now == 0 ? Green : Dark);
                    w.Pdf.Text(Left + 460, w.Y + 3.5,
                        change == 0 ? "-" : (change > 0 ? "+" : "") + F(change),
                        8.5, true, change < 0 ? Green : change > 0 ? Red : Muted);
                }
                else
                {
                    w.Pdf.Text(Left + 355, w.Y + 3.5, check.StatusText, 8.5, false,
                        check.Status == CheckStatus.Passed ? Green : check.Status == CheckStatus.Failed ? Red : Muted);
                    w.Pdf.Text(Left + 440, w.Y + 3.5, F(check.IssueCount), 8.5, true, Dark);
                }

                w.Y += 16;
            }

            // Total row
            w.Ensure(18);
            w.Pdf.Line(Left, w.Y, Right, w.Y, Dark, 0.8);
            w.Pdf.Text(Left + 120, w.Y + 4, "Total", 9, true, Dark);

            if (before != null)
            {
                int change = report.TotalIssues - before.TotalIssues;
                w.Pdf.Text(Left + 355, w.Y + 4, F(before.TotalIssues), 9, true, Dark);
                w.Pdf.Text(Left + 410, w.Y + 4, F(report.TotalIssues), 9, true, Dark);
                w.Pdf.Text(Left + 460, w.Y + 4, (change > 0 ? "+" : "") + F(change), 9, true, change < 0 ? Green : change > 0 ? Red : Muted);
            }
            else
            {
                w.Pdf.Text(Left + 440, w.Y + 4, F(report.TotalIssues), 9, true, Dark);
            }

            w.Y += 24;
        }

        private static void IndicatorsSection(Writer w, DashboardSummary summary, HealthSnapshot? before)
        {
            w.Section("4. Model health indicators");

            w.Paragraph(
                "These indicators are independent of the 16 checks: they describe how ready the drawing set is, " +
                "how the model is organised and what it depends on.",
                8.5, Muted);

            IndicatorGroup(w, "Project readiness", summary.ProjectReadiness, before?.ProjectReadiness);
            IndicatorGroup(w, "Model organization", summary.ModelOrganization, before?.ModelOrganization);
            IndicatorGroup(w, "Dependencies", summary.Dependencies, before?.Dependencies);
        }

        private static void IndicatorGroup(Writer w, string title, List<HealthIndicator> now, List<HealthIndicator>? was)
        {
            bool compare = was != null && was.Count > 0;

            w.Ensure(40);
            w.Pdf.Text(Left, w.Y, title.ToUpperInvariant(), 9, true, Blue);
            w.Y += 15;

            if (compare)
                w.TableHeader(new[] { ("Indicator", 14.0), ("Before", 210.0), ("Now", 360.0) });

            foreach (HealthIndicator indicator in now)
            {
                w.Ensure(16);
                w.RowBackground(16);

                w.Pdf.Dot(Left + 6, w.Y + 8, 3, StatusColor(indicator.Status));
                w.Pdf.Text(Left + 14, w.Y + 3.5, indicator.Label, 8.5, false, Dark);

                if (compare)
                {
                    string previous = HealthSnapshot.ValueOf(was!, indicator.Label) ?? "-";
                    w.Pdf.Text(Left + 210, w.Y + 3.5, SimplePdf.Fit(previous, 8.5, false, 145), 8.5, false, Muted);
                    w.Pdf.Text(Left + 360, w.Y + 3.5, SimplePdf.Fit(indicator.Value, 8.5, true, 150), 8.5, true, StatusColor(indicator.Status));
                }
                else
                {
                    w.Pdf.TextRight(Right - 6, w.Y + 3.5, indicator.Value, 8.5, true, StatusColor(indicator.Status));
                }

                w.Y += 16;
            }

            w.Y += 8;
        }

        private static void RemainingSection(Writer w, ValidationReport report)
        {
            w.Section("5. Remaining issues (need a person)");

            List<CheckResult> failing = report.Results.Where(r => r.IssueCount > 0).ToList();

            if (failing.Count == 0)
            {
                w.Paragraph("No remaining issues. The model passes all checks.", 10, Green);
                return;
            }

            w.Paragraph(
                "The five most serious issues per check. Open the dashboard, click the check and double-click an issue " +
                "to go straight to the element in Revit.",
                8.5, Muted);

            foreach (CheckResult check in failing)
            {
                w.Ensure(34);
                w.Pdf.Text(Left, w.Y, $"{check.Group} · {check.CheckName}", 9.5, true, Dark);
                w.Pdf.TextRight(Right, w.Y, $"{F(check.IssueCount)} issue(s)", 9, true, Red);
                w.Y += 14;

                foreach (ValidationIssue issue in check.Issues.OrderByDescending(i => i.Severity).Take(5))
                {
                    List<string> lines = SimplePdf.Wrap(
                        $"[{issue.Severity}] {issue.ElementName} - {issue.Message}", 8, false, Width - 14);

                    w.Ensure(lines.Count * 10 + 2);

                    foreach (string line in lines)
                    {
                        w.Pdf.Text(Left + 10, w.Y, line, 8, false, Dark);
                        w.Y += 10;
                    }

                    w.Y += 2;
                }

                if (check.IssueCount > 5)
                {
                    w.Pdf.Text(Left + 10, w.Y, $"... and {F(check.IssueCount - 5)} more", 8, false, Muted);
                    w.Y += 11;
                }

                w.Y += 6;
            }
        }

        // ================================================================
        // DRAWING HELPERS
        // ================================================================

        private static void ScoreBox(Writer w, double x, string title, string big, string line1, string line2, PdfColor accent)
        {
            double width = (Width - 20) / 3;

            w.Pdf.Rect(x, w.Y, width, 78, Light);
            w.Pdf.Rect(x, w.Y, width, 3, accent);
            w.Pdf.Text(x + 10, w.Y + 11, title, 7.5, true, Muted);
            w.Pdf.Text(x + 10, w.Y + 25, big, 20, true, accent);
            w.Pdf.Text(x + 10, w.Y + 50, SimplePdf.Fit(line1, 8.5, false, width - 20), 8.5, false, Dark);
            w.Pdf.Text(x + 10, w.Y + 62, SimplePdf.Fit(line2, 8.5, false, width - 20), 8.5, false, Muted);
        }

        private static void ScoreBar(Writer w, string label, int score)
        {
            const double barLeft = Left + 60;
            double barWidth = Width - 100;

            w.Pdf.Text(Left, w.Y, label, 9, true, Dark);
            w.Pdf.Rect(barLeft, w.Y + 1, barWidth, 10, Border);
            w.Pdf.Rect(barLeft, w.Y + 1, barWidth * Math.Max(0, Math.Min(100, score)) / 100.0, 10, ScoreColor(score));
            w.Pdf.TextRight(Right, w.Y, $"{score} / 100", 9, true, Dark);
            w.Y += 18;
        }

        private static PdfColor ScoreColor(int score) =>
            score >= 85 ? Green : score >= 65 ? Amber : Red;

        private static PdfColor StatusColor(IndicatorStatus status) =>
            status == IndicatorStatus.Good ? Green :
            status == IndicatorStatus.Warning ? Amber :
            status == IndicatorStatus.Bad ? Red :
            Muted;

        private static string F(int value) => value.ToString("N0", CultureInfo.InvariantCulture);

        // Keeps track of the current page and vertical position.
        private sealed class Writer
        {
            private readonly string _modelTitle;

            public Writer(string modelTitle)
            {
                _modelTitle = modelTitle;
                Pdf = new SimplePdf();
                NewPage();
            }

            public SimplePdf Pdf { get; }
            public double Y { get; set; }

            public void NewPage()
            {
                Pdf.NewPage();
                Y = 40;

                Pdf.Line(Left, SimplePdf.PageHeight - 34, Right, SimplePdf.PageHeight - 34, Border, 0.6);
                Pdf.Text(Left, SimplePdf.PageHeight - 28,
                    SimplePdf.Fit("BIM Model Health Report · " + _modelTitle, 7.5, false, Width - 60), 7.5, false, Muted);
                Pdf.TextRight(Right, SimplePdf.PageHeight - 28, $"Page {Pdf.PageCount}", 7.5, false, Muted);
            }

            public void Ensure(double height)
            {
                if (Y + height > Bottom)
                    NewPage();
            }

            public void Section(string title)
            {
                Ensure(60);
                Y += 6;
                Pdf.Text(Left, Y, title, 13, true, Dark);
                Y += 19;
                Pdf.Line(Left, Y, Right, Y, Border, 0.8);
                Y += 9;
            }

            public void Label(string label, string value)
            {
                Pdf.Text(Left, Y, label, 8.5, true, Muted);

                foreach (string line in SimplePdf.Wrap(value ?? string.Empty, 8.5, false, Width - 80))
                {
                    Pdf.Text(Left + 80, Y, line, 8.5, false, Dark);
                    Y += 12;
                }
            }

            public void Paragraph(string text, double size, PdfColor color)
            {
                foreach (string line in SimplePdf.Wrap(text, size, false, Width))
                {
                    Ensure(size + 4);
                    Pdf.Text(Left, Y, line, size, false, color);
                    Y += size + 4;
                }

                Y += 6;
            }

            public void TableHeader((string Title, double X)[] columns)
            {
                Ensure(36);
                Pdf.Rect(Left, Y, Width, 16, Dark);

                foreach (var column in columns)
                    Pdf.Text(Left + column.X + 6, Y + 4, column.Title, 8, true, White);

                Y += 16;
                _row = 0;
            }

            private int _row;

            public void RowBackground(double height)
            {
                if (_row++ % 2 == 1)
                    Pdf.Rect(Left, Y, Width, height, Light);
            }
        }
    }
}
