using Autodesk.Revit.DB;
using BIMModelHealthDashboard.Config;
using BIMModelHealthDashboard.Models;
using System;
using System.Collections.Generic;
using System.Linq;

namespace BIMModelHealthDashboard.Checks
{
    // E1 ------------------------------------------------------------------
    public sealed class MissingParametersCheck : IModelCheck
    {
        public string Id => "E1";
        public string Name => "Missing/incorrect parameters";
        public string Group => CheckGroups.Element;
        public string Description =>
            "Elements in the configured categories must have the required parameters " +
            "filled in, in the required format.";

        public void Run(CheckContext context, CheckResult result)
        {
            List<RequiredParameterRule> rules = context.Rules.RequiredParameters
                .Where(r => r.Parameters.Count > 0)
                .ToList();

            if (rules.Count == 0)
            {
                result.MarkNotConfigured("No required-parameter rules are defined in the rules file.");
                return;
            }

            var problemsByElement = new Dictionary<long, (Element Element, List<string> Problems)>();
            var checkedIds = new HashSet<long>();

            foreach (RequiredParameterRule rule in rules)
            {
                HashSet<BuiltInCategory> categories = context.ResolveCategories(rule.Categories);

                if (categories.Count == 0)
                    continue;

                foreach (Element element in context.ElementsOfCategories(categories))
                {
                    if (CheckContext.IsNestedComponent(element))
                        continue;

                    long id = element.Id.Value;
                    checkedIds.Add(id);

                    List<string> problems =
                        ParameterRuleEvaluator.Evaluate(context, element, rule.Parameters);

                    if (problems.Count == 0)
                        continue;

                    if (!problemsByElement.TryGetValue(id, out var entry))
                    {
                        entry = (element, new List<string>());
                        problemsByElement[id] = entry;
                    }

                    entry.Problems.AddRange(problems);
                }
            }

            foreach (var entry in problemsByElement.Values)
            {
                context.AddElementIssue(
                    result,
                    entry.Element,
                    IssueSeverity.Medium,
                    string.Join("; ", entry.Problems.Distinct()));
            }

            result.CheckedCount = checkedIds.Count;
            result.Summary = checkedIds.Count == 0
                ? "No elements found in the configured categories."
                : $"{checkedIds.Count - problemsByElement.Count:N0} of {checkedIds.Count:N0} elements have complete parameters.";
        }
    }

    // E2 ------------------------------------------------------------------
    public sealed class WrongFamilyTypesCheck : IModelCheck
    {
        public string Id => "E2";
        public string Name => "Wrong family types";
        public string Group => CheckGroups.Element;
        public string Description =>
            "Placed types must be approved for their category and must not be " +
            "placeholder/default types or Generic Models.";

        public void Run(CheckContext context, CheckResult result)
        {
            FamilyTypeRules rules = context.Rules.FamilyTypes;

            var categoryRules = rules.Categories
                .Select(r => (Categories: context.ResolveCategories(new[] { r.Category }), Rule: r))
                .Where(x => x.Categories.Count > 0)
                .ToList();

            var instancesByType = context.ModelElements
                .Where(e => !CheckContext.IsNestedComponent(e))
                .Where(e => e.GetTypeId() != ElementId.InvalidElementId)
                .GroupBy(e => e.GetTypeId().Value);

            int checkedTypes = 0;

            foreach (var group in instancesByType)
            {
                if (!(context.Doc.GetElement(new ElementId(group.Key)) is ElementType type))
                    continue;

                Element first = group.First();
                Category category = first.Category!;
                BuiltInCategory bic = category.BuiltInCategory;
                checkedTypes++;

                var reasons = new List<string>();

                foreach (var categoryRule in categoryRules.Where(x => x.Categories.Contains(bic)))
                {
                    CategoryFamilyRule rule = categoryRule.Rule;

                    if (rule.AllowedFamilyNamePatterns.Count > 0 &&
                        !context.Patterns.MatchesAny(rule.AllowedFamilyNamePatterns, type.FamilyName))
                    {
                        reasons.Add($"family '{type.FamilyName}' is not an approved {category.Name} family");
                    }

                    if (rule.AllowedTypeNamePatterns.Count > 0 &&
                        !context.Patterns.MatchesAny(rule.AllowedTypeNamePatterns, type.Name))
                    {
                        reasons.Add($"type '{type.Name}' is not an approved {category.Name} type");
                    }
                }

                if (context.Patterns.MatchesAny(rules.DisallowedTypeNamePatterns, type.Name))
                    reasons.Add($"type '{type.Name}' is a placeholder/default type");

                if (rules.FlagGenericModelInstances && bic == BuiltInCategory.OST_GenericModel)
                    reasons.Add("modelled as Generic Model – use the correct Revit category");

                if (reasons.Count == 0)
                    continue;

                int count = group.Count();

                result.AddIssue(
                    IssueSeverity.Medium,
                    $"{category.Name}: {type.FamilyName} : {type.Name}",
                    category.Name,
                    $"{count:N0} instance(s): {string.Join("; ", reasons)}",
                    group.Select(e => e.Id.Value));
            }

            result.CheckedCount = checkedTypes;
            result.Summary = $"{checkedTypes:N0} placed types checked; {result.IssueCount:N0} not compliant.";
        }
    }

    // E3 ------------------------------------------------------------------
    public sealed class DuplicateElementsCheck : IModelCheck
    {
        public string Id => "E3";
        public string Name => "Duplicate elements";
        public string Group => CheckGroups.Element;
        public string Description =>
            "Finds elements of the same category and type placed on top of each other " +
            "(same location, rotation and geometry extents).";

        public void Run(CheckContext context, CheckResult result)
        {
            DuplicateRules rules = context.Rules.Duplicates;
            HashSet<BuiltInCategory> ignore = context.ResolveCategories(rules.IgnoreCategories);
            double tolerance = context.MmToFeet(Math.Max(0.01, rules.ToleranceMm));

            var buckets = new Dictionary<string, List<Element>>();
            int checkedCount = 0;

            foreach (Element element in context.ModelElements)
            {
                if (ignore.Contains(element.Category!.BuiltInCategory) ||
                    CheckContext.IsNestedComponent(element))
                {
                    continue;
                }

                string? key = BuildKey(element, tolerance);

                if (key == null)
                    continue;

                checkedCount++;

                if (!buckets.TryGetValue(key, out List<Element>? list))
                {
                    list = new List<Element>();
                    buckets[key] = list;
                }

                list.Add(element);
            }

            foreach (List<Element> duplicates in buckets.Values.Where(l => l.Count > 1))
            {
                Element first = duplicates[0];

                result.AddIssue(
                    IssueSeverity.High,
                    context.Describe(first),
                    first.Category?.Name ?? string.Empty,
                    $"{duplicates.Count} identical elements in the same location – delete the extra copies.",
                    duplicates.Select(e => e.Id.Value));
            }

            result.CheckedCount = checkedCount;
            result.Summary = $"{result.IssueCount:N0} duplicate set(s) among {checkedCount:N0} elements.";
        }

        private static string? BuildKey(Element element, double tolerance)
        {
            string category = element.Category!.Id.Value.ToString();
            string type = element.GetTypeId().Value.ToString();

            string P(XYZ p) =>
                $"{Math.Round(p.X / tolerance)},{Math.Round(p.Y / tolerance)},{Math.Round(p.Z / tolerance)}";

            switch (element.Location)
            {
                case LocationPoint point:
                {
                    double rotation = 0;

                    if (element is FamilyInstance instance)
                    {
                        XYZ basisX = instance.GetTransform().BasisX;
                        rotation = Math.Round(Math.Atan2(basisX.Y, basisX.X), 2);
                    }

                    return $"P|{category}|{type}|{P(point.Point)}|{rotation}";
                }

                case LocationCurve curveLocation when curveLocation.Curve != null && curveLocation.Curve.IsBound:
                {
                    Curve curve = curveLocation.Curve;
                    string a = P(curve.GetEndPoint(0));
                    string b = P(curve.GetEndPoint(1));
                    string mid = P(curve.Evaluate(0.5, true));

                    if (string.CompareOrdinal(a, b) > 0)
                        (a, b) = (b, a);

                    // Walls overlap regardless of type, so type is ignored for walls.
                    string typePart = element is Wall ? "wall" : type;

                    return $"C|{category}|{typePart}|{a}|{b}|{mid}";
                }

                default:
                {
                    BoundingBoxXYZ? box = element.get_BoundingBox(null);

                    if (box == null)
                        return null;

                    return $"B|{category}|{type}|{element.LevelId.Value}|{P(box.Min)}|{P(box.Max)}";
                }
            }
        }
    }

    // E4 ------------------------------------------------------------------
    public sealed class DoorWindowParametersCheck : IModelCheck
    {
        public string Id => "E4";
        public string Name => "Doors/windows without required parameters";
        public string Group => CheckGroups.Element;
        public string Description =>
            "Doors and windows must have their required parameters, a host, " +
            "and (for doors) a From/To Room.";

        public void Run(CheckContext context, CheckResult result)
        {
            DoorWindowRules rules = context.Rules.DoorsAndWindows;

            HashSet<BuiltInCategory> categories = context.ResolveCategories(rules.Categories);
            HashSet<BuiltInCategory> roomCategories = context.ResolveCategories(rules.RequireRoomAssociationFor);

            var requirements = rules.Requirements
                .Select(r => (Categories: context.ResolveCategories(r.Categories), Rule: r))
                .ToList();

            foreach (var requirement in requirements)
                categories.UnionWith(requirement.Categories);

            if (categories.Count == 0)
            {
                result.MarkNotConfigured("No door/window categories configured.");
                return;
            }

            bool modelHasPlacedRooms = context.Rooms.Any(r => r.Location != null && r.Area > 0);
            int checkedCount = 0;

            foreach (Element element in context.ElementsOfCategories(categories))
            {
                if (!(element is FamilyInstance instance) || instance.SuperComponent != null)
                    continue;

                checkedCount++;
                BuiltInCategory bic = element.Category!.BuiltInCategory;
                var problems = new List<string>();
                IssueSeverity severity = IssueSeverity.Medium;

                foreach (var requirement in requirements.Where(r => r.Categories.Contains(bic)))
                    problems.AddRange(ParameterRuleEvaluator.Evaluate(context, element, requirement.Rule.Parameters));

                if (rules.RequireHost && instance.Host == null)
                {
                    problems.Add("not hosted in a wall/roof");
                    severity = IssueSeverity.High;
                }

                if (modelHasPlacedRooms &&
                    roomCategories.Contains(bic) &&
                    SafeRoom(() => instance.FromRoom) == null &&
                    SafeRoom(() => instance.ToRoom) == null)
                {
                    problems.Add("no From Room / To Room (not bounding any room)");
                }

                if (problems.Count > 0)
                    context.AddElementIssue(result, element, severity, string.Join("; ", problems.Distinct()));
            }

            result.CheckedCount = checkedCount;
            result.Summary = checkedCount == 0
                ? "No doors or windows in the model."
                : $"{checkedCount - result.IssueCount:N0} of {checkedCount:N0} doors/windows complete.";
        }

        private static Autodesk.Revit.DB.Architecture.Room? SafeRoom(
            Func<Autodesk.Revit.DB.Architecture.Room?> getter)
        {
            try
            {
                return getter();
            }
            catch (Autodesk.Revit.Exceptions.ApplicationException)
            {
                return null;
            }
        }
    }
}
