using BIMModelHealthDashboard.Models;

namespace BIMModelHealthDashboard.Checks
{
    public interface IModelCheck
    {
        // Short stable id, e.g. "E1".
        string Id { get; }

        // Name shown on the dashboard, matching the requirement list.
        string Name { get; }

        // One of CheckGroups.
        string Group { get; }

        string Description { get; }

        // Runs on the Revit API thread. Adds issues to the result.
        void Run(CheckContext context, CheckResult result);
    }
}
