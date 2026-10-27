using Autodesk.Revit.DB;
using BIMModelHealthDashboard.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace BIMModelHealthDashboard.Services
{
    // Model metrics for the top cards and the Performance / Content /
    // Coordination health panels. Each panel row has a traffic-light status
    // (good / warning / bad) and a tip explaining why it matters.
    public class ModelDashboardService
    {
        // Families in these categories are used indirectly (profiles, balusters…)
        // and are never counted as purgeable.
        private static readonly HashSet<BuiltInCategory> IndirectFamilyCategories = new HashSet<BuiltInCategory>
        {
            BuiltInCategory.OST_ProfileFamilies,
            BuiltInCategory.OST_StairsRailingBaluster,
            BuiltInCategory.OST_RailingSupport,
            BuiltInCategory.OST_RailingTermination,
            BuiltInCategory.OST_DetailComponents,
            BuiltInCategory.OST_CurtainWallPanels
        };

        private static readonly HashSet<ViewType> GraphicalViewTypes = new HashSet<ViewType>
        {
            ViewType.FloorPlan, ViewType.CeilingPlan, ViewType.Elevation, ViewType.ThreeD,
            ViewType.Section, ViewType.Detail, ViewType.DraftingView, ViewType.AreaPlan,
            ViewType.EngineeringPlan
        };

        public DashboardSummary GetSummary(Document doc)
        {
            if (doc == null)
                throw new ArgumentNullException(nameof(doc));

            string modelPath = doc.PathName ?? string.Empty;

            string modelName = string.IsNullOrWhiteSpace(modelPath)
                ? doc.Title
                : Path.GetFileName(modelPath);

            // Model-category instances (no <Sketch> lines, no view-specific detail items).
            List<Element> modelElements =
                new FilteredElementCollector(doc)
                    .WhereElementIsNotElementType()
                    .Where(e => e.Category != null &&
                                e.Category.CategoryType == CategoryType.Model &&
                                e.Category.BuiltInCategory != BuiltInCategory.OST_SketchLines &&
                                !e.ViewSpecific)
                    .ToList();

            List<Family> families = new FilteredElementCollector(doc)
                .OfClass(typeof(Family))
                .Cast<Family>()
                .ToList();

            int warningCount = doc.GetWarnings().Count;

            var summary = new DashboardSummary
            {
                ModelKey = ValidationService.ModelKey(doc),
                ModelFileName = modelName,
                ModelFilePath = string.IsNullOrWhiteSpace(modelPath) ? "Model has not been saved" : modelPath,
                FileSize = GetModelFileSize(modelPath),
                TotalFamilies = families.Count,
                TotalElements = modelElements.Count,
                RevitWarnings = warningCount
            };

            AddProjectReadiness(doc, summary);
            AddModelOrganization(doc, summary);
            AddDependencies(doc, summary);

            return summary;
        }

        // ================================================================
        // PROJECT READINESS – is the drawing set ready to issue?
        // ================================================================

        private static void AddProjectReadiness(Document doc, DashboardSummary summary)
        {
            List<ViewSheet> sheets = new FilteredElementCollector(doc)
                .OfClass(typeof(ViewSheet))
                .Cast<ViewSheet>()
                .Where(s => !s.IsPlaceholder)
                .ToList();

            // 1. Sheets without views (no views and no schedules placed)
            var sheetsWithSchedules = new HashSet<long>(new FilteredElementCollector(doc)
                .OfClass(typeof(ScheduleSheetInstance))
                .Cast<ScheduleSheetInstance>()
                .Where(s => !s.IsTitleblockRevisionSchedule)
                .Select(s => s.OwnerViewId.Value));

            int sheetsWithoutViews = sheets.Count(s =>
                s.GetAllPlacedViews().Count == 0 && !sheetsWithSchedules.Contains(s.Id.Value));

            summary.ProjectReadiness.Add(new HealthIndicator(
                "Sheets without views",
                sheetsWithoutViews.ToString("N0"),
                sheetsWithoutViews == 0 ? IndicatorStatus.Good : IndicatorStatus.Warning,
                $"Sheets with no views or schedules on them ({sheets.Count:N0} sheets in total). " +
                "Place the intended views or delete the sheet. Target: 0."));

            // 2. Views missing templates
            List<View> views = new FilteredElementCollector(doc)
                .OfClass(typeof(View))
                .Cast<View>()
                .Where(v => !v.IsTemplate && GraphicalViewTypes.Contains(v.ViewType))
                .ToList();

            int missingTemplates = views.Count(v => v.ViewTemplateId == ElementId.InvalidElementId);

            summary.ProjectReadiness.Add(new HealthIndicator(
                "Views missing templates",
                missingTemplates.ToString("N0"),
                missingTemplates == 0 ? IndicatorStatus.Good :
                missingTemplates <= views.Count * 0.25 ? IndicatorStatus.Warning :
                IndicatorStatus.Bad,
                $"{missingTemplates:N0} of {views.Count:N0} views have no view template, so their graphics are " +
                "set by hand and drift from the company standard. Target: 0."));

            // 3. Sheets missing a title block
            var sheetsWithTitleBlock = new HashSet<long>(new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_TitleBlocks)
                .WhereElementIsNotElementType()
                .Select(e => e.OwnerViewId.Value));

            int missingTitleBlocks = sheets.Count(s => !sheetsWithTitleBlock.Contains(s.Id.Value));

            summary.ProjectReadiness.Add(new HealthIndicator(
                "Missing title blocks",
                missingTitleBlocks.ToString("N0"),
                missingTitleBlocks == 0 ? IndicatorStatus.Good : IndicatorStatus.Bad,
                "Sheets without a title block can't be issued. Target: 0."));
        }

        // ================================================================
        // MODEL ORGANIZATION – how the model is structured
        // ================================================================

        private static void AddModelOrganization(Document doc, DashboardSummary summary)
        {
            // 1. Levels
            int levels = new FilteredElementCollector(doc)
                .OfClass(typeof(Level))
                .GetElementCount();

            summary.ModelOrganization.Add(new HealthIndicator(
                "Levels",
                levels.ToString("N0"),
                levels > 0 ? IndicatorStatus.Good : IndicatorStatus.Bad,
                "Number of levels in the model. Unused or duplicate levels are reported by the Incorrect levels check."));

            // 2. Model groups
            int groups = new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_IOSModelGroups)
                .WhereElementIsNotElementType()
                .GetElementCount();

            int unusedGroupTypes = new FilteredElementCollector(doc)
                .OfClass(typeof(GroupType))
                .Cast<GroupType>()
                .Count(t => t.Category != null &&
                            t.Category.BuiltInCategory == BuiltInCategory.OST_IOSModelGroups &&
                            t.Groups.IsEmpty);

            summary.ModelOrganization.Add(new HealthIndicator(
                "Model groups",
                groups.ToString("N0"),
                unusedGroupTypes == 0 ? IndicatorStatus.Good : IndicatorStatus.Warning,
                $"Placed model groups ({unusedGroupTypes:N0} unused group types). Groups help with repetition " +
                "but slow down editing when overused; purge unused group types."));

            // 3. In-place families
            int inPlace = new FilteredElementCollector(doc)
                .OfClass(typeof(Family))
                .Cast<Family>()
                .Count(f => f.IsInPlace);

            summary.ModelOrganization.Add(new HealthIndicator(
                "In-place families",
                inPlace.ToString("N0"),
                Rate(inPlace, 0, 5),
                "In-place families are heavy, can't be reused and are hard to schedule. Target: 0."));
        }

        // ================================================================
        // DEPENDENCIES – external files the model relies on
        // ================================================================

        private static void AddDependencies(Document doc, DashboardSummary summary)
        {
            // 1. Revit links
            List<RevitLinkType> linkTypes = new FilteredElementCollector(doc)
                .OfClass(typeof(RevitLinkType))
                .Cast<RevitLinkType>()
                .ToList();

            int unloaded = linkTypes.Count(t => !RevitLinkType.IsLoaded(doc, t.Id));

            summary.Dependencies.Add(new HealthIndicator(
                "Revit links",
                linkTypes.Count.ToString("N0"),
                unloaded == 0 ? IndicatorStatus.Good : IndicatorStatus.Warning,
                $"Linked Revit models ({unloaded:N0} unloaded). Unloaded links mean coordination is done " +
                "against missing information."));

            // 2. CAD references (linked + imported)
            List<ImportInstance> cad = new FilteredElementCollector(doc)
                .OfClass(typeof(ImportInstance))
                .Cast<ImportInstance>()
                .ToList();

            int imported = cad.Count(i => !i.IsLinked);

            summary.Dependencies.Add(new HealthIndicator(
                "CAD references",
                cad.Count.ToString("N0"),
                imported == 0 ? IndicatorStatus.Good : IndicatorStatus.Warning,
                $"DWG/DGN/DXF files in the model: {cad.Count - imported:N0} linked, {imported:N0} imported. " +
                "Imported CAD bloats the model; link instead."));

            // 3. Missing references (links whose file can't be found)
            int missing = 0;
            int checkedReferences = 0;

            foreach (ElementId id in ExternalFileUtils.GetAllExternalFileReferences(doc))
            {
                try
                {
                    ExternalFileReference reference = ExternalFileUtils.GetExternalFileReference(doc, id);
                    checkedReferences++;

                    LinkedFileStatus status = reference.GetLinkedFileStatus();

                    if (status == LinkedFileStatus.NotFound || status == LinkedFileStatus.Invalid)
                        missing++;
                }
                catch (Autodesk.Revit.Exceptions.ApplicationException)
                {
                }
            }

            summary.Dependencies.Add(new HealthIndicator(
                "Missing references",
                missing.ToString("N0"),
                missing == 0 ? IndicatorStatus.Good : IndicatorStatus.Bad,
                $"Linked files (Revit, CAD, images, keynotes…) whose source file can't be found " +
                $"({checkedReferences:N0} references checked). Repath them in Manage Links. Target: 0."));
        }

        // Lower is better: good at or below 'good', warning at or below 'warning'.
        private static IndicatorStatus Rate(double value, double good, double warning) =>
            value <= good ? IndicatorStatus.Good :
            value <= warning ? IndicatorStatus.Warning :
            IndicatorStatus.Bad;

        // Higher is better.
        private static IndicatorStatus RateHigherIsBetter(double value, double good, double warning) =>
            value >= good ? IndicatorStatus.Good :
            value >= warning ? IndicatorStatus.Warning :
            IndicatorStatus.Bad;

        private static readonly BuiltInParameter[] LevelParameters =
        {
            BuiltInParameter.WALL_BASE_CONSTRAINT,
            BuiltInParameter.LEVEL_PARAM,
            BuiltInParameter.FAMILY_LEVEL_PARAM,
            BuiltInParameter.INSTANCE_REFERENCE_LEVEL_PARAM
        };

        private static Level? ResolveElementLevel(
            Element element,
            Dictionary<long, Level> levelsById,
            List<Level> levelsByElevation)
        {
            // 1. The element's own level (covers most categories, including walls).
            if (levelsById.TryGetValue(element.LevelId.Value, out Level? level))
                return level;

            // 2. Level-related parameters.
            foreach (BuiltInParameter parameterId in LevelParameters)
            {
                Parameter? parameter = element.get_Parameter(parameterId);

                if (parameter == null || parameter.StorageType != StorageType.ElementId)
                    continue;

                if (levelsById.TryGetValue(parameter.AsElementId().Value, out level))
                    return level;
            }

            // 3. Estimate from location: closest level at or below the element.
            double? elevation = GetElementElevation(element);

            if (!elevation.HasValue)
                return null;

            return levelsByElevation.LastOrDefault(l => l.ProjectElevation <= elevation.Value + 1e-6);
        }

        private static double? GetElementElevation(Element element)
        {
            if (element.Location is LocationPoint pointLocation)
                return pointLocation.Point.Z;

            if (element.Location is LocationCurve curveLocation)
            {
                Curve curve = curveLocation.Curve;

                if (curve == null)
                    return null;

                try
                {
                    return curve.IsBound
                        ? curve.Evaluate(0.5, true).Z
                        : curve.ComputeDerivatives(0.0, false).Origin.Z;
                }
                catch (Autodesk.Revit.Exceptions.ApplicationException)
                {
                    return null;
                }
            }

            return null;
        }

        private static string GetModelFileSize(string modelPath)
        {
            if (string.IsNullOrWhiteSpace(modelPath))
                return "N/A";

            try
            {
                if (!File.Exists(modelPath))
                    return "N/A";

                long bytes = new FileInfo(modelPath).Length;

                if (bytes >= 1024L * 1024L * 1024L)
                    return $"{bytes / (1024.0 * 1024.0 * 1024.0):0.00} GB";

                if (bytes >= 1024L * 1024L)
                    return $"{bytes / (1024.0 * 1024.0):0.00} MB";

                return $"{bytes / 1024.0:0.00} KB";
            }
            catch (IOException)
            {
                return "N/A";
            }
            catch (UnauthorizedAccessException)
            {
                return "N/A";
            }
        }
    }
}
