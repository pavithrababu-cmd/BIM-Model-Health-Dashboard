# BIM Model Health Dashboard (Revit 2025)

Scans the active Revit model against company standards and shows the results
in a dashboard with a health score, per-check issue counts and a results browser
that selects problem elements in Revit.

## The 16 checks

| # | Group | Check | How it works |
|---|-------|-------|--------------|
| E1 | Element | Missing/incorrect parameters | `RequiredParameters` rules per category; optional regex `Pattern` / `AllowedValues` |
| E2 | Element | Wrong family types | Approved family/type name patterns per category, placeholder type names, Generic Models |
| E3 | Element | Duplicate elements | Same category + type + location (+ rotation / curve / extents) within `ToleranceMm` |
| E4 | Element | Doors/windows without required parameters | Required parameters, host, and From/To Room for doors |
| W1 | Wall | Warnings in the Revit model | Every Revit warning, severity from `Warnings` patterns |
| W2 | Wall | Unconnected walls | **Revit Wall Checker logic:** wall end within `ConnectionToleranceFeet` of another wall, with joining disallowed |
| W3 | Wall | Elements on wrong worksets | Category → workset rules; no model elements on `Workset1` |
| W4 | Wall | Incorrect materials | `<By Category>` layers/elements; default, duplicated or non-library materials |
| R1 | Room | Rooms not properly enclosed | Placed rooms with zero area (not enclosed / redundant) |
| R2 | Room | Unplaced rooms | Rooms with no location |
| R3 | Room | Duplicate or unused views | "Copy N" style names; views/schedules not placed on a sheet |
| R4 | Room | Incorrect levels | Element hosted on one level but physically on another; duplicate/unused levels |
| S1 | Standards | Families that don't meet standards | Name pattern, in-place, unused, too many types, required type parameters |
| S2 | Standards | Elements outside required coordinates | Elements, CAD imports, links and Project Base Point beyond the allowed distance from the internal origin |
| S3 | Standards | Naming convention violations | Regex rules for views, sheets, levels, grids, families, types, materials, worksets, rooms |
| S4 | Standards | Missing COBie/asset information | COBie type and component parameters on asset categories |

## Using it

- **Run Full Scan** runs everything. Each checker card's **Run … Checker** button runs just that group.
- Click a check line (e.g. "• Unconnected walls  12 issues") to open its issues.
- In the results window, double-click an issue (or use **Select in Revit**) to select and zoom to the elements.
  For a view issue, the view is opened.
- **Export Report** saves an HTML report (for the manager) or CSV (for Excel).

**Health score.** Each check that ran is worth an equal share of 100 points (6.25 each for 16 checks).
A failing check loses part of its share, based on two things:
- **Severity:** High or Critical issues cost 100% of the share, Medium 70%, Low 40%.
- **How much of the model is affected:** a failing check always loses at least 25% of its share. If a third or more of the items it checked are affected, it loses the whole share.

## Health panels

There are three panels, each showing an icon and a large value with a traffic-light colour. Hover over a value to see why it matters and what the target is.

| Project readiness | Model organization | Dependencies |
|---|---|---|
| Sheets without views | Levels | Revit links |
| Views missing templates | Model groups | CAD references |
| Missing title blocks | In-place families | Missing references (link files not found) |

## Export Report (PDF)

**Export Report** saves a PDF by default (CSV is also available, for Excel). The PDF has five sections:

1. **Health score:** before and after Fix All Issues, the improvement, and score bars.
2. **What was fixed:** each automatic fix with its fixed and skipped counts.
3. **Issues per check:** before → after → change, for all 16 checks.
4. **Health indicators:** Performance, Content and Coordination, before → now.
5. **Remaining issues:** the 5 most serious issues per check, for a person to fix.

The "before" state is captured automatically the first time Fix All Issues runs in a session, and fixes from later runs are added up. If you haven't run Fix All Issues yet, the PDF shows the current state only.

## Fix All Issues

**Fix All Issues** opens a dialog of automatic fixes. All the fixes you select run as one Revit undo step
(**BIM Health: Fix All Issues**), so a single Ctrl+Z reverts them all. Once the fixes finish, all 16 checks run again
and a before/after summary appears, showing issues and health score.

| Fix | Related check | Default |
|---|---|---|
| Delete duplicate family instances (keeps the oldest; skips hosts, pinned and grouped elements) | Duplicate elements | On |
| Purge unused loadable families (only if nothing else is deleted with them) | Families not to standard | On |
| Connect unconnected walls (Wall Checker logic): re-allow the join; if Revit still doesn't join them, extend/trim onto the nearby wall | Unconnected walls | On |
| Resolve common Revit warnings: identical instances, joined-but-not-intersecting, duplicate Marks, overlapping separation lines, room tags outside rooms | Warnings | On |
| Move elements to the workset their rule requires | Wrong worksets | On |
| Delete views not on sheets (never open, starting or parent views, nor views containing CAD/Revit links) | Duplicate/unused views | Off |
| Delete unplaced rooms | Unplaced rooms | Off |
| Number empty Mark values consecutively per category (by level and position, e.g. SC-001) and empty Type Marks per type (e.g. W-T01) | Missing parameters | On |

**What it skips:** in a workshared model, elements owned by other users or changed in central are skipped. Sync first.

**What still needs a person:** Revit warnings, materials, family types, levels, coordinates, naming,
COBie data and parameters other than Mark.

## Rules file versions 2 and 3

The defaults changed in version 2:
- The coordinate limit is now 32 km, Revit's own far-from-origin limit.
- "Fire Rating" is no longer required on doors.
- `<Sketch>` lines are ignored everywhere.
- **Version 3:** the wall auto-fix snap distance is now 300 mm (`Walls.AutoFixSnapDistanceMm`).

An older per-user `HealthRules.json` is upgraded automatically on the first scan. The old file is kept as
`HealthRules.json.v1.bak`.

## Company rules: `HealthRules.json`

All "correct" values are company-specific, so they live in a JSON file rather than in code.
Open the rules file in a text editor, change it, save it, then re-run the scan. Rules are re-read on every scan.

The file is looked up in this order:
1. `HealthRules.json` next to `BIMModelHealthDashboard.dll`. Put the file here to share one rule set with the whole team.
2. `%AppData%\BIMModelHealthDashboard\HealthRules.json`. This per-user file is created with defaults on first use.

Rule conventions:
- **Categories** are `BuiltInCategory` names, e.g. `OST_Doors`.
- **Parameters** use their Revit name (e.g. `Fire Rating`) or `BuiltIn:ALL_MODEL_MARK`. The `BuiltIn:` form is language-independent.
- **Patterns** are .NET regular expressions. Start a pattern with `(?i)` to ignore case.
- **Mistakes in the rules file** do not stop the scan. An unknown category or an invalid regex is listed in yellow on the check that used it.

The defaults are sensible starting points. You should still set your own values, especially these:
- `FamilyTypes.Categories` (approved families per category)
- `FamilyStandards.RequiredNamePattern` (e.g. `^ABC_`)
- `Materials.AllowedNamePatterns` (your material library)
- `Coordinates.MaxHorizontalDistanceM`
- `Naming` patterns

## Install

1. Build (Release, x64) in Visual Studio. The Revit 2025 API DLLs are referenced from `C:\Program Files\Autodesk\Revit 2025\`.
2. Copy `BIMModelHealthDashboard.dll` to e.g. `%AppData%\Autodesk\Revit\Addins\2025\BIMModelHealthDashboard\`.
3. Copy `BIMModelHealthDashboard.addin` to `%AppData%\Autodesk\Revit\Addins\2025\`.
   If you already have a manifest for this add-in, keep only one of them.

## Notes

- The checks are read-only. Nothing in the model is changed.
- Some redundant-room detection reads Revit's warning text, which assumes an English Revit UI.
- On very large models, the full scan can take a minute. Revit is busy while it runs.
