using BIMModelHealthDashboard.Models;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Media;

namespace BIMModelHealthDashboard.Views
{
    public partial class FixIssuesWindow : Window
    {
        private readonly List<FixOption> _options;

        public FixIssuesWindow(ValidationReport report)
        {
            InitializeComponent();

            _options = FixCatalog.All
                .Select(definition => new FixOption(definition, report.Find(definition.RelatedCheckId)))
                .ToList();

            FixList.ItemsSource = _options;
        }

        // Ids of the fixes the user ticked (valid after DialogResult == true).
        public List<string> SelectedFixIds =>
            _options.Where(o => o.IsSelected).Select(o => o.Id).ToList();

        private void FixSelected_Click(object sender, RoutedEventArgs e)
        {
            if (SelectedFixIds.Count == 0)
            {
                MessageBox.Show(this, "Select at least one fix.", "Fix All Issues",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            if (MessageBox.Show(this,
                    $"Apply {SelectedFixIds.Count} fix(es) to the model?\n\n" +
                    "They are applied as one Revit undo step, so Ctrl+Z in Revit reverts all of them.",
                    "Fix All Issues",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question) != MessageBoxResult.Yes)
            {
                return;
            }

            DialogResult = true;
        }

        // Row in the dialog.
        public sealed class FixOption
        {
            private static readonly Brush SafeBack = Make("#E3F5EA");
            private static readonly Brush SafeFore = Make("#159447");
            private static readonly Brush ReviewBack = Make("#FDF1DC");
            private static readonly Brush ReviewFore = Make("#B7791F");

            public FixOption(FixDefinition definition, CheckResult? related)
            {
                Id = definition.Id;
                Name = definition.Name;
                Description = definition.Description;
                Risk = definition.Risk == "Safe" ? "Safe" : "Review first";

                bool safe = definition.Risk == "Safe";
                RiskBackground = safe ? SafeBack : ReviewBack;
                RiskForeground = safe ? SafeFore : ReviewFore;

                if (related == null)
                {
                    FoundText = "Related check not scanned yet.";
                    IsSelected = definition.SelectedByDefault;
                }
                else
                {
                    FoundText = related.IssueCount == 0
                        ? $"{related.CheckName}: no issues found."
                        : $"{related.CheckName}: {related.IssueCount:N0} issue(s) found – the fixable ones will be fixed.";

                    IsSelected = definition.SelectedByDefault && related.IssueCount > 0;
                }
            }

            public string Id { get; }
            public string Name { get; }
            public string Description { get; }
            public string Risk { get; }
            public string FoundText { get; }
            public Brush RiskBackground { get; }
            public Brush RiskForeground { get; }
            public bool IsSelected { get; set; }

            private static Brush Make(string hex)
            {
                var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
                brush.Freeze();
                return brush;
            }
        }
    }
}
