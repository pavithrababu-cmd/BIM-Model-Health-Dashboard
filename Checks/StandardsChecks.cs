using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using BIMModelHealthDashboard.Config;
using BIMModelHealthDashboard.Models;
using System;
using System.Collections.Generic;
using System.Linq;

namespace BIMModelHealthDashboard.Checks
{
    // S1 ------------------------------------------------------------------
    public sealed class FamilyStandardsCheck : IModelCheck
    {
        public string Id => "S1";
        public string Name => "Families that don't meet company standards";
        public string Group => CheckGroups.Standards;
        public string Description =>
            "Loaded families: naming, in-place families, unused families, " +
            "number of types and required type parameters.";

        public void Run(CheckContext context, CheckResult result)
        {
            FamilyStandardRules rules = context.Rules.FamilyStandards;
            Document doc = context.Doc;
            HashSet<BuiltInCategory> unusedIgnore = context.ResolveCategories(rules.UnusedIgnoreCategories);

            List<Family> families = new FilteredElementCollector(doc)
                .OfClass(typeof(Family))
                .Cast<Family>()
                .ToList();

            Dictionary<long, List<long>> instancesByFamily = new FilteredElementCollector(doc)
                .OfClass(typeof(FamilyInstance))
                .Cast<FamilyInstance>()
                .Where(fi => fi.Symbol != null)
                .GroupBy(fi => fi.Symbol.Family.Id.Value)
                .ToDictionary(g => g.Key, g => g.Select(fi => fi.Id.Value).ToList());

            var requiredTypeParameters = rules.RequiredTypeParameters
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .ToList();

            foreach (string name in requiredTypeParameters.Where(n => !ParameterHelper.IsBuiltInNameValid(n)))
                context.ReportConfigError($"Unknown built-in parameter '{name}'.");

            foreach (Family family in families)
            {
                Category? familyCategory = family.FamilyCategory;
                instancesByFamily.TryGetValue(family.Id.Value, out List<long>? instances);
                instances ??= new List<long>();

                var reasons = new List<string>();
                IssueSeverity severity = IssueSeverity.Low;

                void Add(string reason, IssueSeverity s)
                {
                    reasons.Add(reason);
                    if (s > severity) severity = s;
                }

                if (!string.IsNullOrWhiteSpace(rules.RequiredNamePattern) &&
                    !context.Patterns.IsMatch(rules.RequiredNamePattern, family.Name))
                {
                    Add("name does not follow the family naming standard", IssueSeverity.Medium);
                }

                if (rules.DisallowInPlaceFamilies && family.IsInPlace)
                    Add("in-place family – replace with a loadable family", IssueSeverity.High);

                if (rules.FlagUnusedFamilies &&
                    instances.Count == 0 &&
                    !family.IsInPlace &&
                    familyCategory != null &&
                    familyCategory.CategoryType == CategoryType.Model &&
                    !unusedIgnore.Contains(familyCategory.BuiltInCategory))
                {
                    Add("loaded but not placed (purge candidate)", IssueSeverity.Low);
                }

                ISet<ElementId> symbolIds = family.GetFamilySymbolIds();

                if (rules.MaxTypesPerFamily > 0 && symbolIds.Count > rules.MaxTypesPerFamily)
                    Add($"{symbolIds.Count} types (limit {rules.MaxTypesPerFamily})", IssueSeverity.Low);

                if (requiredTypeParameters.Count > 0)
                {
                    var incompleteTypes = new List<string>();

                    foreach (ElementId symbolId in symbolIds)
                    {
                        if (!(doc.GetElement(symbolId) is FamilySymbol symbol))
                            continue;

                        List<string> missing = requiredTypeParameters
                            .Where(p => ParameterHelper.IsBuiltInNameValid(p))
                            .Where(p =>
                            {
                                Parameter? parameter = ParameterHelper.FindOn(symbol, p);
                                return parameter == null || !ParameterHelper.HasValue(parameter);
                            })
                            .Select(p => ParameterHelper.DisplayName(p, null))
                            .ToList();

                        if (missing.Count > 0)
                            incompleteTypes.Add($"'{symbol.Name}' missing {string.Join(", ", missing)}");
                    }

                    if (incompleteTypes.Count > 0)
                    {
                        string shown = string.Join("; ", incompleteTypes.Take(3));
                        string more = incompleteTypes.Count > 3 ? $" (+{incompleteTypes.Count - 3} more types)" : string.Empty;
                        Add($"type data incomplete: {shown}{more}", IssueSeverity.Medium);
                    }
                }

                if (reasons.Count == 0)
                    continue;

                string categoryName = familyCategory?.Name ?? "Family";
                List<long> ids = instances.Count > 0 ? instances : new List<long> { family.Id.Value };

                result.AddIssue(
                    severity,
                    $"{categoryName}: {family.Name}",
                    categoryName,
                    $"{instances.Count:N0} placed – {string.Join("; ", reasons)}.",
                    ids);
            }

            result.CheckedCount = families.Count;
            result.Summary = $"{result.IssueCount:N0} of {families.Count:N0} loaded families do not meet the standard.";
        }
    }

    // S2 ------------------------------------------------------------------
    public sealed class CoordinatesCheck : IModelCheck
    {
        public string Id => "S2";
        public string Name => "Model elements outside required coordinates";
        public string Group => CheckGroups.Standards;
        public string Description =>
            "Elements, imports and links that extend beyond the allowed distance " +
            "from the internal origin, and a misplaced Project Base Point.";

        public void Run(CheckContext context, CheckResult result)
        {
            CoordinateRules rules = context.Rules.Coordinates;
            Document doc = context.Doc;

            double maxHorizontal = context.MetresToFeet(rules.MaxHorizontalDistanceM);
            double maxVertical = context.MetresToFeet(rules.MaxVerticalDistanceM);
            HashSet<BuiltInCategory> ignore = context.ResolveCategories(rules.IgnoreCategories);

            var elements = new Dictionary<long, Element>();

            foreach (Element element in context.ModelElements)
                elements[element.Id.Value] = element;

            // CAD imports and Revit links are the most common cause of far-away geometry.
            foreach (Element element in new FilteredElementCollector(doc).OfClass(typeof(ImportInstance)))
                elements[element.Id.Value] = element;

            foreach (Element element in new FilteredElementCollector(doc).OfClass(typeof(RevitLinkInstance)))
                elements[element.Id.Value] = element;

            int checkedCount = 0;

            foreach (Element element in elements.Values)
            {
                if (element.Category != null && ignore.Contains(element.Category.BuiltInCategory))
                    continue;

                BoundingBoxXYZ? box = element.get_BoundingBox(null);

                if (box == null)
                    continue;

                checkedCount++;

                double farX = Math.Max(Math.Abs(box.Min.X), Math.Abs(box.Max.X));
                double farY = Math.Max(Math.Abs(box.Min.Y), Math.Abs(box.Max.Y));
                double horizontal = Math.Sqrt(farX * farX + farY * farY);
                double vertical = Math.Max(Math.Abs(box.Min.Z), Math.Abs(box.Max.Z));

                if (horizontal <= maxHorizontal && vertical <= maxVertical)
                    continue;

                string what = element is ImportInstance ? "CAD import" :
                              element is RevitLinkInstance ? "Revit link" : "Element";

                string message = horizontal > maxHorizontal
                    ? $"{what} extends {context.FeetToMetres(horizontal):N0} m from the internal origin (limit {rules.MaxHorizontalDistanceM:N0} m)."
                    : $"{what} extends {context.FeetToMetres(vertical):N0} m vertically from the internal origin (limit {rules.MaxVerticalDistanceM:N0} m).";

                result.AddIssue(
                    element is ImportInstance || element is RevitLinkInstance ? IssueSeverity.Critical : IssueSeverity.High,
                    element is ImportInstance ? $"Import: {element.Name}" : context.Describe(element),
                    element.Category?.Name ?? string.Empty,
                    message,
                    new[] { element.Id.Value });
            }

            if (rules.CheckProjectBasePoint)
            {
                BasePoint? projectBasePoint = BasePoint.GetProjectBasePoint(doc);

                if (projectBasePoint != null)
                {
                    XYZ position = projectBasePoint.Position;
                    double distance = Math.Sqrt(position.X * position.X + position.Y * position.Y);

                    if (distance > maxHorizontal)
                    {
                        result.AddIssue(
                            IssueSeverity.High,
                            "Project Base Point",
                            "Project Base Point",
                            $"Project Base Point is {context.FeetToMetres(distance):N0} m from the internal origin (limit {rules.MaxHorizontalDistanceM:N0} m).",
                            new[] { projectBasePoint.Id.Value });
                    }
                }
            }

            result.CheckedCount = checkedCount;
            result.Summary = $"{result.IssueCount:N0} item(s) outside {rules.MaxHorizontalDistanceM:N0} m of the internal origin.";
        }
    }

    // S3 ------------------------------------------------------------------
    public sealed class NamingConventionsCheck : IModelCheck
    {
        public string Id => "S3";
        public string Name => "Naming convention violations";
        public string Group => CheckGroups.Standards;
        public string Description =>
            "Checks names of views, sheets, levels, grids, families, types, materials, " +
            "worksets and rooms against the naming rules.";

        private sealed class NamedItem
        {
            public NamedItem(string kind, string name, long? id)
            {
                Kind = kind;
                Name = name ?? string.Empty;
                Id = id;
            }

            public string Kind { get; }
            public string Name { get; }
            public long? Id { get; }
        }

        public void Run(CheckContext context, CheckResult result)
        {
            List<NamingRule> rules = context.Rules.Naming
                .Where(r => !string.IsNullOrWhiteSpace(r.Target))
                .Where(r => !string.IsNullOrWhiteSpace(r.RequiredPattern) || r.DisallowedPatterns.Count > 0)
                .ToList();

            if (rules.Count == 0)
            {
                result.MarkNotConfigured("No naming rules are defined in the rules file.");
                return;
            }

            int checkedCount = 0;

            foreach (NamingRule rule in rules)
            {
                List<NamedItem>? items = GetItems(context, rule.Target.Trim());

                if (items == null)
                {
                    context.ReportConfigError($"Unknown naming target '{rule.Target}'.");
                    continue;
                }

                string expectation = rule.Description ?? rule.RequiredPattern ?? "naming rule";

                foreach (NamedItem item in items)
                {
                    checkedCount++;
                    string? problem = null;

                    if (!string.IsNullOrWhiteSpace(rule.RequiredPattern) &&
                        !context.Patterns.IsMatch(rule.RequiredPattern, item.Name))
                    {
                        problem = $"'{item.Name}' does not follow the standard ({expectation}).";
                    }
                    else if (context.Patterns.MatchesAny(rule.DisallowedPatterns, item.Name))
                    {
                        problem = $"'{item.Name}' breaks the naming rule ({expectation}).";
                    }

                    if (problem == null)
                        continue;

                    result.AddIssue(
                        IssueSeverity.Low,
                        $"{item.Kind}: {item.Name}",
                        item.Kind,
                        problem,
                        item.Id.HasValue ? new[] { item.Id.Value } : null);
                }
            }

            result.CheckedCount = checkedCount;
            result.Summary = $"{result.IssueCount:N0} naming violation(s) in {checkedCount:N0} names checked.";
        }

        private static List<NamedItem>? GetItems(CheckContext context, string target)
        {
            Document doc = context.Doc;

            List<NamedItem> Collect<T>(string kind, Func<T, bool> filter, Func<T, string> getName) where T : Element =>
                new FilteredElementCollector(doc)
                    .OfClass(typeof(T))
                    .Cast<T>()
                    .Where(filter)
                    .Select(e => new NamedItem(kind, getName(e), e.Id.Value))
                    .ToList();

            switch (target.ToLowerInvariant())
            {
                case "views":
                    return Collect<View>("View", v => !v.IsTemplate && IsNamedView(v), v => v.Name);

                case "viewtemplates":
                    return Collect<View>("View Template", v => v.IsTemplate, v => v.Name);

                case "sheetnumbers":
                    return Collect<ViewSheet>("Sheet Number", s => true, s => s.SheetNumber);

                case "sheetnames":
                    return Collect<ViewSheet>("Sheet Name", s => true, s => s.Name);

                case "levels":
                    return Collect<Level>("Level", l => true, l => l.Name);

                case "grids":
                    return Collect<Grid>("Grid", g => true, g => g.Name);

                case "families":
                    return Collect<Family>("Family", f => true, f => f.Name);

                case "familytypes":
                    return Collect<FamilySymbol>("Family Type", s => true, s => s.Name)
                        .Concat(Collect<HostObjAttributes>("System Type", t => true, t => t.Name))
                        .ToList();

                case "materials":
                    return Collect<Material>("Material", m => true, m => m.Name);

                case "rooms":
                    return context.Rooms
                        .Select(r => new NamedItem("Room", CheckContext.RoomName(r), r.Id.Value))
                        .ToList();

                case "worksets":
                    if (!doc.IsWorkshared)
                        return new List<NamedItem>();

                    return new FilteredWorksetCollector(doc)
                        .OfKind(WorksetKind.UserWorkset)
                        .Select(w => new NamedItem("Workset", w.Name, null))
                        .ToList();

                default:
                    return null;
            }
        }

        private static bool IsNamedView(View view)
        {
            switch (view.ViewType)
            {
                case ViewType.ProjectBrowser:
                case ViewType.SystemBrowser:
                case ViewType.Internal:
                case ViewType.Undefined:
                case ViewType.DrawingSheet:
                    return false;
                default:
                    return !(view is ViewSchedule schedule && schedule.IsTitleblockRevisionSchedule);
            }
        }
    }

    // S4 ------------------------------------------------------------------
    public sealed class CobieCheck : IModelCheck
    {
        public string Id => "S4";
        public string Name => "Missing COBie/asset information";
        public string Group => CheckGroups.Standards;
        public string Description =>
            "Asset elements must have the COBie type and component parameters filled in.";

        public void Run(CheckContext context, CheckResult result)
        {
            CobieRules rules = context.Rules.Cobie;

            if (!rules.Enabled)
            {
                result.MarkNotConfigured("COBie checking is disabled in the rules file.");
                return;
            }

            Document doc = context.Doc;
            HashSet<BuiltInCategory> categories = context.ResolveCategories(rules.Categories);

            List<Element> assets = context.ElementsOfCategories(categories)
                .Where(e => !CheckContext.IsNestedComponent(e))
                .Where(e => IsIncluded(e, doc, rules.InclusionParameter))
                .ToList();

            List<string> typeParameters = Clean(rules.RequiredTypeParameters);
            List<string> instanceParameters = Clean(rules.RequiredInstanceParameters);

            if (assets.Count == 0 || typeParameters.Count + instanceParameters.Count == 0)
            {
                result.CheckedCount = assets.Count;
                result.Summary = assets.Count == 0
                    ? "No asset elements in the COBie categories."
                    : "No COBie parameters configured.";
                return;
            }

            // Parameters that are not loaded at all are reported once, not per element.
            var notLoaded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (string name in typeParameters.Concat(instanceParameters).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (!assets.Any(e => ParameterHelper.Find(e, doc, name) != null))
                {
                    notLoaded.Add(name);
                    result.AddIssue(
                        IssueSeverity.High,
                        "Project parameters",
                        "COBie",
                        $"COBie parameter '{ParameterHelper.DisplayName(name, null)}' is not loaded for the asset categories – " +
                        "add the COBie shared parameters (or run the COBie Extension setup).");
                }
            }

            List<string> typeToCheck = typeParameters.Where(p => !notLoaded.Contains(p)).ToList();
            List<string> instanceToCheck = instanceParameters.Where(p => !notLoaded.Contains(p)).ToList();

            // Type data: one issue per type listing all its instances.
            if (typeToCheck.Count > 0)
            {
                foreach (var group in assets.GroupBy(e => e.GetTypeId().Value))
                {
                    Element first = group.First();
                    List<string> missing = MissingParameters(first, doc, typeToCheck);

                    if (missing.Count == 0)
                        continue;

                    result.AddIssue(
                        IssueSeverity.Medium,
                        context.Describe(first),
                        first.Category?.Name ?? string.Empty,
                        $"Type data missing ({group.Count():N0} instance(s)): {string.Join(", ", missing)}",
                        group.Select(e => e.Id.Value));
                }
            }

            // Component data: per element.
            if (instanceToCheck.Count > 0)
            {
                foreach (Element element in assets)
                {
                    List<string> missing = MissingParameters(element, doc, instanceToCheck);

                    if (missing.Count > 0)
                        context.AddElementIssue(result, element, IssueSeverity.Low,
                            $"Component data missing: {string.Join(", ", missing)}");
                }
            }

            result.CheckedCount = assets.Count;
            result.Summary = $"{assets.Count:N0} asset elements checked; {result.IssueCount:N0} issue(s).";
        }

        private static List<string> Clean(IEnumerable<string> names) =>
            names.Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => n.Trim()).ToList();

        private static List<string> MissingParameters(Element element, Document doc, IEnumerable<string> names) =>
            names.Where(name =>
                {
                    Parameter? parameter = ParameterHelper.Find(element, doc, name);
                    return parameter == null || !ParameterHelper.HasValue(parameter);
                })
                .Select(name => ParameterHelper.DisplayName(name, null))
                .ToList();

        private static bool IsIncluded(Element element, Document doc, string? inclusionParameter)
        {
            if (string.IsNullOrWhiteSpace(inclusionParameter))
                return true;

            Parameter? parameter = ParameterHelper.Find(element, doc, inclusionParameter);

            return parameter == null ||
                   parameter.StorageType != StorageType.Integer ||
                   !parameter.HasValue ||
                   parameter.AsInteger() != 0;
        }
    }
}
