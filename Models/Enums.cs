namespace BIMModelHealthDashboard.Models
{
    // Ordered from least to most serious so Max() returns the worst.
    public enum IssueSeverity
    {
        Info = 0,
        Low = 1,
        Medium = 2,
        High = 3,
        Critical = 4
    }

    public enum CheckStatus
    {
        NotRun,
        Passed,
        Failed,
        NotConfigured,
        NotApplicable,
        Error
    }

    // The four checker sections shown on the dashboard.
    public static class CheckGroups
    {
        public const string Element = "Element Checker";
        public const string Wall = "Wall Checker";
        public const string Room = "Room Checker";
        public const string Standards = "Standards & Compliance";

        public static readonly string[] All =
        {
            Element, Wall, Room, Standards
        };
    }
}
