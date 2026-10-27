using System.Collections.Generic;
using System.Linq;

namespace BIMModelHealthDashboard.Models
{
    public class ValidationIssue
    {
        public string CheckId { get; set; } = string.Empty;
        public string CheckName { get; set; } = string.Empty;
        public string Group { get; set; } = string.Empty;

        public IssueSeverity Severity { get; set; } = IssueSeverity.Medium;

        // What the issue is about, e.g. "Doors: Single-Flush : 900 x 2100mm".
        public string ElementName { get; set; } = string.Empty;
        public string Category { get; set; } = string.Empty;
        public string Message { get; set; } = string.Empty;

        // Revit ElementId values (ElementId.Value) used to select/zoom in Revit.
        public List<long> ElementIds { get; set; } = new List<long>();

        public string SeverityText => Severity.ToString();

        public string ElementIdsText
        {
            get
            {
                if (ElementIds.Count == 0)
                    return string.Empty;

                if (ElementIds.Count <= 5)
                    return string.Join(", ", ElementIds);

                return string.Join(", ", ElementIds.Take(5))
                       + $" … (+{ElementIds.Count - 5:N0})";
            }
        }
    }
}
