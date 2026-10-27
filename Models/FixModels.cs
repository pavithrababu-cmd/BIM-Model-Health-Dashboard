using System.Collections.Generic;

namespace BIMModelHealthDashboard.Models
{
    // One automatic fix offered by "Fix All Issues".
    public sealed class FixDefinition
    {
        public FixDefinition(string id, string name, string description,
                             string relatedCheckId, bool selectedByDefault, string risk)
        {
            Id = id;
            Name = name;
            Description = description;
            RelatedCheckId = relatedCheckId;
            SelectedByDefault = selectedByDefault;
            Risk = risk;
        }

        public string Id { get; }
        public string Name { get; }
        public string Description { get; }
        public string RelatedCheckId { get; }
        public bool SelectedByDefault { get; }

        // "Safe" or "Review" – shown in the fix dialog.
        public string Risk { get; }
    }

    public static class FixCatalog
    {
        public const string DeleteDuplicates = "DeleteDuplicates";
        public const string PurgeUnusedFamilies = "PurgeUnusedFamilies";
        public const string ConnectWalls = "ConnectWalls";
        public const string ResolveWarnings = "ResolveWarnings";
        public const string FixWorksets = "FixWorksets";
        public const string DeleteUnusedViews = "DeleteUnusedViews";
        public const string DeleteUnplacedRooms = "DeleteUnplacedRooms";
        public const string NumberEmptyMarks = "NumberEmptyMarks";

        // In the order they are applied.
        public static readonly IReadOnlyList<FixDefinition> All = new[]
        {
            new FixDefinition(DeleteDuplicates, "Delete duplicate elements",
                "Deletes the extra copies of identical family instances placed on top of each other " +
                "(keeps the oldest). Elements that host other elements, are pinned or are in groups are skipped.",
                "E3", true, "Safe"),

            new FixDefinition(PurgeUnusedFamilies, "Purge unused families",
                "Deletes loadable model families that have no placed instances. " +
                "A family is only deleted if no element is removed with it.",
                "S1", true, "Safe"),

            new FixDefinition(ConnectWalls, "Connect unconnected walls",
                "Uses the Revit Wall Checker logic: for every wall end that is within the connection tolerance of " +
                "another wall but has joining disallowed, the join is re-allowed. If Revit still doesn't join them, " +
                "the end is extended/trimmed (up to 300 mm) onto the nearby wall. Pinned and grouped walls are skipped.",
                "W2", true, "Review"),

            new FixDefinition(ResolveWarnings, "Resolve common Revit warnings",
                "Fixes the cause of these warning types: identical instances (deletes the extra copy), elements " +
                "joined but not intersecting (unjoins them), duplicate Mark values (renumbers), overlapping room " +
                "separation lines (deletes the duplicate) and room tags outside their room (moves the tag in). " +
                "Other warnings need a person and are listed in the summary.",
                "W1", true, "Safe"),

            new FixDefinition(FixWorksets, "Move elements to the correct workset",
                "Moves elements to the workset required by the workset rules (e.g. levels and grids to the shared " +
                "levels and grids workset, links to the link workset).",
                "W3", true, "Safe"),

            new FixDefinition(DeleteUnusedViews, "Delete unused views",
                "Deletes views that are not placed on any sheet (except open views, the starting view, views with " +
                "dependent views and views matching the unused-view ignore patterns).",
                "R3", false, "Review"),

            new FixDefinition(DeleteUnplacedRooms, "Delete unplaced rooms",
                "Deletes rooms that exist in schedules but are not placed in any plan.",
                "R2", false, "Review"),

            new FixDefinition(NumberEmptyMarks, "Number empty Mark and Type Mark values",
                "Fills empty Mark values with consecutive numbers per category, ordered by level and position " +
                "(e.g. SC-001, SC-002…), and empty Type Mark values per type (e.g. W-T01). Only for categories where " +
                "the rules require Mark or Type Mark; existing values are never changed.",
                "E1", true, "Review")
        };
    }

    // What one fix did.
    public sealed class FixResult
    {
        public string FixId { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public int Fixed { get; set; }
        public int Skipped { get; set; }
        public List<string> Notes { get; } = new List<string>();
        public string? Error { get; set; }
    }
}
