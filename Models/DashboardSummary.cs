using System.Collections.Generic;

namespace BIMModelHealthDashboard.Models
{
    // Model metrics shown in the top cards and the three health panels.
    // Validation results are held separately in ValidationReport.
    public class DashboardSummary
    {
        // Identifies the document (path, or title for unsaved models).
        public string ModelKey { get; set; } = string.Empty;

        // Active Revit model information
        public string ModelFileName { get; set; } = "Unknown model";
        public string ModelFilePath { get; set; } = "File location unavailable";
        public string FileSize { get; set; } = "N/A";

        // Top cards
        public int TotalFamilies { get; set; }
        public int TotalElements { get; set; }
        public int RevitWarnings { get; set; }

        // Health panels: Project readiness, Model organization, Dependencies
        // (each row has a traffic-light status and a tip).
        public List<HealthIndicator> ProjectReadiness { get; } = new List<HealthIndicator>();
        public List<HealthIndicator> ModelOrganization { get; } = new List<HealthIndicator>();
        public List<HealthIndicator> Dependencies { get; } = new List<HealthIndicator>();
    }
}
