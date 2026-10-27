using Autodesk.Revit.DB;
using BIMModelHealthDashboard.Config;
using BIMModelHealthDashboard.Models;
using System;
using System.Collections.Generic;
using System.Linq;

namespace BIMModelHealthDashboard.Checks
{
    // W1 ------------------------------------------------------------------
    public sealed class RevitWarningsCheck : IModelCheck
    {
        public string Id => "W1";
        public string Name => "Warnings in the Revit model";
        public string Group => CheckGroups.Wall;
        public string Description =>
            "Lists every native Revit warning with the elements involved. " +
            "Severity is set by the warning patterns in the rules file.";

        public void Run(CheckContext context, CheckResult result)
        {
            WarningRules rules = context.Rules.Warnings;
            IList<FailureMessage> warnings = context.Warnings;
            var countsByText = new Dictionary<string, int>();

            foreach (FailureMessage warning in warnings)
            {
                string text = warning.GetDescriptionText() ?? "Unknown warning";

                if (context.Patterns.MatchesAny(rules.IgnorePatterns, text))
                    continue;

                IssueSeverity severity =
                    context.Patterns.MatchesAny(rules.HighSeverityPatterns, text) ? IssueSeverity.High :
                    context.Patterns.MatchesAny(rules.LowSeverityPatterns, text) ? IssueSeverity.Low :
                    IssueSeverity.Medium;

                List<ElementId> ids = warning.GetFailingElements()
                    .Concat(warning.GetAdditionalElements())
                    .Distinct()
                    .ToList();

                Element? first = ids
                    .Select(id => context.Doc.GetElement(id))
                    .FirstOrDefault(e => e != null);

                result.AddIssue(
                    severity,
                    first != null ? context.Describe(first) : "Model",
                    first?.Category?.Name ?? string.Empty,
                    text,
                    ids.Select(id => id.Value));

                countsByText[text] = countsByText.TryGetValue(text, out int n) ? n + 1 : 1;
            }

            // Extent is measured against the model size (used by the health score).
            result.CheckedCount = context.ModelElements.Count;

            string top = string.Join(" | ", countsByText
                .OrderByDescending(p => p.Value)
                .Take(3)
                .Select(p => $"{p.Value:N0}× {Shorten(p.Key, 60)}"));

            result.Summary = result.IssueCount == 0
                ? "No Revit warnings."
                : $"{result.IssueCount:N0} warnings of {countsByText.Count:N0} kinds. Most common: {top}";
        }

        private static string Shorten(string text, int max) =>
            text.Length <= max ? text : text.Substring(0, max - 1) + "…";
    }

    // W2 ------------------------------------------------------------------
    public sealed class UnconnectedWallsCheck : IModelCheck
    {
        public string Id => "W2";
        public string Name => "Unconnected walls";
        public string Group => CheckGroups.Wall;
        public string Description =>
            "Wall ends that lie within the connection tolerance of another wall but where joining is " +
            "disallowed (Revit Wall Checker logic).";

        // Same logic as the Revit Wall Checker's CheckUnconnectedWalls:
        // for each wall end, find a nearby wall within the tolerance; if one
        // exists and joining is disallowed at that end, report it.
        public void Run(CheckContext context, CheckResult result)
        {
            WallRules rules = context.Rules.Walls;
            Document doc = context.Doc;

            if (!rules.RequireConnectionWhenNearbyWall)
            {
                result.MarkNotConfigured("Disabled in the rules file (Walls.RequireConnectionWhenNearbyWall).");
                return;
            }

            double tolerance = Math.Max(0.001, rules.ConnectionToleranceFeet);

            List<Wall> walls = new FilteredElementCollector(doc)
                .OfClass(typeof(Wall))
                .WhereElementIsNotElementType()
                .Cast<Wall>()
                .ToList();

            foreach (Wall wall in walls)
            {
                if (!(wall.Location is LocationCurve location) || location.Curve == null)
                    continue;

                Curve curve = location.Curve;

                for (int end = 0; end <= 1; end++)
                {
                    XYZ endpoint = curve.GetEndPoint(end);
                    Wall? nearbyWall = FindNearbyWall(doc, endpoint, wall.Id, tolerance);

                    if (nearbyWall == null)
                        continue;

                    bool joinAllowed;

                    try
                    {
                        joinAllowed = WallUtils.IsWallJoinAllowedAtEnd(wall, end);
                    }
                    catch (Autodesk.Revit.Exceptions.ApplicationException)
                    {
                        result.AddIssue(
                            IssueSeverity.Medium,
                            context.Describe(wall),
                            "Walls",
                            $"Could not determine join permission at wall end {end + 1} " +
                            $"(nearby wall {nearbyWall.Id.Value}).",
                            new[] { wall.Id.Value, nearbyWall.Id.Value });
                        continue;
                    }

                    // Join permission alone does not prove physical connectivity.
                    if (!joinAllowed)
                    {
                        result.AddIssue(
                            IssueSeverity.Medium,
                            context.Describe(wall),
                            "Walls",
                            $"Wall end {end + 1} is within the connection tolerance of wall {nearbyWall.Id.Value}, " +
                            "but joining is disallowed. Review whether these walls should connect " +
                            "(Fix All Issues re-allows the join).",
                            new[] { wall.Id.Value, nearbyWall.Id.Value });
                    }
                }
            }

            result.CheckedCount = walls.Count;
            result.Summary =
                $"{result.IssueCount:N0} wall end(s) next to another wall with joining disallowed, in {walls.Count:N0} walls " +
                $"(tolerance {rules.ConnectionToleranceFeet:0.###} ft).";
        }

        // The Wall Checker's FindNearbyWall: the first other wall whose location
        // line passes within 'tolerance' of the point. Candidates are pre-filtered
        // by bounding box so large models stay fast.
        internal static Wall? FindNearbyWall(Document doc, XYZ point, ElementId sourceId, double tolerance)
        {
            double reach = Math.Max(tolerance, 0.01);
            var offset = new XYZ(reach, reach, reach);

            IEnumerable<Wall> candidates = new FilteredElementCollector(doc)
                .OfClass(typeof(Wall))
                .WherePasses(new BoundingBoxIntersectsFilter(new Outline(point - offset, point + offset)))
                .Excluding(new List<ElementId> { sourceId })
                .Cast<Wall>();

            foreach (Wall other in candidates)
            {
                if (!(other.Location is LocationCurve otherLocation) || otherLocation.Curve == null)
                    continue;

                try
                {
                    IntersectionResult? projection = otherLocation.Curve.Project(point);

                    if (projection != null && projection.Distance <= tolerance)
                        return other;
                }
                catch (Autodesk.Revit.Exceptions.ApplicationException)
                {
                }
            }

            return null;
        }

        // Also used by Fix All Issues, so the fix targets exactly what the check reports.
        internal static bool IsEndConnected(
            Document doc,
            Wall wall,
            LocationCurve location,
            int end,
            double tolerance)
        {
            // 1. Revit wall joins at this end.
            try
            {
                foreach (Element joined in location.get_ElementsAtJoin(end))
                {
                    if (joined != null && joined.Id != wall.Id && joined is Wall)
                        return true;
                }
            }
            catch (Autodesk.Revit.Exceptions.ApplicationException)
            {
            }

            // 2. Geometric touch: the end point lies on/in another wall or a column
            //    (covers T-junctions, disallowed joins and walls ending at columns).
            XYZ point = location.Curve.GetEndPoint(end);
            var offset = new XYZ(tolerance, tolerance, tolerance);
            var outline = new Outline(point - offset, point + offset);

            var categories = new List<BuiltInCategory>
            {
                BuiltInCategory.OST_Walls,
                BuiltInCategory.OST_Columns,
                BuiltInCategory.OST_StructuralColumns
            };

            IEnumerable<Element> nearby = new FilteredElementCollector(doc)
                .WherePasses(new ElementMulticategoryFilter(categories))
                .WhereElementIsNotElementType()
                .WherePasses(new BoundingBoxIntersectsFilter(outline))
                .Excluding(new List<ElementId> { wall.Id });

            foreach (Element element in nearby)
            {
                if (!(element is Wall other))
                    return true; // a column at the wall end

                if (!(other.Location is LocationCurve otherLocation) || otherLocation.Curve == null)
                    continue;

                Curve otherCurve = otherLocation.Curve;
                var flattened = new XYZ(point.X, point.Y, otherCurve.GetEndPoint(0).Z);
                IntersectionResult? projection = otherCurve.Project(flattened);

                if (projection != null && projection.Distance <= other.Width / 2.0 + tolerance)
                    return true;
            }

            return false;
        }
    }

    // W3 ------------------------------------------------------------------
    public sealed class WorksetCheck : IModelCheck
    {
        public string Id => "W3";
        public string Name => "Elements on wrong worksets";
        public string Group => CheckGroups.Wall;
        public string Description =>
            "Checks category-to-workset rules and that no model elements remain on " +
            "disallowed worksets (e.g. Workset1).";

        public void Run(CheckContext context, CheckResult result)
        {
            Document doc = context.Doc;

            if (!doc.IsWorkshared)
            {
                result.MarkNotApplicable("Model is not workshared.");
                return;
            }

            WorksetRules rules = context.Rules.Worksets;
            WorksetTable table = doc.GetWorksetTable();
            var worksetCache = new Dictionary<int, Workset?>();

            Workset? GetUserWorkset(Element element)
            {
                WorksetId id = element.WorksetId;

                if (id == WorksetId.InvalidWorksetId)
                    return null;

                if (!worksetCache.TryGetValue(id.IntegerValue, out Workset? workset))
                {
                    workset = table.GetWorkset(id);
                    worksetCache[id.IntegerValue] = workset;
                }

                return workset != null && workset.Kind == WorksetKind.UserWorkset ? workset : null;
            }

            // Group results: (category, workset, reason) -> element ids
            var grouped = new Dictionary<(string Category, string Workset, string Reason), List<long>>();
            var flagged = new HashSet<long>();
            var checkedIds = new HashSet<long>();

            void Flag(Element element, string worksetName, string reason)
            {
                var key = (element.Category?.Name ?? "No category", worksetName, reason);

                if (!grouped.TryGetValue(key, out List<long>? ids))
                {
                    ids = new List<long>();
                    grouped[key] = ids;
                }

                ids.Add(element.Id.Value);
                flagged.Add(element.Id.Value);
            }

            foreach (WorksetAssignmentRule rule in rules.Assignments)
            {
                if (string.IsNullOrWhiteSpace(rule.ExpectedWorksetPattern))
                    continue;

                HashSet<BuiltInCategory> categories = context.ResolveCategories(rule.Categories);

                if (categories.Count == 0)
                    continue;

                IEnumerable<Element> elements = new FilteredElementCollector(doc)
                    .WherePasses(new ElementMulticategoryFilter(categories.ToList()))
                    .WhereElementIsNotElementType();

                foreach (Element element in elements)
                {
                    Workset? workset = GetUserWorkset(element);

                    if (workset == null)
                        continue;

                    checkedIds.Add(element.Id.Value);

                    if (!context.Patterns.IsMatch(rule.ExpectedWorksetPattern, workset.Name))
                    {
                        Flag(element, workset.Name,
                            $"should be on {rule.Description ?? "a workset matching " + rule.ExpectedWorksetPattern}");
                    }
                }
            }

            if (rules.DisallowedWorksetPatterns.Count > 0)
            {
                foreach (Element element in context.ModelElements)
                {
                    Workset? workset = GetUserWorkset(element);

                    if (workset == null)
                        continue;

                    checkedIds.Add(element.Id.Value);

                    if (!flagged.Contains(element.Id.Value) &&
                        context.Patterns.MatchesAny(rules.DisallowedWorksetPatterns, workset.Name))
                    {
                        Flag(element, workset.Name, "model elements must not be on this workset");
                    }
                }
            }

            foreach (var pair in grouped.OrderBy(p => p.Key.Category))
            {
                result.AddIssue(
                    IssueSeverity.Medium,
                    $"{pair.Key.Category} on '{pair.Key.Workset}'",
                    pair.Key.Category,
                    $"{pair.Value.Count:N0} element(s) on workset '{pair.Key.Workset}' – {pair.Key.Reason}.",
                    pair.Value);
            }

            result.CheckedCount = checkedIds.Count;
            result.Summary = $"{flagged.Count:N0} of {checkedIds.Count:N0} elements on the wrong workset.";
        }
    }

    // W4 ------------------------------------------------------------------
    public sealed class MaterialsCheck : IModelCheck
    {
        public string Id => "W4";
        public string Name => "Incorrect materials";
        public string Group => CheckGroups.Wall;
        public string Description =>
            "Flags missing (<By Category>) materials and materials that are " +
            "default, duplicated or not in the approved library.";

        public void Run(CheckContext context, CheckResult result)
        {
            MaterialRules rules = context.Rules.Materials;
            Document doc = context.Doc;

            HashSet<BuiltInCategory> checkCategories = context.ResolveCategories(rules.CheckCategories);
            HashSet<BuiltInCategory> requireCategories = context.ResolveCategories(rules.RequireMaterialCategories);
            var allCategories = new HashSet<BuiltInCategory>(checkCategories);
            allCategories.UnionWith(requireCategories);

            if (allCategories.Count == 0)
            {
                result.MarkNotConfigured("No material categories configured.");
                return;
            }

            var usersByMaterial = new Dictionary<long, List<long>>();
            var missingByType = new Dictionary<long, (string Name, string Category, string Reason, List<long> Ids)>();
            int checkedCount = 0;

            foreach (Element element in context.ElementsOfCategories(allCategories))
            {
                if (CheckContext.IsNestedComponent(element))
                    continue;

                checkedCount++;
                BuiltInCategory bic = element.Category!.BuiltInCategory;

                ICollection<ElementId> materialIds;

                try
                {
                    materialIds = element.GetMaterialIds(false);
                }
                catch (Autodesk.Revit.Exceptions.ApplicationException)
                {
                    continue;
                }

                if (checkCategories.Contains(bic))
                {
                    foreach (ElementId materialId in materialIds)
                    {
                        if (!usersByMaterial.TryGetValue(materialId.Value, out List<long>? users))
                        {
                            users = new List<long>();
                            usersByMaterial[materialId.Value] = users;
                        }

                        users.Add(element.Id.Value);
                    }
                }

                if (!requireCategories.Contains(bic))
                    continue;

                string? reason = GetMissingMaterialReason(context, element, materialIds);

                if (reason == null)
                    continue;

                long typeKey = element.GetTypeId().Value;

                if (!missingByType.TryGetValue(typeKey, out var entry))
                {
                    entry = (context.Describe(element), element.Category.Name, reason, new List<long>());
                    missingByType[typeKey] = entry;
                }

                entry.Ids.Add(element.Id.Value);
            }

            foreach (var entry in missingByType.Values)
            {
                result.AddIssue(
                    IssueSeverity.Medium,
                    entry.Name,
                    entry.Category,
                    $"{entry.Ids.Count:N0} element(s): {entry.Reason}.",
                    entry.Ids);
            }

            foreach (var pair in usersByMaterial)
            {
                if (!(doc.GetElement(new ElementId(pair.Key)) is Material material))
                    continue;

                var reasons = new List<string>();

                if (rules.AllowedNamePatterns.Count > 0 &&
                    !context.Patterns.MatchesAny(rules.AllowedNamePatterns, material.Name))
                {
                    reasons.Add("not in the approved material library");
                }

                if (context.Patterns.MatchesAny(rules.DisallowedNamePatterns, material.Name))
                    reasons.Add("default, generic or duplicated material name");

                if (reasons.Count == 0)
                    continue;

                result.AddIssue(
                    IssueSeverity.Medium,
                    $"Material: {material.Name}",
                    "Materials",
                    $"Used by {pair.Value.Distinct().Count():N0} element(s) – {string.Join("; ", reasons)}.",
                    pair.Value);
            }

            result.CheckedCount = checkedCount;
            result.Summary =
                $"{missingByType.Count:N0} type(s) with missing materials; " +
                $"{result.IssueCount - missingByType.Count:N0} non-standard material(s).";
        }

        private static string? GetMissingMaterialReason(
            CheckContext context,
            Element element,
            ICollection<ElementId> materialIds)
        {
            if (element is HostObject &&
                context.GetTypeElement(element) is HostObjAttributes hostType)
            {
                CompoundStructure? structure = hostType.GetCompoundStructure();

                if (structure != null)
                {
                    List<int> emptyLayers = structure.GetLayers()
                        .Select((layer, index) => (layer, index))
                        .Where(x => x.layer.MaterialId == ElementId.InvalidElementId)
                        .Select(x => x.index + 1)
                        .ToList();

                    return emptyLayers.Count == 0
                        ? null
                        : $"layer(s) {string.Join(", ", emptyLayers)} have no material (<By Category>)";
                }
            }

            return materialIds.Count == 0
                ? "no material assigned (<By Category>)"
                : null;
        }
    }
}
