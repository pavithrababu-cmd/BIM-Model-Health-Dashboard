using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using BIMModelHealthDashboard.Config;
using BIMModelHealthDashboard.Models;
using System;
using System.Collections.Generic;
using System.Linq;

namespace BIMModelHealthDashboard.Checks
{
    // R1 ------------------------------------------------------------------
    public sealed class RoomsNotEnclosedCheck : IModelCheck
    {
        public string Id => "R1";
        public string Name => "Rooms not properly enclosed";
        public string Group => CheckGroups.Room;
        public string Description =>
            "Placed rooms with zero area: either not enclosed by room-bounding " +
            "elements or redundant (sharing a region with another room).";

        public void Run(CheckContext context, CheckResult result)
        {
            // Rooms reported by Revit as redundant (same enclosed region).
            var redundant = new HashSet<long>();

            foreach (FailureMessage warning in context.Warnings)
            {
                string text = warning.GetDescriptionText() ?? string.Empty;

                if (text.IndexOf("same enclosed region", StringComparison.OrdinalIgnoreCase) < 0 &&
                    text.IndexOf("redundant", StringComparison.OrdinalIgnoreCase) < 0)
                {
                    continue;
                }

                foreach (ElementId id in warning.GetFailingElements())
                    redundant.Add(id.Value);
            }

            int checkedCount = 0;

            foreach (Room room in context.Rooms.Where(r => r.Location != null))
            {
                checkedCount++;

                if (room.Area > 1e-6)
                    continue;

                string message = redundant.Contains(room.Id.Value)
                    ? "Redundant room – another room already occupies this enclosed region."
                    : "Not enclosed – room-bounding elements or separation lines leave the boundary open (area = 0).";

                string level = room.Level?.Name ?? "unknown level";
                context.AddElementIssue(result, room, IssueSeverity.High, $"{message} Level: {level}.");
            }

            result.CheckedCount = checkedCount;
            result.Summary = checkedCount == 0
                ? "No placed rooms in the model."
                : $"{result.IssueCount:N0} of {checkedCount:N0} placed rooms are not properly enclosed.";
        }
    }

    // R2 ------------------------------------------------------------------
    public sealed class UnplacedRoomsCheck : IModelCheck
    {
        public string Id => "R2";
        public string Name => "Unplaced rooms";
        public string Group => CheckGroups.Room;
        public string Description =>
            "Rooms that exist in the project (e.g. in schedules) but are not placed in any plan.";

        public void Run(CheckContext context, CheckResult result)
        {
            IReadOnlyList<Room> rooms = context.Rooms;

            foreach (Room room in rooms.Where(r => r.Location == null))
            {
                context.AddElementIssue(
                    result,
                    room,
                    IssueSeverity.Medium,
                    "Room is not placed. Place it in a plan or delete it.");
            }

            result.CheckedCount = rooms.Count;
            result.Summary = $"{result.IssueCount:N0} of {rooms.Count:N0} rooms are unplaced.";
        }
    }

    // R3 ------------------------------------------------------------------
    public sealed class ViewsCheck : IModelCheck
    {
        public string Id => "R3";
        public string Name => "Duplicate or unused views";
        public string Group => CheckGroups.Room;
        public string Description =>
            "Views whose names indicate a duplicate (e.g. 'Copy 1') and views not placed on any sheet.";

        private static readonly HashSet<ViewType> GraphicalViewTypes = new HashSet<ViewType>
        {
            ViewType.FloorPlan, ViewType.CeilingPlan, ViewType.Elevation,
            ViewType.ThreeD, ViewType.Section, ViewType.Detail,
            ViewType.DraftingView, ViewType.AreaPlan, ViewType.EngineeringPlan,
            ViewType.Walkthrough, ViewType.Rendering
        };

        public void Run(CheckContext context, CheckResult result)
        {
            ViewRules rules = context.Rules.Views;
            Document doc = context.Doc;

            List<View> views = new FilteredElementCollector(doc)
                .OfClass(typeof(View))
                .Cast<View>()
                .Where(v => !v.IsTemplate && IsReportable(v, rules))
                .ToList();

            var placed = new HashSet<long>();

            foreach (Viewport viewport in new FilteredElementCollector(doc).OfClass(typeof(Viewport)).Cast<Viewport>())
                placed.Add(viewport.ViewId.Value);

            foreach (ScheduleSheetInstance schedule in new FilteredElementCollector(doc).OfClass(typeof(ScheduleSheetInstance)).Cast<ScheduleSheetInstance>())
                placed.Add(schedule.ScheduleId.Value);

            int duplicates = 0;
            int unused = 0;

            foreach (View view in views)
            {
                bool isPlaced = placed.Contains(view.Id.Value) ||
                                view.GetDependentViewIds().Any(id => placed.Contains(id.Value));

                bool isDuplicate = context.Patterns.MatchesAny(rules.DuplicateNamePatterns, view.Name);

                bool isUnused = rules.ReportUnusedViews &&
                                !isPlaced &&
                                !context.Patterns.MatchesAny(rules.UnusedIgnorePatterns, view.Name);

                if (!isDuplicate && !isUnused)
                    continue;

                string message;
                IssueSeverity severity;

                if (isDuplicate)
                {
                    duplicates++;
                    severity = IssueSeverity.Medium;
                    message = isPlaced
                        ? "Name indicates a duplicated view – rename it to the project standard."
                        : "Duplicated view that is not on any sheet – delete it or rename and place it.";
                }
                else
                {
                    unused++;
                    severity = IssueSeverity.Low;
                    message = "Not placed on any sheet – delete if no longer needed.";
                }

                result.AddIssue(
                    severity,
                    $"{view.ViewType}: {view.Name}",
                    "Views",
                    message,
                    new[] { view.Id.Value });
            }

            result.CheckedCount = views.Count;
            result.Summary = $"{duplicates:N0} duplicate and {unused:N0} unused of {views.Count:N0} views.";
        }

        private static bool IsReportable(View view, ViewRules rules)
        {
            if (GraphicalViewTypes.Contains(view.ViewType))
                return true;

            if (view.ViewType == ViewType.Legend)
                return rules.IncludeLegends;

            if (view.ViewType == ViewType.Schedule && view is ViewSchedule schedule)
                return rules.IncludeSchedules && !schedule.IsTitleblockRevisionSchedule;

            return false;
        }
    }

    // R4 ------------------------------------------------------------------
    public sealed class IncorrectLevelsCheck : IModelCheck
    {
        public string Id => "R4";
        public string Name => "Incorrect levels";
        public string Group => CheckGroups.Room;
        public string Description =>
            "Elements hosted on a level that does not match where they physically sit, " +
            "levels at the same elevation, and levels with nothing on them.";

        public void Run(CheckContext context, CheckResult result)
        {
            LevelRules rules = context.Rules.Levels;
            IReadOnlyList<Level> levels = context.Levels;

            if (levels.Count == 0)
            {
                result.MarkNotApplicable("The model has no levels.");
                return;
            }

            double tolerance = context.MmToFeet(Math.Max(1.0, rules.ElevationToleranceMm));
            double belowTolerance = context.MetresToFeet(Math.Max(0.0, rules.BelowLevelToleranceM));

            Dictionary<long, Level> levelsById = levels.ToDictionary(l => l.Id.Value);

            // Only building-story levels are used to decide where an element "really" is.
            List<Level> stories = levels.Where(IsBuildingStory).ToList();

            if (stories.Count == 0)
                stories = levels.ToList();

            var usedLevels = new HashSet<long>();
            int checkedCount = 0;

            foreach (Element element in context.ModelElements)
            {
                if (!levelsById.TryGetValue(element.LevelId.Value, out Level? level))
                    continue;

                usedLevels.Add(level.Id.Value);

                double? baseZ = GetBaseElevation(element);

                if (!baseZ.HasValue)
                    continue;

                checkedCount++;
                double offset = baseZ.Value - level.ProjectElevation;

                if (offset < -belowTolerance)
                {
                    context.AddElementIssue(
                        result, element, IssueSeverity.Medium,
                        $"Sits {context.FeetToMetres(-offset):0.00} m below its level '{level.Name}' – re-host it on the correct level.");
                    continue;
                }

                Level? actual = stories.LastOrDefault(l => l.ProjectElevation <= baseZ.Value + tolerance);

                if (actual != null &&
                    actual.Id != level.Id &&
                    actual.ProjectElevation > level.ProjectElevation + tolerance)
                {
                    context.AddElementIssue(
                        result, element, IssueSeverity.Medium,
                        $"Hosted on '{level.Name}' with a +{context.FeetToMetres(offset):0.00} m offset but physically on '{actual.Name}' – re-host it on '{actual.Name}'.");
                }
            }

            if (rules.FlagLevelsAtSameElevation)
            {
                for (int i = 1; i < levels.Count; i++)
                {
                    Level previous = levels[i - 1];
                    Level current = levels[i];

                    if (Math.Abs(current.ProjectElevation - previous.ProjectElevation) <= tolerance)
                    {
                        result.AddIssue(
                            IssueSeverity.Medium,
                            $"Levels: {previous.Name} / {current.Name}",
                            "Levels",
                            "Two levels at the same elevation – one is probably a duplicate.",
                            new[] { previous.Id.Value, current.Id.Value });
                    }
                }
            }

            if (rules.FlagUnusedLevels)
            {
                foreach (Level level in levels.Where(l => !usedLevels.Contains(l.Id.Value)))
                {
                    result.AddIssue(
                        IssueSeverity.Low,
                        $"Level: {level.Name}",
                        "Levels",
                        "No model elements are hosted on this level.",
                        new[] { level.Id.Value });
                }
            }

            result.CheckedCount = checkedCount;
            result.Summary = $"{checkedCount:N0} elements checked against {levels.Count:N0} levels.";
        }

        private static bool IsBuildingStory(Level level)
        {
            Parameter? parameter = level.get_Parameter(BuiltInParameter.LEVEL_IS_BUILDING_STORY);
            return parameter == null || parameter.AsInteger() == 1;
        }

        private static double? GetBaseElevation(Element element)
        {
            if (element is FamilyInstance instance)
            {
                if (instance.SuperComponent != null)
                    return null;

                return instance.Location is LocationPoint point ? point.Point.Z : (double?)null;
            }

            if (element is Wall wall &&
                wall.Location is LocationCurve curve &&
                curve.Curve != null)
            {
                return curve.Curve.GetEndPoint(0).Z;
            }

            return null;
        }
    }
}
