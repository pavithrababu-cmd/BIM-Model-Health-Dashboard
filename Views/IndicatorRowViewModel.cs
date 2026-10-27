using BIMModelHealthDashboard.Models;
using System.Windows.Media;

namespace BIMModelHealthDashboard.Views
{
    // A health indicator ready for display (status turned into a colour).
    public sealed class IndicatorRowViewModel
    {
        private static readonly Brush Good = Make("#159447");
        private static readonly Brush Warning = Make("#B7791F");
        private static readonly Brush Bad = Make("#D64545");
        private static readonly Brush Info = Make("#61738E");

        public string Label { get; private set; } = string.Empty;
        public string Value { get; private set; } = string.Empty;
        public string Tip { get; private set; } = string.Empty;
        public Brush StatusBrush { get; private set; } = Info;

        public static IndicatorRowViewModel From(HealthIndicator indicator) =>
            new IndicatorRowViewModel
            {
                Label = indicator.Label,
                Value = indicator.Value,
                Tip = indicator.Tip,
                StatusBrush =
                    indicator.Status == IndicatorStatus.Good ? Good :
                    indicator.Status == IndicatorStatus.Warning ? Warning :
                    indicator.Status == IndicatorStatus.Bad ? Bad :
                    Info
            };

        private static Brush Make(string hex)
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
            brush.Freeze();
            return brush;
        }
    }
}
