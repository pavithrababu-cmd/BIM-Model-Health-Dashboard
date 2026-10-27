using Autodesk.Revit.DB;
using BIMModelHealthDashboard.Checks;
using BIMModelHealthDashboard.Config;
using BIMModelHealthDashboard.Models;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace BIMModelHealthDashboard.Services
{
    public static class ValidationService
    {
        // Registry of all checks, in dashboard order.
        public static IReadOnlyList<IModelCheck> AllChecks { get; } = new IModelCheck[]
        {
            // 01 Element Checker
            new MissingParametersCheck(),
            new WrongFamilyTypesCheck(),
            new DuplicateElementsCheck(),
            new DoorWindowParametersCheck(),

            // 02 Wall Checker
            new RevitWarningsCheck(),
            new UnconnectedWallsCheck(),
            new WorksetCheck(),
            new MaterialsCheck(),

            // 03 Room Checker
            new RoomsNotEnclosedCheck(),
            new UnplacedRoomsCheck(),
            new ViewsCheck(),
            new IncorrectLevelsCheck(),

            // 04 Standards & Compliance
            new FamilyStandardsCheck(),
            new CoordinatesCheck(),
            new NamingConventionsCheck(),
            new CobieCheck()
        };

        // Runs the checks of the given groups (all groups when null/empty).
        public static List<CheckResult> Run(
            Document doc,
            HealthRules rules,
            IReadOnlyCollection<string>? groups)
        {
            var context = new CheckContext(doc, rules);
            var results = new List<CheckResult>();

            for (int i = 0; i < AllChecks.Count; i++)
            {
                IModelCheck check = AllChecks[i];

                if (groups != null && groups.Count > 0 && !groups.Contains(check.Group))
                    continue;

                var result = new CheckResult
                {
                    CheckId = check.Id,
                    CheckName = check.Name,
                    Group = check.Group,
                    Description = check.Description,
                    Order = i
                };

                int errorsBefore = context.ConfigErrors.Count;
                Stopwatch stopwatch = Stopwatch.StartNew();

                try
                {
                    check.Run(context, result);

                    if (result.Status == CheckStatus.NotRun)
                        result.Status = result.IssueCount > 0 ? CheckStatus.Failed : CheckStatus.Passed;

                    if (string.IsNullOrWhiteSpace(result.Summary))
                    {
                        result.Summary = result.IssueCount > 0
                            ? $"{result.IssueCount:N0} issue(s) found."
                            : "No issues found.";
                    }
                }
                catch (Exception ex)
                {
                    result.Status = CheckStatus.Error;
                    result.Summary = "The check could not be completed: " + ex.Message;
                }

                stopwatch.Stop();
                result.DurationMs = stopwatch.ElapsedMilliseconds;
                result.ConfigurationWarnings.AddRange(context.ConfigErrors.Skip(errorsBefore));

                results.Add(result);
            }

            return results;
        }

        public static string ModelKey(Document doc) =>
            string.IsNullOrWhiteSpace(doc.PathName) ? doc.Title : doc.PathName;
    }

    // Health score (0–100).
    //
    // Every check that ran is worth an equal share of 100 points
    // (16 checks = 6.25 points each). A failing check loses part of its share:
    //   - how severe its worst issue is   (High/Critical 100%, Medium 70%, Low 40%)
    //   - how much of the model is affected (a failing check loses at least 25%
    //     of its share; with a third or more of the checked items affected it
    //     loses all of it).
    // So 2 wrong family types out of 300 cost far less than 1,700 elements
    // with missing parameters.
    public static class ValidationScoring
    {
        private static double SeverityFactor(IssueSeverity? severity)
        {
            switch (severity)
            {
                case IssueSeverity.Critical:
                case IssueSeverity.High:
                    return 1.0;
                case IssueSeverity.Medium:
                    return 0.7;
                case IssueSeverity.Low:
                    return 0.4;
                default:
                    return 0.0;
            }
        }

        public static double PenaltyFor(CheckResult result, double pointsPerCheck)
        {
            if (result.IssueCount == 0)
                return 0.0;

            double affected = Math.Min(1.0, (double)result.IssueCount / Math.Max(1, result.CheckedCount));
            double extent = Math.Min(1.0, 0.25 + affected * 3.0);

            return pointsPerCheck * SeverityFactor(result.WorstSeverity) * extent;
        }

        public static void Apply(ValidationReport report, int totalCheckCount)
        {
            List<CheckResult> ran = report.Results
                .Where(r => r.Status == CheckStatus.Passed || r.Status == CheckStatus.Failed)
                .ToList();

            report.IsComplete = report.Results.Count >= totalCheckCount;

            if (ran.Count == 0)
            {
                report.HealthScore = null;
                report.HealthStatus = "Checks not yet run";
                return;
            }

            double pointsPerCheck = 100.0 / ran.Count;
            double penalty = ran.Sum(r => PenaltyFor(r, pointsPerCheck));

            int score = (int)Math.Round(Math.Max(0.0, 100.0 - penalty));

            string status =
                score >= 85 ? "Healthy" :
                score >= 65 ? "Needs attention" :
                score >= 40 ? "At risk" :
                "Critical";

            report.HealthScore = score;
            report.HealthStatus = report.IsComplete ? status : status + " (partial scan)";
        }
    }
}
