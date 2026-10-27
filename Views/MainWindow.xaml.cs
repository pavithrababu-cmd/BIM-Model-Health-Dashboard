using BIMModelHealthDashboard.Config;
using BIMModelHealthDashboard.Models;
using BIMModelHealthDashboard.Services;
using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using ExternalEvent = Autodesk.Revit.UI.ExternalEvent;
using ExternalEventRequest = Autodesk.Revit.UI.ExternalEventRequest;

namespace BIMModelHealthDashboard.Views
{
    public partial class MainWindow : Window
    {
        private readonly DashboardRequestHandler _handler;
        private readonly ExternalEvent _externalEvent;
        private readonly Dictionary<string, CheckRowViewModel> _rows;

        private ValidationReport? _report;
        private DashboardSummary? _summary;
        private ValidationResultsWindow? _resultsWindow;

        // Score and issue count before Fix All Issues, for the summary.
        private int? _scoreBeforeFix;
        private int _issuesBeforeFix;

        // For the PDF report: the model's health before the FIRST Fix All Issues
        // of this session, and everything fixed since (per fix, added up).
        private HealthSnapshot? _beforeFixes;
        private readonly Dictionary<string, FixResult> _fixHistory = new Dictionary<string, FixResult>();

        // Where to focus the results window when the running scan finishes.
        private string? _focusGroupAfterScan;
        private bool _openResultsAfterScan;

        public MainWindow(DashboardRequestHandler handler, ExternalEvent externalEvent)
        {
            InitializeComponent();

            _handler = handler ?? throw new ArgumentNullException(nameof(handler));
            _externalEvent = externalEvent ?? throw new ArgumentNullException(nameof(externalEvent));

            _handler.MetricsCompleted = summary => OnUi(() => UpdateDashboard(summary));
            _handler.ChecksCompleted = report => OnUi(() => ApplyValidationReport(report));
            _handler.Failed = message => OnUi(() => ShowError(message));
            _handler.Info = message => OnUi(() => SetStatus(message));
            _handler.FixesCompleted = fixes => OnUi(() => ShowFixSummary(fixes));

            _rows = ValidationService.AllChecks.ToDictionary(
                c => c.Id,
                c => new CheckRowViewModel(c.Id, c.Name, c.Group, c.Description));

            ElementChecksList.ItemsSource = RowsFor(CheckGroups.Element);
            WallChecksList.ItemsSource = RowsFor(CheckGroups.Wall);
            RoomChecksList.ItemsSource = RowsFor(CheckGroups.Room);
            StandardsChecksList.ItemsSource = RowsFor(CheckGroups.Standards);

            Loaded += (sender, args) => RequestRefresh();
            Closed += (sender, args) => _resultsWindow?.Close();
        }

        private List<CheckRowViewModel> RowsFor(string group) =>
            _rows.Values.Where(r => r.Group == group).ToList();

        // ------------------------------------------------------------------
        // Requests to Revit (executed on the API thread by the handler)
        // ------------------------------------------------------------------

        public void RequestRefresh()
        {
            Send(
                new DashboardRequest { Kind = DashboardRequestKind.RefreshMetrics },
                "Requesting active Revit model information...");
        }

        private void RunChecks(string? group, bool openResults)
        {
            _openResultsAfterScan = openResults;
            _focusGroupAfterScan = group;

            Send(
                new DashboardRequest
                {
                    Kind = DashboardRequestKind.RunChecks,
                    Groups = group == null ? null : new[] { group }
                },
                group == null
                    ? "Running all 16 checks... Revit will be busy for a moment."
                    : $"Running {group}... Revit will be busy for a moment.");

            Cursor = Cursors.Wait;
        }

        // Called by the results window.
        public void RequestSelection(IReadOnlyCollection<long> elementIds)
        {
            if (elementIds.Count == 0)
            {
                SetStatus("This issue is not linked to a selectable element.");
                return;
            }

            Send(
                new DashboardRequest
                {
                    Kind = DashboardRequestKind.SelectElements,
                    ElementIds = elementIds,
                    ExpectedModelKey = _report?.ModelKey
                },
                "Selecting elements in Revit...");
        }

        private void Send(DashboardRequest request, string status)
        {
            SetStatus(status);
            _handler.Enqueue(request);

            ExternalEventRequest result = _externalEvent.Raise();

            if (result == ExternalEventRequest.Denied || result == ExternalEventRequest.TimedOut)
            {
                Cursor = null;
                ShowError("Revit is busy and could not accept the request. Finish the current command and try again.");
            }
        }

        // ------------------------------------------------------------------
        // Results from Revit
        // ------------------------------------------------------------------

        private void UpdateDashboard(DashboardSummary summary)
        {
            // Another model became active: old validation results no longer apply.
            if (_report != null && _report.ModelKey != summary.ModelKey)
                ClearValidationResults();

            _summary = summary;

            ModelFileNameText.Text = string.IsNullOrWhiteSpace(summary.ModelFileName)
                ? "Active Revit project"
                : summary.ModelFileName;

            FileSizeText.Text = summary.FileSize ?? "N/A";
            TotalFamiliesText.Text = summary.TotalFamilies.ToString("N0");
            WarningsText.Text = summary.RevitWarnings.ToString("N0");

            ShowHealthIndicators();

            if (_report == null)
                SetStatus("Model metrics refreshed. Run a scan to validate the model.");
        }

        private void ApplyValidationReport(ValidationReport partial)
        {
            Cursor = null;

            if (_report == null || _report.ModelKey != partial.ModelKey)
            {
                _report = new ValidationReport
                {
                    ModelKey = partial.ModelKey,
                    ModelTitle = partial.ModelTitle,
                    ModelPath = partial.ModelPath
                };
            }

            _report.Merge(partial.Results);
            _report.ScannedAt = partial.ScannedAt;
            _report.RulesSource = partial.RulesSource;
            _report.RulesWarning = partial.RulesWarning;

            ValidationScoring.Apply(_report, ValidationService.AllChecks.Count);

            foreach (CheckRowViewModel row in _rows.Values)
                row.Apply(_report.Find(row.CheckId));

            UpdateValidationSummary();

            int errors = partial.Results.Count(r => r.Status == CheckStatus.Error);
            int configWarnings = partial.Results.Sum(r => r.ConfigurationWarnings.Count);

            string status =
                $"Scan finished at {partial.ScannedAt:HH:mm}: {partial.Results.Count} check(s), " +
                $"{partial.TotalIssues:N0} issue(s).";

            if (errors > 0)
                status += $" {errors} check(s) failed to run.";

            if (configWarnings > 0)
                status += $" {configWarnings} rules-file problem(s) – see the results window.";

            SetStatus(status);

            if (!string.IsNullOrWhiteSpace(partial.RulesWarning))
            {
                MessageBox.Show(
                    partial.RulesWarning!,
                    "BIM Model Health Dashboard",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }

            if (_openResultsAfterScan)
                ShowResults(_focusGroupAfterScan ?? ValidationResultsWindow.AllGroups, null);
            else
                _resultsWindow?.ShowReport(_report, null, null);

            _openResultsAfterScan = false;
            _focusGroupAfterScan = null;
        }

        private void UpdateValidationSummary()
        {
            if (_report == null || _report.Results.Count == 0)
            {
                TotalIssuesText.Text = "--";
                TotalIssuesSubText.Text = "Awaiting validation scan";
                HealthScoreText.Text = "--";
                HealthStatusText.Text = "Checks not yet run";
                ShowHealthIndicators();
                return;
            }

            TotalIssuesText.Text = _report.TotalIssues.ToString("N0");
            TotalIssuesSubText.Text = _report.IsComplete
                ? $"All {_report.Results.Count} checks · {_report.ScannedAt:dd MMM HH:mm}"
                : $"{_report.Results.Count} of {ValidationService.AllChecks.Count} checks run";

            HealthScoreText.Text = _report.HealthScore.HasValue
                ? $"{_report.HealthScore.Value} / 100"
                : "--";

            HealthStatusText.Text = _report.HealthStatus;

            ShowHealthIndicators();
        }

        // ------------------------------------------------------------------
        // Project readiness / Model organization / Dependencies panels
        // ------------------------------------------------------------------

        private void ShowHealthIndicators()
        {
            if (_summary == null)
                return;

            ReadinessList.ItemsSource = _summary.ProjectReadiness
                .Select(IndicatorRowViewModel.From)
                .ToList();

            OrganizationList.ItemsSource = _summary.ModelOrganization
                .Select(IndicatorRowViewModel.From)
                .ToList();

            DependenciesList.ItemsSource = _summary.Dependencies
                .Select(IndicatorRowViewModel.From)
                .ToList();
        }

        // Adds the results of one Fix All Issues run to the session history.
        private void RecordFixHistory(List<FixResult> fixes)
        {
            foreach (FixResult fix in fixes)
            {
                if (!_fixHistory.TryGetValue(fix.FixId, out FixResult? total))
                {
                    total = new FixResult { FixId = fix.FixId, Name = fix.Name };
                    _fixHistory[fix.FixId] = total;
                }

                total.Fixed += fix.Fixed;
                total.Skipped += fix.Skipped;
                total.Error = fix.Error;
                total.Notes.Clear();
                total.Notes.AddRange(fix.Notes);
            }
        }

        private void ClearValidationResults()
        {
            _report = null;
            _beforeFixes = null;
            _fixHistory.Clear();

            foreach (CheckRowViewModel row in _rows.Values)
                row.Apply(null);

            UpdateValidationSummary();
            _resultsWindow?.Close();
        }

        // ------------------------------------------------------------------
        // Results window
        // ------------------------------------------------------------------

        private void ShowResults(string? group, string? checkId)
        {
            if (_report == null || _report.Results.Count == 0)
            {
                MessageBox.Show(
                    "No checks have been run yet. Use 'Run Full Scan' or one of the checker buttons first.",
                    "BIM Model Health Dashboard",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            if (_resultsWindow == null)
            {
                _resultsWindow = new ValidationResultsWindow(this) { Owner = this };
                _resultsWindow.Closed += (sender, args) => _resultsWindow = null;
            }

            _resultsWindow.ShowReport(_report, group, checkId);

            if (!_resultsWindow.IsVisible)
                _resultsWindow.Show();

            _resultsWindow.Activate();
        }

        private void CheckRow_Click(object sender, MouseButtonEventArgs e)
        {
            if (!((sender as FrameworkElement)?.DataContext is CheckRowViewModel row))
                return;

            if (_report?.Find(row.CheckId) == null)
            {
                if (MessageBox.Show(
                        $"'{row.Name}' has not been run yet.\n\nRun the {row.Group} now?",
                        "BIM Model Health Dashboard",
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Question) == MessageBoxResult.Yes)
                {
                    RunChecks(row.Group, openResults: true);
                }

                return;
            }

            ShowResults(row.Group, row.CheckId);
        }

        // ------------------------------------------------------------------
        // Buttons
        // ------------------------------------------------------------------

        private void RefreshDashboard_Click(object sender, RoutedEventArgs e) => RequestRefresh();

        private void RunModelScan_Click(object sender, RoutedEventArgs e) => RunChecks(null, openResults: true);

        private void ElementChecker_Click(object sender, RoutedEventArgs e) => RunChecks(CheckGroups.Element, openResults: true);

        private void WallChecker_Click(object sender, RoutedEventArgs e) => RunChecks(CheckGroups.Wall, openResults: true);

        private void RoomChecker_Click(object sender, RoutedEventArgs e) => RunChecks(CheckGroups.Room, openResults: true);

        private void StandardsChecker_Click(object sender, RoutedEventArgs e) => RunChecks(CheckGroups.Standards, openResults: true);

        private void ViewAllResults_Click(object sender, RoutedEventArgs e) => ShowResults(ValidationResultsWindow.AllGroups, null);

        private void ViewGroupResults_Click(object sender, RoutedEventArgs e)
        {
            string? list = (sender as FrameworkElement)?.Tag as string;

            string? group =
                list == nameof(ElementChecksList) ? CheckGroups.Element :
                list == nameof(WallChecksList) ? CheckGroups.Wall :
                list == nameof(RoomChecksList) ? CheckGroups.Room :
                list == nameof(StandardsChecksList) ? CheckGroups.Standards :
                null;

            ShowResults(group, null);
        }

        private void Report_Click(object sender, RoutedEventArgs e) => ExportReport();

        // ------------------------------------------------------------------
        // Fix All Issues
        // ------------------------------------------------------------------

        private void FixAllIssues_Click(object sender, RoutedEventArgs e)
        {
            if (_report == null || _report.Results.Count == 0)
            {
                MessageBox.Show(
                    "Run Full Scan first, so the dashboard knows which issues exist.",
                    "Fix All Issues",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            var dialog = new FixIssuesWindow(_report) { Owner = this };

            if (dialog.ShowDialog() != true)
                return;

            _scoreBeforeFix = _report.HealthScore;
            _issuesBeforeFix = _report.TotalIssues;

            if (_beforeFixes == null || _beforeFixes.ModelKey != _report.ModelKey)
            {
                _beforeFixes = HealthSnapshot.Capture(_report, _summary);
                _fixHistory.Clear();
            }
            _openResultsAfterScan = false;

            Send(
                new DashboardRequest
                {
                    Kind = DashboardRequestKind.FixIssues,
                    FixIds = dialog.SelectedFixIds,
                    ExpectedModelKey = _report.ModelKey
                },
                "Fixing issues and re-scanning the model... Revit will be busy for a moment.");

            Cursor = Cursors.Wait;
        }

        // Shown after the fixes are applied and all checks have run again.
        private void ShowFixSummary(List<FixResult> fixes)
        {
            Cursor = null;

            RecordFixHistory(fixes);

            var text = new System.Text.StringBuilder();
            int totalFixed = fixes.Sum(f => f.Fixed);

            foreach (FixResult fix in fixes)
            {
                text.AppendLine(fix.Error == null
                    ? $"✓ {fix.Name}: {fix.Fixed:N0} fixed" + (fix.Skipped > 0 ? $", {fix.Skipped:N0} skipped" : "")
                    : $"✗ {fix.Name}: not applied – {fix.Error}");

                foreach (string note in fix.Notes)
                    text.AppendLine("     " + note);
            }

            text.AppendLine();

            if (_report != null)
            {
                text.AppendLine($"Total issues: {_issuesBeforeFix:N0} → {_report.TotalIssues:N0}");
                text.AppendLine(
                    $"Health score: {(_scoreBeforeFix.HasValue ? _scoreBeforeFix.Value.ToString() : "--")} → " +
                    $"{(_report.HealthScore.HasValue ? _report.HealthScore.Value.ToString() : "--")} / 100");
            }

            text.AppendLine();
            text.AppendLine("To undo all of these changes, press Ctrl+Z once in Revit (\"BIM Health: Fix All Issues\").");
            text.AppendLine("The remaining issues need a person: click a check line and double-click an issue to go to it.");
            text.AppendLine("Click Export Report to save the before/after analysis as a PDF.");

            SetStatus($"Fix All Issues: {totalFixed:N0} item(s) fixed. Model re-scanned.");

            MessageBox.Show(
                text.ToString(),
                "Fix All Issues – results",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }

        public void ExportReport()
        {
            if (_report == null || _report.Results.Count == 0)
            {
                MessageBox.Show(
                    "Run a scan before exporting a report.",
                    "BIM Model Health Report",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            string safeTitle = string.Concat(
                Path.GetFileNameWithoutExtension(_report.ModelTitle)
                    .Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));

            var dialog = new SaveFileDialog
            {
                Title = "Export BIM Model Health Report",
                Filter = "PDF report (*.pdf)|*.pdf|CSV issue list for Excel (*.csv)|*.csv",
                FileName = $"{safeTitle}_ModelHealth_{DateTime.Now:yyyyMMdd_HHmm}",
                AddExtension = true
            };

            if (dialog.ShowDialog(this) != true)
                return;

            try
            {
                if (Path.GetExtension(dialog.FileName).Equals(".csv", StringComparison.OrdinalIgnoreCase))
                {
                    ReportExporter.Export(_report, dialog.FileName);
                }
                else
                {
                    HealthSnapshot? before =
                        _beforeFixes != null && _beforeFixes.ModelKey == _report.ModelKey ? _beforeFixes : null;

                    byte[] pdf = PdfHealthReport.Build(
                        _report,
                        _summary != null && _summary.ModelKey == _report.ModelKey ? _summary : null,
                        before,
                        before != null ? _fixHistory.Values.ToList() : new List<FixResult>());

                    File.WriteAllBytes(dialog.FileName, pdf);
                }

                SetStatus($"Report saved: {dialog.FileName}");

                if (MessageBox.Show(
                        "Report saved. Open it now?",
                        "BIM Model Health Report",
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Information) == MessageBoxResult.Yes)
                {
                    Process.Start(new ProcessStartInfo(dialog.FileName) { UseShellExecute = true });
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is System.ComponentModel.Win32Exception)
            {
                ShowError("Could not save or open the report.\n\n" + ex.Message);
            }
        }

        private void EditRules_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                RulesRepository.EnsureExists();
                string path = RulesRepository.ActiveRulesPath;

                Process.Start(new ProcessStartInfo("notepad.exe", $"\"{path}\"") { UseShellExecute = true });

                SetStatus($"Editing rules: {path}. Save the file, then re-run the scan.");
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is System.ComponentModel.Win32Exception)
            {
                ShowError("Could not open the rules file.\n\n" + ex.Message);
            }
        }

        private void Settings_Click(object sender, RoutedEventArgs e)
        {
            MessageBox.Show(
                "BIM Model Health Dashboard for Revit 2025\n\n" +
                "16 checks in four groups:\n" +
                "01 Element: parameters, family types, duplicates, doors/windows\n" +
                "02 Wall: Revit warnings, unconnected walls, worksets, materials\n" +
                "03 Room: enclosure, unplaced rooms, views, levels\n" +
                "04 Standards: families, coordinates, naming, COBie\n\n" +
                "Company rules file:\n" + RulesRepository.ActiveRulesPath + "\n\n" +
                "To share rules with the whole team, place HealthRules.json next to the add-in DLL; " +
                "it then takes priority over the per-user file.\n\n" +
                "Health score: each check is worth an equal share of 100 points. A failing check loses part " +
                "of its share depending on the severity of its issues and how much of the model is affected " +
                "(a third or more of the checked items = the whole share).\n\n" +
                "Fix All Issues repairs what can be fixed safely (duplicates, unused families, wall joins, worksets, " +
                "and optionally unused views, unplaced rooms and empty Marks) as one Revit undo step.",
                "About",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }

        // ------------------------------------------------------------------
        // Helpers
        // ------------------------------------------------------------------

        private void SetStatus(string message) => ScanStatusText.Text = message;

        private void ShowError(string message)
        {
            Cursor = null;
            SetStatus("Last request did not complete.");

            MessageBox.Show(
                string.IsNullOrWhiteSpace(message) ? "An unknown error occurred." : message,
                "BIM Model Health Dashboard",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }

        private void OnUi(Action action)
        {
            if (Dispatcher.CheckAccess())
                action();
            else
                Dispatcher.BeginInvoke(action);
        }
    }
}
