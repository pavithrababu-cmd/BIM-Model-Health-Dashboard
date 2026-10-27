using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.UI;
using BIMModelHealthDashboard.Checks;
using BIMModelHealthDashboard.Config;
using BIMModelHealthDashboard.Models;
using System;
using System.Collections.Generic;
using System.Linq;

namespace BIMModelHealthDashboard.Services
{
    // Applies the automatic fixes chosen in the "Fix All Issues" dialog.
    //
    // Safety:
    //  - All fixes run inside one TransactionGroup, so a single Ctrl+Z in Revit
    //    undoes everything ("BIM Health: Fix All Issues").
    //  - Elements in groups, elements owned by another user and elements not
    //    up to date with central are skipped.
    //  - Deletions are checked so that nothing else is deleted with them.
    public static class AutoFixService
    {
        public static List<FixResult> Run(
            UIDocument uiDocument,
            HealthRules rules,
            IReadOnlyCollection<string> fixIds)
        {
            Document doc = uiDocument.Document;
            var results = new List<FixResult>();

            if (doc.IsReadOnly)
                throw new InvalidOperationException("The model is read-only, so it cannot be fixed.");

            using (var group = new TransactionGroup(doc, "BIM Health: Fix All Issues"))
            {
                group.Start();

                foreach (FixDefinition fix in FixCatalog.All.Where(f => fixIds.Contains(f.Id)))
                {
                    var result = new FixResult { FixId = fix.Id, Name = fix.Name };

                    // Wall connections run one small transaction per wall end, so a
                    // wall Revit can't rebuild is rolled back on its own.
                    if (fix.Id == FixCatalog.ConnectWalls || fix.Id == FixCatalog.ResolveWarnings)
                    {
                        try
                        {
                            if (fix.Id == FixCatalog.ConnectWalls)
                                ConnectWalls(doc, rules, result);
                            else
                                ResolveWarnings(doc, result);
                        }
                        catch (Exception ex)
                        {
                            result.Error = ex.Message;
                        }

                        results.Add(result);
                        continue;
                    }

                    using (var transaction = new Transaction(doc, "BIM Health: " + fix.Name))
                    {
                        FailureHandlingOptions options = transaction.GetFailureHandlingOptions();
                        options.SetFailuresPreprocessor(new SilentFailurePreprocessor());
                        transaction.SetFailureHandlingOptions(options);

                        try
                        {
                            transaction.Start();
                            Apply(fix.Id, uiDocument, rules, result);

                            if (transaction.Commit() != TransactionStatus.Committed)
                            {
                                result.Error = "Revit rolled this fix back.";
                                result.Fixed = 0;
                            }
                        }
                        catch (Exception ex)
                        {
                            if (transaction.HasStarted() && !transaction.HasEnded())
                                transaction.RollBack();

                            result.Error = ex.Message;
                            result.Fixed = 0;
                        }
                    }

                    results.Add(result);
                }

                group.Assimilate();
            }

            return results;
        }

        private static void Apply(string fixId, UIDocument uiDocument, HealthRules rules, FixResult result)
        {
            Document doc = uiDocument.Document;

            switch (fixId)
            {
                case FixCatalog.DeleteDuplicates: DeleteDuplicates(doc, rules, result); break;
                case FixCatalog.PurgeUnusedFamilies: PurgeUnusedFamilies(doc, rules, result); break;
                case FixCatalog.ConnectWalls: ConnectWalls(doc, rules, result); break;
                case FixCatalog.FixWorksets: FixWorksets(doc, rules, result); break;
                case FixCatalog.DeleteUnusedViews: DeleteUnusedViews(uiDocument, rules, result); break;
                case FixCatalog.DeleteUnplacedRooms: DeleteUnplacedRooms(doc, result); break;
                case FixCatalog.NumberEmptyMarks: NumberEmptyMarks(doc, rules, result); break;
            }
        }

        // ================================================================
        // 1. DUPLICATE ELEMENTS
        // ================================================================

        private static void DeleteDuplicates(Document doc, HealthRules rules, FixResult result)
        {
            // Find duplicates exactly as the "Duplicate elements" check does.
            var check = new CheckResult();
            new DuplicateElementsCheck().Run(new CheckContext(doc, rules), check);

            int manual = 0;

            foreach (ValidationIssue issue in check.Issues)
            {
                List<Element> copies = issue.ElementIds
                    .Select(id => doc.GetElement(new ElementId(id)))
                    .Where(e => e != null)
                    .OrderBy(e => e.Id.Value)
                    .ToList();

                if (copies.Count < 2)
                    continue;

                // Walls, floors etc. need a person to decide which copy to keep.
                if (!copies.All(e => e is FamilyInstance))
                {
                    manual += copies.Count - 1;
                    continue;
                }

                foreach (Element extra in copies.Skip(1))
                {
                    if (!CanEdit(doc, extra, forDelete: true) || HostsOtherElements(doc, extra))
                    {
                        result.Skipped++;
                        continue;
                    }

                    if (TryDelete(doc, extra.Id))
                        result.Fixed++;
                    else
                        result.Skipped++;
                }
            }

            if (manual > 0)
                result.Notes.Add($"{manual} duplicate wall/floor/other system elements need manual review.");
        }

        private static bool HostsOtherElements(Document doc, Element element)
        {
            ICollection<ElementId> dependents;

            try
            {
                dependents = element.GetDependentElements(null);
            }
            catch (Autodesk.Revit.Exceptions.ApplicationException)
            {
                return true;
            }

            foreach (ElementId id in dependents)
            {
                if (id == element.Id)
                    continue;

                Element? dependent = doc.GetElement(id);

                if (dependent == null || dependent is ElementType || dependent.ViewSpecific)
                    continue;

                // Its own nested components are fine.
                if (dependent is FamilyInstance nested && nested.SuperComponent?.Id == element.Id)
                    continue;

                if (dependent.Category != null && dependent.Category.CategoryType == CategoryType.Model)
                    return true;
            }

            return false;
        }

        // ================================================================
        // 2. UNUSED FAMILIES
        // ================================================================

        private static void PurgeUnusedFamilies(Document doc, HealthRules rules, FixResult result)
        {
            var context = new CheckContext(doc, rules);
            HashSet<BuiltInCategory> ignore = context.ResolveCategories(rules.FamilyStandards.UnusedIgnoreCategories);

            var usedFamilies = new HashSet<long>(new FilteredElementCollector(doc)
                .OfClass(typeof(FamilyInstance))
                .Cast<FamilyInstance>()
                .Where(fi => fi.Symbol != null)
                .Select(fi => fi.Symbol.Family.Id.Value));

            List<Family> unused = new FilteredElementCollector(doc)
                .OfClass(typeof(Family))
                .Cast<Family>()
                .Where(f => !f.IsInPlace &&
                            f.FamilyCategory != null &&
                            f.FamilyCategory.CategoryType == CategoryType.Model &&
                            !ignore.Contains(f.FamilyCategory.BuiltInCategory) &&
                            !usedFamilies.Contains(f.Id.Value))
                .ToList();

            foreach (Family family in unused)
            {
                if (!CanEdit(doc, family, forDelete: true))
                {
                    result.Skipped++;
                    continue;
                }

                // Only accept the deletion if no instance disappears with it.
                int instancesBefore = CountInstances(doc);

                using (var sub = new SubTransaction(doc))
                {
                    sub.Start();

                    try
                    {
                        doc.Delete(family.Id);
                    }
                    catch (Autodesk.Revit.Exceptions.ApplicationException)
                    {
                        sub.RollBack();
                        result.Skipped++;
                        continue;
                    }

                    if (CountInstances(doc) != instancesBefore)
                    {
                        sub.RollBack();
                        result.Skipped++;
                    }
                    else
                    {
                        sub.Commit();
                        result.Fixed++;
                    }
                }
            }
        }

        private static int CountInstances(Document doc) =>
            new FilteredElementCollector(doc).WhereElementIsNotElementType().GetElementCount();

        // ================================================================
        // 3. UNCONNECTED WALLS
        // ================================================================
        // For every wall end that the "Unconnected walls" check reports:
        //   a) if joining is disallowed and a wall is right next to the end,
        //      allow the join again;
        //   b) otherwise extend/trim a straight wall so its end meets the
        //      nearest straight wall (gap up to AutoFixSnapDistanceMm).
        // Revit then joins the walls automatically.

        private static void ConnectWalls(Document doc, HealthRules rules, FixResult result)
        {
            // Same detection as the "Unconnected walls" check (Revit Wall Checker logic).
            double tolerance = Math.Max(0.001, rules.Walls.ConnectionToleranceFeet);

            double snap = UnitUtils.ConvertToInternalUnits(
                Math.Max(1.0, rules.Walls.AutoFixSnapDistanceMm), UnitTypeId.Millimeters);

            List<Wall> walls = new FilteredElementCollector(doc)
                .OfClass(typeof(Wall))
                .WhereElementIsNotElementType()
                .Cast<Wall>()
                .ToList();

            int joinsAllowed = 0;
            int extended = 0;
            int notJoined = 0;

            foreach (Wall wall in walls)
            {
                for (int end = 0; end <= 1; end++)
                {
                    if (!(wall.Location is LocationCurve location) || location.Curve == null)
                        break;

                    XYZ endpoint = location.Curve.GetEndPoint(end);
                    Wall? nearbyWall = UnconnectedWallsCheck.FindNearbyWall(doc, endpoint, wall.Id, tolerance);

                    if (nearbyWall == null)
                        continue;

                    bool joinAllowed;

                    try
                    {
                        joinAllowed = WallUtils.IsWallJoinAllowedAtEnd(wall, end);
                    }
                    catch (Autodesk.Revit.Exceptions.ApplicationException)
                    {
                        result.Skipped++;
                        continue;
                    }

                    if (joinAllowed)
                        continue;   // not reported by the check

                    if (!CanEdit(doc, wall, forDelete: true))
                    {
                        result.Skipped++;
                        continue;
                    }

                    // 1. Re-allow the join (what the Wall Checker's Fix All Issues does).
                    int endIndex = end;

                    if (!TryInOwnTransaction(doc, () => WallUtils.AllowWallJoinAtEnd(wall, endIndex)))
                    {
                        result.Skipped++;
                        continue;
                    }

                    joinsAllowed++;
                    result.Fixed++;

                    // 2. If Revit still doesn't join them, move the end onto the nearby wall.
                    if (IsJoinedTo(wall, endIndex, nearbyWall.Id))
                        continue;

                    if (wall.Location is LocationCurve updated &&
                        updated.Curve is Line line &&
                        TryFindConnectionPoint(doc, wall, line, endIndex, snap, out XYZ target) &&
                        target.DistanceTo(line.GetEndPoint(1 - endIndex)) > 0.1)
                    {
                        XYZ otherEnd = line.GetEndPoint(1 - endIndex);
                        Line newLine = endIndex == 0 ? Line.CreateBound(target, otherEnd) : Line.CreateBound(otherEnd, target);

                        if (TryInOwnTransaction(doc, () => updated.Curve = newLine))
                        {
                            extended++;
                            continue;
                        }
                    }

                    notJoined++;
                }
            }

            result.Notes.Add($"{joinsAllowed:N0} wall join(s) re-allowed; {extended:N0} of them also extended/trimmed to meet the nearby wall.");

            if (notJoined > 0)
                result.Notes.Add($"{notJoined:N0} end(s) are allowed to join now but Revit did not join them geometrically – review them.");
        }

        private static bool IsJoinedTo(Wall wall, int end, ElementId otherId)
        {
            try
            {
                if (!(wall.Location is LocationCurve location))
                    return false;

                foreach (Element joined in location.get_ElementsAtJoin(end))
                {
                    if (joined != null && joined.Id == otherId)
                        return true;
                }
            }
            catch (Autodesk.Revit.Exceptions.ApplicationException)
            {
            }

            return false;
        }

        // Runs one change in its own transaction; any Revit error rolls back
        // only this change.
        private static bool TryInOwnTransaction(Document doc, Action change)
        {
            using (var transaction = new Transaction(doc, "BIM Health: automatic fix"))
            {
                FailureHandlingOptions options = transaction.GetFailureHandlingOptions();
                options.SetFailuresPreprocessor(new RollbackOnErrorPreprocessor());
                transaction.SetFailureHandlingOptions(options);

                try
                {
                    transaction.Start();
                    change();
                    return transaction.Commit() == TransactionStatus.Committed;
                }
                catch (Autodesk.Revit.Exceptions.ApplicationException)
                {
                    if (transaction.HasStarted() && !transaction.HasEnded())
                        transaction.RollBack();

                    return false;
                }
            }
        }

        // Finds where this wall end should meet another straight wall:
        //  - the intersection of the two wall lines (L and T junctions), or
        //  - the nearest end of an in-line (collinear) wall.
        // The end may move at most 'snap'.
        private static bool TryFindConnectionPoint(
            Document doc, Wall wall, Line line, int end, double snap, out XYZ target)
        {
            target = XYZ.Zero;

            XYZ endPoint = line.GetEndPoint(end);
            XYZ otherEnd = line.GetEndPoint(1 - end);
            XYZ direction = (endPoint - otherEnd).Normalize();   // points out of this end

            double z = endPoint.Z;
            double footAbove = UnitUtils.ConvertToInternalUnits(300, UnitTypeId.Millimeters);

            var offset = new XYZ(snap, snap, snap);
            var outline = new Outline(endPoint - offset, endPoint + offset);

            IEnumerable<Wall> candidates = new FilteredElementCollector(doc)
                .OfClass(typeof(Wall))
                .WherePasses(new BoundingBoxIntersectsFilter(outline))
                .Excluding(new List<ElementId> { wall.Id })
                .Cast<Wall>();

            double bestMove = double.MaxValue;
            bool found = false;

            foreach (Wall candidate in candidates)
            {
                if (!(candidate.Location is LocationCurve candidateLocation) || !(candidateLocation.Curve is Line candidateLine))
                    continue;

                // Must occupy the height just above this wall's base (same storey).
                BoundingBoxXYZ? box = candidate.get_BoundingBox(null);

                if (box == null || box.Min.Z > z + footAbove || box.Max.Z < z + footAbove)
                    continue;

                XYZ c0 = new XYZ(candidateLine.GetEndPoint(0).X, candidateLine.GetEndPoint(0).Y, z);
                XYZ c1 = new XYZ(candidateLine.GetEndPoint(1).X, candidateLine.GetEndPoint(1).Y, z);
                XYZ e = c1 - c0;
                double length = e.GetLength();

                if (length < 0.01)
                    continue;

                double cross = direction.X * e.Y - direction.Y * e.X;
                XYZ q = c0 - endPoint;

                if (Math.Abs(cross) > 1e-6)
                {
                    // Intersection of the two (infinite) wall lines in plan.
                    double t = (q.X * e.Y - q.Y * e.X) / cross;   // move along this wall
                    double u = (q.X * direction.Y - q.Y * direction.X) / cross;   // position on candidate (0..1)

                    double margin = snap / length;

                    if (Math.Abs(t) <= snap && u >= -margin && u <= 1 + margin && Math.Abs(t) < bestMove)
                    {
                        bestMove = Math.Abs(t);
                        target = endPoint + direction * t;
                        found = true;
                    }
                }
                else
                {
                    // Parallel: only connect if in line, to the nearest candidate end in front.
                    XYZ eUnit = e / length;
                    double offLine = Math.Abs(q.X * eUnit.Y - q.Y * eUnit.X);

                    if (offLine > 0.01)
                        continue;

                    foreach (XYZ candidateEnd in new[] { c0, c1 })
                    {
                        double ahead = (candidateEnd - endPoint).DotProduct(direction);
                        double distance = candidateEnd.DistanceTo(endPoint);

                        if (ahead > 0 && distance <= snap && distance < bestMove)
                        {
                            bestMove = distance;
                            target = candidateEnd;
                            found = true;
                        }
                    }
                }
            }

            return found;
        }

        private static bool HasWallNearEnd(Document doc, Wall wall, XYZ point, double snap)
        {
            var offset = new XYZ(snap, snap, snap);
            var outline = new Outline(point - offset, point + offset);

            IEnumerable<Wall> nearby = new FilteredElementCollector(doc)
                .OfClass(typeof(Wall))
                .WherePasses(new BoundingBoxIntersectsFilter(outline))
                .Excluding(new List<ElementId> { wall.Id })
                .Cast<Wall>();

            foreach (Wall other in nearby)
            {
                if (!(other.Location is LocationCurve otherLocation) || otherLocation.Curve == null)
                    continue;

                Curve curve = otherLocation.Curve;
                var flattened = new XYZ(point.X, point.Y, curve.GetEndPoint(0).Z);
                IntersectionResult? projection = curve.Project(flattened);

                if (projection != null && projection.Distance <= other.Width / 2.0 + snap)
                    return true;
            }

            return false;
        }

        // ================================================================
        // 4. WORKSETS
        // ================================================================

        private static void FixWorksets(Document doc, HealthRules rules, FixResult result)
        {
            if (!doc.IsWorkshared)
            {
                result.Notes.Add("The model is not workshared.");
                return;
            }

            var context = new CheckContext(doc, rules);
            WorksetTable table = doc.GetWorksetTable();

            List<Workset> userWorksets = new FilteredWorksetCollector(doc)
                .OfKind(WorksetKind.UserWorkset)
                .ToList();

            foreach (WorksetAssignmentRule rule in rules.Worksets.Assignments)
            {
                if (string.IsNullOrWhiteSpace(rule.ExpectedWorksetPattern))
                    continue;

                HashSet<BuiltInCategory> categories = context.ResolveCategories(rule.Categories);

                if (categories.Count == 0)
                    continue;

                Workset? target = userWorksets
                    .Where(w => context.Patterns.IsMatch(rule.ExpectedWorksetPattern, w.Name))
                    .OrderBy(w => w.Name)
                    .FirstOrDefault();

                if (target == null)
                {
                    result.Notes.Add($"No workset matches '{rule.Description ?? rule.ExpectedWorksetPattern}' – create it first.");
                    continue;
                }

                IEnumerable<Element> elements = new FilteredElementCollector(doc)
                    .WherePasses(new ElementMulticategoryFilter(categories.ToList()))
                    .WhereElementIsNotElementType();

                foreach (Element element in elements)
                {
                    Workset? current = table.GetWorkset(element.WorksetId);

                    if (current == null ||
                        current.Kind != WorksetKind.UserWorkset ||
                        context.Patterns.IsMatch(rule.ExpectedWorksetPattern, current.Name))
                    {
                        continue;
                    }

                    Parameter? worksetParameter = element.get_Parameter(BuiltInParameter.ELEM_PARTITION_PARAM);

                    if (worksetParameter == null || worksetParameter.IsReadOnly || !CanEdit(doc, element, forDelete: false))
                    {
                        result.Skipped++;
                        continue;
                    }

                    try
                    {
                        worksetParameter.Set(target.Id.IntegerValue);
                        result.Fixed++;
                    }
                    catch (Autodesk.Revit.Exceptions.ApplicationException)
                    {
                        result.Skipped++;
                    }
                }
            }

            result.Notes.Add("Elements on disallowed worksets (e.g. Workset1) without a workset rule for their " +
                             "category are left for manual review.");
        }

        // ================================================================
        // 4b. REVIT WARNINGS
        // ================================================================
        // Revit can't "clear" a warning; we fix its cause, and the warning
        // disappears. Each change runs in its own small transaction.
        // Warning types are recognised by their (English) description.

        private static void ResolveWarnings(Document doc, FixResult result)
        {
            List<FailureMessage> warnings = doc.GetWarnings().ToList();

            int duplicates = 0, unjoined = 0, marks = 0, separationLines = 0, tags = 0;
            var notAutomatable = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var marksUsed = new Dictionary<long, HashSet<string>>();
            var markCounters = new Dictionary<long, int>();

            foreach (FailureMessage warning in warnings)
            {
                string text = warning.GetDescriptionText() ?? string.Empty;

                List<Element> elements = warning.GetFailingElements()
                    .Concat(warning.GetAdditionalElements())
                    .Distinct()
                    .Select(id => doc.GetElement(id))
                    .Where(e => e != null)
                    .ToList();

                if (Has(text, "identical instances"))
                    duplicates += DeleteIdenticalInstances(doc, elements, result);
                else if (Has(text, "joined but do not intersect"))
                    unjoined += UnjoinElements(doc, elements, result);
                else if (Has(text, "duplicate") && Has(text, "mark"))
                    marks += RenumberDuplicateMarks(doc, elements, marksUsed, markCounters, result);
                else if (Has(text, "separation line") && Has(text, "overlap"))
                    separationLines += DeleteOverlappingLines(doc, elements, result);
                else if (Has(text, "tag is outside"))
                    tags += MoveTagsIntoRooms(doc, elements, result);
                else
                    notAutomatable[Shorten(text, 70)] = notAutomatable.TryGetValue(Shorten(text, 70), out int n) ? n + 1 : 1;
            }

            result.Notes.Add(
                $"{duplicates:N0} identical instance(s) deleted, {unjoined:N0} wrong join(s) removed, " +
                $"{marks:N0} duplicate Mark(s) renumbered, {separationLines:N0} overlapping separation line(s) deleted, " +
                $"{tags:N0} room tag(s) moved into their room.");

            if (notAutomatable.Count > 0)
            {
                result.Notes.Add(
                    $"{notAutomatable.Values.Sum():N0} warning(s) need a person. Most common: " +
                    string.Join("; ", notAutomatable.OrderByDescending(p => p.Value).Take(3)
                        .Select(p => $"{p.Value}× {p.Key}")));
            }
        }

        // "There are identical instances in the same place": keep the oldest.
        private static int DeleteIdenticalInstances(Document doc, List<Element> elements, FixResult result)
        {
            int deleted = 0;

            foreach (Element extra in elements.OfType<FamilyInstance>().OrderBy(e => e.Id.Value).Skip(1))
            {
                if (!CanEdit(doc, extra, forDelete: true) || HostsOtherElements(doc, extra))
                {
                    result.Skipped++;
                    continue;
                }

                ElementId id = extra.Id;

                if (TryInOwnTransaction(doc, () => doc.Delete(id)))
                {
                    deleted++;
                    result.Fixed++;
                }
                else
                {
                    result.Skipped++;
                }
            }

            return deleted;
        }

        // "Highlighted elements are joined but do not intersect": unjoin them.
        private static int UnjoinElements(Document doc, List<Element> elements, FixResult result)
        {
            if (elements.Count < 2)
                return 0;

            Element first = elements[0];
            Element second = elements[1];

            if (!JoinGeometryUtils.AreElementsJoined(doc, first, second))
                return 0;

            if (!CanEdit(doc, first, forDelete: false) || !CanEdit(doc, second, forDelete: false))
            {
                result.Skipped++;
                return 0;
            }

            if (TryInOwnTransaction(doc, () => JoinGeometryUtils.UnjoinGeometry(doc, first, second)))
            {
                result.Fixed++;
                return 1;
            }

            result.Skipped++;
            return 0;
        }

        // "Elements have duplicate 'Mark' values": the oldest keeps its Mark,
        // the others get the next free consecutive number in their category.
        private static int RenumberDuplicateMarks(
            Document doc,
            List<Element> elements,
            Dictionary<long, HashSet<string>> marksUsed,
            Dictionary<long, int> counters,
            FixResult result)
        {
            int renumbered = 0;

            foreach (Element element in elements.Where(e => e.Category != null).OrderBy(e => e.Id.Value).Skip(1))
            {
                Parameter? mark = element.get_Parameter(BuiltInParameter.ALL_MODEL_MARK);

                if (mark == null || mark.IsReadOnly || !CanEdit(doc, element, forDelete: false))
                {
                    result.Skipped++;
                    continue;
                }

                long categoryId = element.Category!.Id.Value;

                if (!marksUsed.TryGetValue(categoryId, out HashSet<string>? used))
                {
                    used = new HashSet<string>(
                        new FilteredElementCollector(doc)
                            .OfCategoryId(element.Category.Id)
                            .WhereElementIsNotElementType()
                            .Select(e => e.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)?.AsString())
                            .Where(v => !string.IsNullOrWhiteSpace(v))
                            .Select(v => v!.Trim()),
                        StringComparer.OrdinalIgnoreCase);

                    marksUsed[categoryId] = used;
                    counters[categoryId] = 1;
                }

                int counter = counters[categoryId];
                string value = NextFree(Abbreviate(element.Category.Name) + "-", 3, used, ref counter);
                counters[categoryId] = counter;

                if (TryInOwnTransaction(doc, () => mark.Set(value)))
                {
                    used.Add(value);
                    renumbered++;
                    result.Fixed++;
                }
                else
                {
                    result.Skipped++;
                }
            }

            return renumbered;
        }

        // "Room separation lines overlap": delete the shorter duplicate line.
        private static int DeleteOverlappingLines(Document doc, List<Element> elements, FixResult result)
        {
            List<CurveElement> lines = elements.OfType<CurveElement>().ToList();

            if (lines.Count < 2)
                return 0;

            CurveElement shorter = lines
                .OrderBy(l => l.GeometryCurve?.Length ?? double.MaxValue)
                .First();

            if (!CanEdit(doc, shorter, forDelete: true))
            {
                result.Skipped++;
                return 0;
            }

            ElementId id = shorter.Id;

            if (TryInOwnTransaction(doc, () => doc.Delete(id)))
            {
                result.Fixed++;
                return 1;
            }

            result.Skipped++;
            return 0;
        }

        // "Room Tag is outside of its Room": move the tag to the room's location point.
        private static int MoveTagsIntoRooms(Document doc, List<Element> elements, FixResult result)
        {
            int moved = 0;

            foreach (RoomTag tag in elements.OfType<RoomTag>())
            {
                if (!(tag.Room?.Location is LocationPoint roomPoint) ||
                    !(tag.Location is LocationPoint tagPoint) ||
                    !CanEdit(doc, tag, forDelete: false))
                {
                    result.Skipped++;
                    continue;
                }

                var delta = new XYZ(roomPoint.Point.X - tagPoint.Point.X, roomPoint.Point.Y - tagPoint.Point.Y, 0);
                ElementId tagId = tag.Id;

                if (TryInOwnTransaction(doc, () => ElementTransformUtils.MoveElement(doc, tagId, delta)))
                {
                    moved++;
                    result.Fixed++;
                }
                else
                {
                    result.Skipped++;
                }
            }

            return moved;
        }

        private static bool Has(string text, string part) =>
            text.IndexOf(part, StringComparison.OrdinalIgnoreCase) >= 0;

        private static string Shorten(string text, int max) =>
            text.Length <= max ? text : text.Substring(0, max - 1) + "…";

        // ================================================================
        // 5. UNUSED VIEWS
        // ================================================================

        private static void DeleteUnusedViews(UIDocument uiDocument, HealthRules rules, FixResult result)
        {
            Document doc = uiDocument.Document;

            var check = new CheckResult();
            new ViewsCheck().Run(new CheckContext(doc, rules), check);

            var placed = new HashSet<long>();

            foreach (Viewport viewport in new FilteredElementCollector(doc).OfClass(typeof(Viewport)).Cast<Viewport>())
                placed.Add(viewport.ViewId.Value);

            foreach (ScheduleSheetInstance schedule in new FilteredElementCollector(doc).OfClass(typeof(ScheduleSheetInstance)).Cast<ScheduleSheetInstance>())
                placed.Add(schedule.ScheduleId.Value);

            var protectedViews = new HashSet<long>(uiDocument.GetOpenUIViews().Select(v => v.ViewId.Value));
            protectedViews.Add(uiDocument.ActiveView.Id.Value);

            ElementId startingView = StartingViewSettings.GetStartingViewSettings(doc).ViewId;
            if (startingView != ElementId.InvalidElementId)
                protectedViews.Add(startingView.Value);

            foreach (long viewId in check.Issues.SelectMany(i => i.ElementIds).Distinct())
            {
                if (!(doc.GetElement(new ElementId(viewId)) is View view))
                    continue;

                if (placed.Contains(viewId) ||
                    protectedViews.Contains(viewId) ||
                    view.GetDependentViewIds().Count > 0 ||
                    OwnsLinks(doc, view.Id) ||
                    !CanEdit(doc, view, forDelete: true))
                {
                    result.Skipped++;
                    continue;
                }

                // Only accept the deletion if exactly one view disappears
                // (no callouts or other views deleted with it).
                int viewsBefore = CountViews(doc);

                using (var sub = new SubTransaction(doc))
                {
                    sub.Start();

                    try
                    {
                        doc.Delete(view.Id);
                    }
                    catch (Autodesk.Revit.Exceptions.ApplicationException)
                    {
                        sub.RollBack();
                        result.Skipped++;
                        continue;
                    }

                    if (viewsBefore - CountViews(doc) != 1)
                    {
                        sub.RollBack();
                        result.Skipped++;
                    }
                    else
                    {
                        sub.Commit();
                        result.Fixed++;
                    }
                }
            }
        }

        // Views that contain view-specific CAD or Revit links are never deleted.
        private static bool OwnsLinks(Document doc, ElementId viewId) =>
            new FilteredElementCollector(doc)
                .OfClass(typeof(ImportInstance))
                .Any(e => e.OwnerViewId == viewId) ||
            new FilteredElementCollector(doc)
                .OfClass(typeof(RevitLinkInstance))
                .Any(e => e.OwnerViewId == viewId);

        private static int CountViews(Document doc) =>
            new FilteredElementCollector(doc).OfClass(typeof(View)).GetElementCount();

        // ================================================================
        // 6. UNPLACED ROOMS
        // ================================================================

        private static void DeleteUnplacedRooms(Document doc, FixResult result)
        {
            List<Room> unplaced = new FilteredElementCollector(doc)
                .OfClass(typeof(SpatialElement))
                .WhereElementIsNotElementType()
                .OfType<Room>()
                .Where(r => r.Location == null)
                .ToList();

            foreach (Room room in unplaced)
            {
                if (CanEdit(doc, room, forDelete: true) && TryDelete(doc, room.Id))
                    result.Fixed++;
                else
                    result.Skipped++;
            }
        }

        // ================================================================
        // 7. EMPTY MARK / TYPE MARK VALUES (consecutive numbers)
        // ================================================================

        private static void NumberEmptyMarks(Document doc, HealthRules rules, FixResult result)
        {
            var context = new CheckContext(doc, rules);

            List<RequiredParameterRule> ruleSets =
                rules.RequiredParameters.Concat(rules.DoorsAndWindows.Requirements).ToList();

            HashSet<BuiltInCategory> markCategories = context.ResolveCategories(ruleSets
                .Where(r => r.Parameters.Any(p => IsMarkParameter(p.Name)))
                .SelectMany(r => r.Categories));

            HashSet<BuiltInCategory> typeMarkCategories = context.ResolveCategories(ruleSets
                .Where(r => r.Parameters.Any(p => IsTypeMarkParameter(p.Name)))
                .SelectMany(r => r.Categories));

            if (markCategories.Count == 0 && typeMarkCategories.Count == 0)
            {
                result.Notes.Add("Mark and Type Mark are not required parameters in the rules file.");
                return;
            }

            int marks = NumberInstanceMarks(doc, context, markCategories, result);
            int typeMarks = NumberTypeMarks(doc, context, typeMarkCategories, result);

            result.Notes.Add($"{marks:N0} Mark value(s) and {typeMarks:N0} Type Mark value(s) filled in.");
        }

        // Mark: consecutive per category, ordered by level, then north-to-south, west-to-east.
        private static int NumberInstanceMarks(
            Document doc, CheckContext context, HashSet<BuiltInCategory> categories, FixResult result)
        {
            int filled = 0;

            if (categories.Count == 0)
                return filled;

            foreach (var byCategory in context.ElementsOfCategories(categories)
                         .Where(e => !CheckContext.IsNestedComponent(e))
                         .GroupBy(e => e.Category!.Id.Value))
            {
                List<Element> elements = byCategory
                    .OrderBy(e => LevelElevation(doc, e))
                    .ThenByDescending(e => Position(e).Y)
                    .ThenBy(e => Position(e).X)
                    .ToList();

                string prefix = Abbreviate(elements[0].Category!.Name);

                var used = new HashSet<string>(
                    elements.Select(e => e.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)?.AsString())
                            .Where(v => !string.IsNullOrWhiteSpace(v))
                            .Select(v => v!.Trim()),
                    StringComparer.OrdinalIgnoreCase);

                int counter = 1;

                foreach (Element element in elements)
                {
                    Parameter? mark = element.get_Parameter(BuiltInParameter.ALL_MODEL_MARK);

                    if (mark == null || mark.IsReadOnly || !string.IsNullOrWhiteSpace(mark.AsString()))
                        continue;

                    if (!CanEdit(doc, element, forDelete: false))
                    {
                        result.Skipped++;
                        continue;
                    }

                    string value = NextFree(prefix + "-", 3, used, ref counter);

                    try
                    {
                        mark.Set(value);
                        used.Add(value);
                        result.Fixed++;
                        filled++;
                    }
                    catch (Autodesk.Revit.Exceptions.ApplicationException)
                    {
                        result.Skipped++;
                    }
                }
            }

            return filled;
        }

        // Type Mark: consecutive per category, one value per type (e.g. W-T01).
        private static int NumberTypeMarks(
            Document doc, CheckContext context, HashSet<BuiltInCategory> categories, FixResult result)
        {
            int filled = 0;

            if (categories.Count == 0)
                return filled;

            var typesByCategory = context.ElementsOfCategories(categories)
                .Where(e => !CheckContext.IsNestedComponent(e))
                .Select(e => context.GetTypeElement(e))
                .OfType<ElementType>()
                .Where(t => t.Category != null)
                .GroupBy(t => t.Id.Value)
                .Select(g => g.First())
                .GroupBy(t => t.Category!.Id.Value);

            foreach (var byCategory in typesByCategory)
            {
                List<ElementType> types = byCategory.OrderBy(t => t.FamilyName).ThenBy(t => t.Name).ToList();
                string prefix = Abbreviate(types[0].Category!.Name);

                var used = new HashSet<string>(
                    types.Select(t => t.get_Parameter(BuiltInParameter.ALL_MODEL_TYPE_MARK)?.AsString())
                         .Where(v => !string.IsNullOrWhiteSpace(v))
                         .Select(v => v!.Trim()),
                    StringComparer.OrdinalIgnoreCase);

                int counter = 1;

                foreach (ElementType type in types)
                {
                    Parameter? typeMark = type.get_Parameter(BuiltInParameter.ALL_MODEL_TYPE_MARK);

                    if (typeMark == null || typeMark.IsReadOnly || !string.IsNullOrWhiteSpace(typeMark.AsString()))
                        continue;

                    if (!CanEdit(doc, type, forDelete: false))
                    {
                        result.Skipped++;
                        continue;
                    }

                    string value = NextFree(prefix + "-T", 2, used, ref counter);

                    try
                    {
                        typeMark.Set(value);
                        used.Add(value);
                        result.Fixed++;
                        filled++;
                    }
                    catch (Autodesk.Revit.Exceptions.ApplicationException)
                    {
                        result.Skipped++;
                    }
                }
            }

            return filled;
        }

        // Next consecutive value that is not used yet, e.g. "SC-" + 007.
        private static string NextFree(string prefix, int digits, HashSet<string> used, ref int counter)
        {
            string value;

            do
            {
                value = prefix + counter.ToString(new string('0', digits));
                counter++;
            }
            while (used.Contains(value));

            return value;
        }

        private static double LevelElevation(Document doc, Element element) =>
            (doc.GetElement(element.LevelId) as Level)?.ProjectElevation ?? double.MaxValue;

        private static XYZ Position(Element element)
        {
            switch (element.Location)
            {
                case LocationPoint point:
                    return point.Point;

                case LocationCurve curve when curve.Curve != null && curve.Curve.IsBound:
                    return curve.Curve.Evaluate(0.5, true);

                default:
                    BoundingBoxXYZ? box = element.get_BoundingBox(null);
                    return box == null ? XYZ.Zero : (box.Min + box.Max) / 2.0;
            }
        }

        private static bool IsTypeMarkParameter(string name)
        {
            string n = (name ?? string.Empty).Trim();
            return n.Equals("Type Mark", StringComparison.OrdinalIgnoreCase) ||
                   n.Equals("BuiltIn:ALL_MODEL_TYPE_MARK", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsMarkParameter(string name)
        {
            string n = (name ?? string.Empty).Trim();
            return n.Equals("Mark", StringComparison.OrdinalIgnoreCase) ||
                   n.Equals("BuiltIn:ALL_MODEL_MARK", StringComparison.OrdinalIgnoreCase);
        }

        // "Structural Columns" -> "SC", "Doors" -> "DO"
        private static string Abbreviate(string categoryName)
        {
            string[] words = categoryName
                .Split(new[] { ' ', '-', '_' }, StringSplitOptions.RemoveEmptyEntries);

            if (words.Length >= 2)
                return string.Concat(words.Take(3).Select(w => char.ToUpperInvariant(w[0])));

            string word = words.Length == 1 ? words[0] : "EL";
            return word.Substring(0, Math.Min(2, word.Length)).ToUpperInvariant();
        }

        // ================================================================
        // HELPERS
        // ================================================================

        // Skips elements in groups, pinned elements (for deletion), elements
        // owned by someone else and elements changed in central since the last sync.
        private static bool CanEdit(Document doc, Element element, bool forDelete)
        {
            if (element.GroupId != ElementId.InvalidElementId)
                return false;

            if (forDelete && element.Pinned)
                return false;

            if (!doc.IsWorkshared)
                return true;

            try
            {
                if (WorksharingUtils.GetCheckoutStatus(doc, element.Id) == CheckoutStatus.OwnedByOtherUser)
                    return false;

                if (WorksharingUtils.GetModelUpdatesStatus(doc, element.Id) == ModelUpdatesStatus.UpdatedInCentral)
                    return false;
            }
            catch (Autodesk.Revit.Exceptions.ApplicationException)
            {
                return false;
            }

            return true;
        }

        private static bool TryDelete(Document doc, ElementId id)
        {
            try
            {
                doc.Delete(id);
                return true;
            }
            catch (Autodesk.Revit.Exceptions.ApplicationException)
            {
                return false;
            }
        }
    }

    // Removes Revit warnings raised by the fixes and accepts Revit's default
    // resolution for errors, so no dialogs interrupt the batch.
    internal sealed class SilentFailurePreprocessor : IFailuresPreprocessor
    {
        public FailureProcessingResult PreprocessFailures(FailuresAccessor failuresAccessor)
        {
            bool resolved = false;

            foreach (FailureMessageAccessor failure in failuresAccessor.GetFailureMessages())
            {
                if (failure.GetSeverity() == FailureSeverity.Warning)
                {
                    failuresAccessor.DeleteWarning(failure);
                }
                else if (failure.HasResolutions())
                {
                    failuresAccessor.ResolveFailure(failure);
                    resolved = true;
                }
            }

            return resolved
                ? FailureProcessingResult.ProceedWithCommit
                : FailureProcessingResult.Continue;
        }
    }

    // Removes warnings; rolls the transaction back if Revit reports an error
    // (used for wall changes, so a failed wall is simply left as it was).
    internal sealed class RollbackOnErrorPreprocessor : IFailuresPreprocessor
    {
        public FailureProcessingResult PreprocessFailures(FailuresAccessor failuresAccessor)
        {
            foreach (FailureMessageAccessor failure in failuresAccessor.GetFailureMessages())
            {
                if (failure.GetSeverity() == FailureSeverity.Warning)
                    failuresAccessor.DeleteWarning(failure);
                else
                    return FailureProcessingResult.ProceedWithRollBack;
            }

            return FailureProcessingResult.Continue;
        }
    }
}
