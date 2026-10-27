using BIMModelHealthDashboard.Models;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;

namespace BIMModelHealthDashboard.Views
{
    // One requirement line in a checker card, e.g. "• Unconnected walls   12 issues".
    public sealed class CheckRowViewModel : INotifyPropertyChanged
    {
        private static readonly Brush Grey = Make("#8A97AB");
        private static readonly Brush Green = Make("#159447");
        private static readonly Brush Amber = Make("#B7791F");
        private static readonly Brush Orange = Make("#DD6B20");
        private static readonly Brush Red = Make("#D64545");

        private string _resultText = "Not run";
        private Brush _resultBrush = Grey;
        private string _toolTip;

        public CheckRowViewModel(string checkId, string name, string group, string description)
        {
            CheckId = checkId;
            Name = name;
            Group = group;
            Description = description;
            _toolTip = description;
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        public string CheckId { get; }
        public string Name { get; }
        public string Group { get; }
        public string Description { get; }

        public string Bullet => "• " + Name;

        public string ResultText
        {
            get => _resultText;
            private set { _resultText = value; OnPropertyChanged(); }
        }

        public Brush ResultBrush
        {
            get => _resultBrush;
            private set { _resultBrush = value; OnPropertyChanged(); }
        }

        public string ToolTip
        {
            get => _toolTip;
            private set { _toolTip = value; OnPropertyChanged(); }
        }

        public void Apply(CheckResult? result)
        {
            if (result == null)
            {
                ResultText = "Not run";
                ResultBrush = Grey;
                ToolTip = Description;
                return;
            }

            ToolTip = Description + "\n\n" + result.Summary + "\n\nClick to view details.";

            switch (result.Status)
            {
                case CheckStatus.Passed:
                    ResultText = "✓ OK";
                    ResultBrush = Green;
                    break;

                case CheckStatus.Failed:
                    ResultText = $"{result.IssueCount:N0} issue{(result.IssueCount == 1 ? "" : "s")}";
                    ResultBrush = result.WorstSeverity >= IssueSeverity.High ? Red : Orange;
                    break;

                case CheckStatus.NotConfigured:
                    ResultText = "Rules needed";
                    ResultBrush = Amber;
                    break;

                case CheckStatus.NotApplicable:
                    ResultText = "N/A";
                    ResultBrush = Grey;
                    break;

                case CheckStatus.Error:
                    ResultText = "Error";
                    ResultBrush = Red;
                    break;

                default:
                    ResultText = "Not run";
                    ResultBrush = Grey;
                    break;
            }
        }

        private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

        private static Brush Make(string hex)
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
            brush.Freeze();
            return brush;
        }
    }
}
