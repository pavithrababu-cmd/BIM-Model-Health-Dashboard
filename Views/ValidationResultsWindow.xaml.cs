using BIMModelHealthDashboard.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace BIMModelHealthDashboard.Views
{
    public partial class ValidationResultsWindow : Window
    {
        public const string AllGroups = "All groups";

        private readonly MainWindow _dashboard;
        private ValidationReport? _report;
        private bool _updating;

        public ValidationResultsWindow(MainWindow dashboard)
        {
            InitializeComponent();
            _dashboard = dashboard ?? throw new ArgumentNullException(nameof(dashboard));

            GroupFilter.ItemsSource = new[] { AllGroups }.Concat(CheckGroups.All).ToList();
            GroupFilter.SelectedIndex = 0;
        }

        // Shows (or refreshes) the report. group/checkId choose what to focus;
        // when null the current selection is kept.
        public void ShowReport(ValidationReport report, string? group, string? checkId)
        {
            _report = report;

            string? keepCheckId = checkId ?? (ChecksGrid.SelectedItem as CheckResult)?.CheckId;

            _updating = true;

            if (group != null)
                GroupFilter.SelectedItem = group;
            else if (checkId == null && GroupFilter.SelectedItem == null)
                GroupFilter.SelectedIndex = 0;

            _updating = false;

            TitleText.Text = $"Validation Results – {report.ModelTitle}";

            SummaryText.Text =
                $"Health score: {(report.HealthScore.HasValue ? report.HealthScore.Value + " / 100" : "--")} " +
                $"({report.HealthStatus}) · {report.TotalIssues:N0} issue(s) in {report.Results.Count} check(s) · " +
                $"scanned {report.ScannedAt:dd MMM yyyy HH:mm} · rules: {report.RulesSource}";

            RefreshChecks(keepCheckId);
        }

        private void RefreshChecks(string? selectCheckId)
        {
            if (_report == null)
                return;

            string? group = GroupFilter.SelectedItem as string;

            List<CheckResult> checks = _report.Results
                .Where(r => group == null || group == AllGroups || r.Group == group)
                .ToList();

            ChecksGrid.ItemsSource = checks;

            CheckResult? toSelect =
                checks.FirstOrDefault(c => c.CheckId == selectCheckId) ??
                checks.FirstOrDefault(c => c.IssueCount > 0) ??
                checks.FirstOrDefault();

            ChecksGrid.SelectedItem = toSelect;

            if (toSelect != null)
                ChecksGrid.ScrollIntoView(toSelect);

            ShowCheck(toSelect);
        }

        private void ShowCheck(CheckResult? check)
        {
            if (check == null)
            {
                CheckTitleText.Text = "No checks in this group have been run.";
                CheckDescriptionText.Text = string.Empty;
                CheckSummaryText.Text = string.Empty;
                CheckWarningsText.Visibility = Visibility.Collapsed;
                IssuesGrid.ItemsSource = null;
                return;
            }

            CheckTitleText.Text = $"{check.Group} · {check.CheckName}";
            CheckDescriptionText.Text = check.Description;
            CheckSummaryText.Text =
                $"{check.StatusText}: {check.Summary}  ({check.CheckedCount:N0} checked, {check.DurationMs:N0} ms)";

            if (check.ConfigurationWarnings.Count > 0)
            {
                CheckWarningsText.Text = "Rules file: " + string.Join(" | ", check.ConfigurationWarnings);
                CheckWarningsText.Visibility = Visibility.Visible;
            }
            else
            {
                CheckWarningsText.Visibility = Visibility.Collapsed;
            }

            ApplyIssueFilter();
        }

        private void ApplyIssueFilter()
        {
            if (!(ChecksGrid.SelectedItem is CheckResult check))
            {
                IssuesGrid.ItemsSource = null;
                return;
            }

            string search = SearchBox.Text?.Trim() ?? string.Empty;

            IEnumerable<ValidationIssue> issues = check.Issues
                .OrderByDescending(i => i.Severity)
                .ThenBy(i => i.ElementName);

            if (search.Length > 0)
            {
                issues = issues.Where(i =>
                    Contains(i.ElementName, search) ||
                    Contains(i.Message, search) ||
                    Contains(i.Category, search) ||
                    i.ElementIds.Any(id => id.ToString() == search));
            }

            List<ValidationIssue> list = issues.ToList();
            IssuesGrid.ItemsSource = list;

            FooterText.Text = search.Length > 0
                ? $"{list.Count:N0} of {check.IssueCount:N0} issues match '{search}'. Double-click an issue to select it in Revit."
                : $"{check.IssueCount:N0} issue(s). Double-click an issue to select and zoom to it in Revit.";
        }

        private static bool Contains(string? text, string search) =>
            text != null && text.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0;

        private void SelectIssues(IEnumerable<ValidationIssue> issues)
        {
            List<long> ids = issues.SelectMany(i => i.ElementIds).Distinct().ToList();
            _dashboard.RequestSelection(ids);
        }

        // ------------------------------------------------------------------
        // Events
        // ------------------------------------------------------------------

        private void GroupFilter_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_updating)
                RefreshChecks((ChecksGrid.SelectedItem as CheckResult)?.CheckId);
        }

        private void ChecksGrid_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
            ShowCheck(ChecksGrid.SelectedItem as CheckResult);

        private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) => ApplyIssueFilter();

        private void IssuesGrid_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
            SelectButton.IsEnabled = IssuesGrid.SelectedItems.Count > 0;

        private void IssuesGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (IssuesGrid.SelectedItem is ValidationIssue issue)
                SelectIssues(new[] { issue });
        }

        private void SelectInRevit_Click(object sender, RoutedEventArgs e) =>
            SelectIssues(IssuesGrid.SelectedItems.OfType<ValidationIssue>());

        private void SelectAllForCheck_Click(object sender, RoutedEventArgs e)
        {
            if (ChecksGrid.SelectedItem is CheckResult check)
                SelectIssues(check.Issues);
        }

        private void Export_Click(object sender, RoutedEventArgs e) => _dashboard.ExportReport();

        private void Close_Click(object sender, RoutedEventArgs e) => Close();
    }
}
