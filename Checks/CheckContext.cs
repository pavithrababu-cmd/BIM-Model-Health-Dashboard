using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using BIMModelHealthDashboard.Config;
using BIMModelHealthDashboard.Models;
using System;
using System.Collections.Generic;
using System.Linq;

namespace BIMModelHealthDashboard.Checks
{
    // Shared state for one scan: the document, the rules and cached
    // collections so each check doesn't re-collect the same elements.
    public sealed class CheckContext
    {
        private readonly HashSet<string> _errorSet =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        private List<Element>? _modelElements;
        private List<Level>? _levels;
        private IList<FailureMessage>? _warnings;
        private List<Room>? _rooms;

        public CheckContext(Document doc, HealthRules rules)
        {
            Doc = doc ?? throw new ArgumentNullException(nameof(doc));
            Rules = rules ?? new HealthRules();
            Patterns = new PatternMatcher(ReportConfigError);
        }

        public Document Doc { get; }
        public HealthRules Rules { get; }
        public PatternMatcher Patterns { get; }

        public List<string> ConfigErrors { get; } = new List<string>();

        public void ReportConfigError(string message)
        {
            if (_errorSet.Add(message))
                ConfigErrors.Add(message);
        }

        // Model-category instances (no types, no view-specific detail items).
        public IReadOnlyList<Element> ModelElements =>
            _modelElements ??= new FilteredElementCollector(Doc)
                .WhereElementIsNotElementType()
                .Where(e =>
                    e.Category != null &&
                    e.Category.CategoryType == CategoryType.Model &&
                    e.Category.BuiltInCategory != BuiltInCategory.OST_SketchLines &&
                    !e.ViewSpecific)
                .ToList();

        // Levels sorted by elevation from the internal origin.
        public IReadOnlyList<Level> Levels =>
            _levels ??= new FilteredElementCollector(Doc)
                .OfClass(typeof(Level))
                .Cast<Level>()
                .OrderBy(l => l.ProjectElevation)
                .ToList();

        public IList<FailureMessage> Warnings =>
            _warnings ??= Doc.GetWarnings();

        public IReadOnlyList<Room> Rooms =>
            _rooms ??= new FilteredElementCollector(Doc)
                .OfClass(typeof(SpatialElement))
                .WhereElementIsNotElementType()
                .OfType<Room>()
                .ToList();

        public HashSet<BuiltInCategory> ResolveCategories(IEnumerable<string>? names)
        {
            var result = new HashSet<BuiltInCategory>();

            if (names == null)
                return result;

            foreach (string raw in names)
            {
                if (string.IsNullOrWhiteSpace(raw))
                    continue;

                string name = raw.Trim();

                if (!name.StartsWith("OST_", StringComparison.OrdinalIgnoreCase))
                    name = "OST_" + name.Replace(" ", string.Empty);

                if (Enum.TryParse(name, true, out BuiltInCategory category) &&
                    Enum.IsDefined(typeof(BuiltInCategory), category))
                {
                    result.Add(category);
                }
                else
                {
                    ReportConfigError($"Unknown category '{raw}'.");
                }
            }

            return result;
        }

        public IEnumerable<Element> ElementsOfCategories(ISet<BuiltInCategory> categories) =>
            ModelElements.Where(e => categories.Contains(e.Category!.BuiltInCategory));

        public static bool IsNestedComponent(Element element) =>
            element is FamilyInstance instance && instance.SuperComponent != null;

        public Element? GetTypeElement(Element element)
        {
            ElementId typeId = element.GetTypeId();

            return typeId == ElementId.InvalidElementId
                ? null
                : Doc.GetElement(typeId);
        }

        // "Doors: Single-Flush : 900 x 2100mm"
        public string Describe(Element element)
        {
            string category = element.Category?.Name ?? "No category";

            if (element is Room room)
                return $"Room {room.Number} - {RoomName(room)}";

            if (element is FamilyInstance instance && instance.Symbol != null)
                return $"{category}: {instance.Symbol.FamilyName} : {instance.Symbol.Name}";

            if (GetTypeElement(element) is ElementType type)
                return $"{category}: {type.FamilyName} : {type.Name}";

            return $"{category}: {element.Name}";
        }

        public static string RoomName(Room room) =>
            room.get_Parameter(BuiltInParameter.ROOM_NAME)?.AsString() ?? room.Name;

        public void AddElementIssue(
            CheckResult result,
            Element element,
            IssueSeverity severity,
            string message)
        {
            result.AddIssue(
                severity,
                Describe(element),
                element.Category?.Name ?? string.Empty,
                message,
                new[] { element.Id.Value });
        }

        public double MmToFeet(double millimetres) =>
            UnitUtils.ConvertToInternalUnits(millimetres, UnitTypeId.Millimeters);

        public double MetresToFeet(double metres) =>
            UnitUtils.ConvertToInternalUnits(metres, UnitTypeId.Meters);

        public double FeetToMetres(double feet) =>
            UnitUtils.ConvertFromInternalUnits(feet, UnitTypeId.Meters);
    }
}
