namespace BIMModelHealthDashboard.Models
{
    public enum IndicatorStatus
    {
        Info,
        Good,
        Warning,
        Bad
    }

    // One row in the Project readiness / Model organization / Dependencies panels,
    // e.g. "Imported CAD files: 3" with a red status and a tip.
    public sealed class HealthIndicator
    {
        public HealthIndicator(string label, string value, IndicatorStatus status, string tip)
        {
            Label = label;
            Value = value;
            Status = status;
            Tip = tip;
        }

        public string Label { get; }
        public string Value { get; }
        public IndicatorStatus Status { get; }

        // Why it matters and what the target is (shown as a tooltip).
        public string Tip { get; }
    }
}
