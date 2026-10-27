using System.Collections.Generic;

namespace BIMModelHealthDashboard.Config
{
    // Company standards used by the validation checks. Saved as
    // HealthRules.json so BIM managers can change rules without recompiling.
    //
    // Conventions:
    //  - Categories use Revit BuiltInCategory names, e.g. "OST_Doors"
    //    ("Doors" is also accepted).
    //  - Parameter names are the names shown in Revit, or
    //    "BuiltIn:ALL_MODEL_MARK" for language-independent built-in
    //    parameters. Instance parameters are checked first, then type.
    //  - Patterns are .NET regular expressions; start with (?i) to ignore case.
    public class HealthRules
    {
        public string About { get; set; } =
            "BIM Model Health Dashboard rules. Edit, save, then re-run the scan. " +
            "Categories: BuiltInCategory names (OST_Doors). Parameters: Revit names, " +
            "or BuiltIn:ALL_MODEL_MARK. Patterns: .NET regex, (?i) = ignore case. " +
            "Naming targets: Views, ViewTemplates, SheetNumbers, SheetNames, Levels, " +
            "Grids, Families, FamilyTypes, Materials, Worksets, Rooms.";

        // Bump when defaults change; older per-user rules files are upgraded
        // automatically (the old file is kept as a .bak).
        public const int CurrentVersion = 4;

        public int Version { get; set; } = CurrentVersion;

        // 01 ELEMENT CHECKER ------------------------------------------------

        public List<RequiredParameterRule> RequiredParameters { get; set; } = new List<RequiredParameterRule>
        {
            new RequiredParameterRule
            {
                Categories = { "OST_Walls", "OST_Floors", "OST_Roofs", "OST_Ceilings" },
                Parameters = { new ParameterRequirement("BuiltIn:ALL_MODEL_TYPE_MARK") }
            },
            new RequiredParameterRule
            {
                Categories =
                {
                    "OST_MechanicalEquipment", "OST_PlumbingFixtures",
                    "OST_ElectricalEquipment", "OST_LightingFixtures",
                    "OST_Furniture", "OST_SpecialityEquipment"
                },
                Parameters = { new ParameterRequirement("BuiltIn:ALL_MODEL_MARK") }
            },
            new RequiredParameterRule
            {
                Categories = { "OST_StructuralColumns", "OST_StructuralFraming", "OST_StructuralFoundation" },
                Parameters =
                {
                    new ParameterRequirement("BuiltIn:ALL_MODEL_MARK"),
                    new ParameterRequirement("BuiltIn:STRUCTURAL_MATERIAL_PARAM")
                }
            }
        };

        public FamilyTypeRules FamilyTypes { get; set; } = new FamilyTypeRules();

        public DuplicateRules Duplicates { get; set; } = new DuplicateRules();

        public DoorWindowRules DoorsAndWindows { get; set; } = new DoorWindowRules();

        // 02 WALL CHECKER ---------------------------------------------------

        public WarningRules Warnings { get; set; } = new WarningRules();

        public WallRules Walls { get; set; } = new WallRules();

        public WorksetRules Worksets { get; set; } = new WorksetRules();

        public MaterialRules Materials { get; set; } = new MaterialRules();

        // 03 ROOM CHECKER ---------------------------------------------------

        public ViewRules Views { get; set; } = new ViewRules();

        public LevelRules Levels { get; set; } = new LevelRules();

        // 04 STANDARDS & COMPLIANCE ------------------------------------------

        public FamilyStandardRules FamilyStandards { get; set; } = new FamilyStandardRules();

        public CoordinateRules Coordinates { get; set; } = new CoordinateRules();

        public List<NamingRule> Naming { get; set; } = new List<NamingRule>
        {
            new NamingRule
            {
                Target = "Views",
                DisallowedPatterns = { @"(?i)\bcopy\b", @"^\s|\s$", @"\s{2,}" },
                Description = "no 'Copy', no leading/trailing or double spaces"
            },
            new NamingRule
            {
                Target = "SheetNumbers",
                RequiredPattern = @"^[A-Za-z0-9]+([-_.][A-Za-z0-9]+)*$",
                Description = "letters/digits separated only by - _ or ."
            },
            new NamingRule
            {
                Target = "Levels",
                DisallowedPatterns = { @"(?i)\bcopy\b", @"^\s|\s$" },
                Description = "no 'Copy', no leading/trailing spaces"
            },
            new NamingRule
            {
                Target = "Grids",
                RequiredPattern = @"^([A-Za-z]{1,2}|\d{1,3})(\.\d+)?$",
                Description = "letters (A, AA) or numbers (1, 12), optionally .1"
            },
            new NamingRule
            {
                Target = "Families",
                DisallowedPatterns = { @"(?i)\bcopy\b", @"(?i)^family\d*$" },
                Description = "no 'Copy' and no default 'Family1' names"
            },
            new NamingRule
            {
                Target = "FamilyTypes",
                DisallowedPatterns = { @"(?i)\bcopy\b" },
                Description = "no 'Copy' in type names"
            }
        };

        public CobieRules Cobie { get; set; } = new CobieRules();
    }

    public class RequiredParameterRule
    {
        public List<string> Categories { get; set; } = new List<string>();
        public List<ParameterRequirement> Parameters { get; set; } = new List<ParameterRequirement>();
    }

    public class ParameterRequirement
    {
        public ParameterRequirement()
        {
        }

        public ParameterRequirement(string name)
        {
            Name = name;
        }

        public string Name { get; set; } = string.Empty;

        // Optional regex the value must match ("incorrect" value check).
        public string? Pattern { get; set; }

        // Optional list of allowed values (case-insensitive).
        public List<string>? AllowedValues { get; set; }
    }

    public class FamilyTypeRules
    {
        // Per category, the approved family/type names. Leave lists empty
        // to skip that part. Example:
        // { "Category": "OST_Doors", "AllowedFamilyNamePatterns": ["^ABC_Door_"] }
        public List<CategoryFamilyRule> Categories { get; set; } = new List<CategoryFamilyRule>();

        // Type names that indicate placeholder/default content.
        public List<string> DisallowedTypeNamePatterns { get; set; } = new List<string>
        {
            @"(?i)^generic\b",
            @"(?i)\bdefault\b",
            @"(?i)\bplaceholder\b",
            @"(?i)\bcopy\b"
        };

        // Flag elements modelled as Generic Models instead of a real category.
        public bool FlagGenericModelInstances { get; set; } = true;
    }

    public class CategoryFamilyRule
    {
        public string Category { get; set; } = string.Empty;
        public List<string> AllowedFamilyNamePatterns { get; set; } = new List<string>();
        public List<string> AllowedTypeNamePatterns { get; set; } = new List<string>();
    }

    public class DuplicateRules
    {
        public double ToleranceMm { get; set; } = 1.0;

        public List<string> IgnoreCategories { get; set; } = new List<string>
        {
            "OST_Rooms", "OST_Areas", "OST_MEPSpaces",
            "OST_CurtainWallPanels", "OST_CurtainWallMullions",
            "OST_IOSModelGroups", "OST_RvtLinks"
        };
    }

    public class DoorWindowRules
    {
        public List<string> Categories { get; set; } = new List<string> { "OST_Doors", "OST_Windows" };

        public List<RequiredParameterRule> Requirements { get; set; } = new List<RequiredParameterRule>
        {
            new RequiredParameterRule
            {
                Categories = { "OST_Doors" },
                Parameters =
                {
                    new ParameterRequirement("BuiltIn:ALL_MODEL_MARK"),
                    new ParameterRequirement("BuiltIn:ALL_MODEL_TYPE_MARK")
                }
            },
            new RequiredParameterRule
            {
                Categories = { "OST_Windows" },
                Parameters =
                {
                    new ParameterRequirement("BuiltIn:ALL_MODEL_MARK"),
                    new ParameterRequirement("BuiltIn:ALL_MODEL_TYPE_MARK")
                }
            }
        };

        public bool RequireHost { get; set; } = true;

        // Categories that must have a From Room or To Room
        // (only checked when the model contains placed rooms).
        public List<string> RequireRoomAssociationFor { get; set; } = new List<string> { "OST_Doors" };
    }

    public class WarningRules
    {
        public List<string> HighSeverityPatterns { get; set; } = new List<string>
        {
            @"(?i)identical instances",
            @"(?i)overlap",
            @"(?i)not in a properly enclosed region",
            @"(?i)same enclosed region"
        };

        public List<string> LowSeverityPatterns { get; set; } = new List<string>
        {
            @"(?i)slightly off axis"
        };

        public List<string> IgnorePatterns { get; set; } = new List<string>();
    }

    public class WallRules
    {
        // Unconnected walls (Revit Wall Checker logic): a wall end is checked
        // when another wall's location line is within this distance (feet).
        // Use the same value as ConnectionToleranceFeet in WallCheckerSettings.json.
        public bool RequireConnectionWhenNearbyWall { get; set; } = true;
        public double ConnectionToleranceFeet { get; set; } = 0.5;

        // Distance within which a wall end counts as touching another wall.
        public double JoinToleranceMm { get; set; } = 10.0;

        public bool IgnoreCurtainWalls { get; set; } = false;

        public List<string> IgnoreWallTypeNamePatterns { get; set; } = new List<string>
        {
            @"(?i)fence",
            @"(?i)free-?standing"
        };

        // Fix All Issues: an unconnected wall end is extended/trimmed to meet
        // another wall when the gap is at most this distance.
        public double AutoFixSnapDistanceMm { get; set; } = 300.0;
    }

    public class WorksetRules
    {
        public List<WorksetAssignmentRule> Assignments { get; set; } = new List<WorksetAssignmentRule>
        {
            new WorksetAssignmentRule
            {
                Categories = { "OST_Levels", "OST_Grids" },
                ExpectedWorksetPattern = @"(?i)(level|grid)",
                Description = "Shared Levels and Grids workset"
            },
            new WorksetAssignmentRule
            {
                Categories = { "OST_RvtLinks" },
                ExpectedWorksetPattern = @"(?i)link",
                Description = "a workset for Revit links"
            }
        };

        // Worksets that model elements must not be on.
        public List<string> DisallowedWorksetPatterns { get; set; } = new List<string> { @"^Workset1$" };
    }

    public class WorksetAssignmentRule
    {
        public List<string> Categories { get; set; } = new List<string>();
        public string ExpectedWorksetPattern { get; set; } = string.Empty;
        public string? Description { get; set; }
    }

    public class MaterialRules
    {
        // Categories whose materials are checked against the name rules.
        public List<string> CheckCategories { get; set; } = new List<string>
        {
            "OST_Walls", "OST_Floors", "OST_Roofs", "OST_Ceilings",
            "OST_StructuralColumns", "OST_StructuralFraming", "OST_StructuralFoundation",
            "OST_Doors", "OST_Windows"
        };

        // Categories where "<By Category>" / no material is not acceptable.
        public List<string> RequireMaterialCategories { get; set; } = new List<string>
        {
            "OST_Walls", "OST_Floors", "OST_Roofs", "OST_Ceilings",
            "OST_StructuralColumns", "OST_StructuralFraming", "OST_StructuralFoundation"
        };

        // If not empty, every material must match one of these (approved library).
        public List<string> AllowedNamePatterns { get; set; } = new List<string>();

        public List<string> DisallowedNamePatterns { get; set; } = new List<string>
        {
            @"(?i)^default",
            @"(?i)^generic",
            @"\(\d+\)$",
            @"(?i)\bcopy\b"
        };
    }

    public class ViewRules
    {
        public List<string> DuplicateNamePatterns { get; set; } = new List<string>
        {
            @"(?i)\bcopy\s*\d*$",
            @"\(\d+\)$"
        };

        // Views matching these are never reported as unused.
        public List<string> UnusedIgnorePatterns { get; set; } = new List<string>
        {
            @"^\{3D",
            @"(?i)^(working|wip|coord|coordination|export|navis|_)"
        };

        public bool ReportUnusedViews { get; set; } = true;
        public bool IncludeSchedules { get; set; } = true;
        public bool IncludeLegends { get; set; } = false;
    }

    public class LevelRules
    {
        // How far below its level an element may sit (e.g. beams below slab).
        public double BelowLevelToleranceM { get; set; } = 1.0;

        public double ElevationToleranceMm { get; set; } = 50.0;

        public bool FlagUnusedLevels { get; set; } = true;
        public bool FlagLevelsAtSameElevation { get; set; } = true;
    }

    public class FamilyStandardRules
    {
        // Example: "^ABC_" to require a company prefix. Null = not checked.
        public string? RequiredNamePattern { get; set; }

        public bool DisallowInPlaceFamilies { get; set; } = true;
        public bool FlagUnusedFamilies { get; set; } = true;
        public int MaxTypesPerFamily { get; set; } = 50;

        // Type parameters every family type must have filled in,
        // e.g. "Manufacturer", "BuiltIn:ALL_MODEL_DESCRIPTION".
        public List<string> RequiredTypeParameters { get; set; } = new List<string>();

        // Families in these categories are used indirectly and are never "unused".
        public List<string> UnusedIgnoreCategories { get; set; } = new List<string>
        {
            "OST_ProfileFamilies", "OST_StairsRailingBaluster",
            "OST_RailingSupport", "OST_RailingTermination",
            "OST_DetailComponents", "OST_CurtainWallPanels"
        };
    }

    public class CoordinateRules
    {
        // Elements must lie within this distance of the internal origin.
        // 32 km is Revit's own "far from origin" limit (about 20 miles);
        // set your project's site limit here if it is smaller.
        public double MaxHorizontalDistanceM { get; set; } = 32000.0;
        public double MaxVerticalDistanceM { get; set; } = 1000.0;

        public bool CheckProjectBasePoint { get; set; } = true;

        public List<string> IgnoreCategories { get; set; } = new List<string>();
    }

    public class NamingRule
    {
        public string Target { get; set; } = string.Empty;
        public string? RequiredPattern { get; set; }
        public List<string> DisallowedPatterns { get; set; } = new List<string>();
        public string? Description { get; set; }
    }

    public class CobieRules
    {
        public bool Enabled { get; set; } = true;

        public List<string> Categories { get; set; } = new List<string>
        {
            "OST_MechanicalEquipment", "OST_PlumbingFixtures", "OST_ElectricalEquipment",
            "OST_LightingFixtures", "OST_SpecialityEquipment", "OST_Furniture",
            "OST_Doors", "OST_Windows", "OST_Sprinklers", "OST_FireAlarmDevices",
            "OST_DuctTerminal"
        };

        public List<string> RequiredTypeParameters { get; set; } = new List<string>
        {
            "COBie.Type.Manufacturer",
            "COBie.Type.ModelNumber",
            "COBie.Type.WarrantyGuarantorParts",
            "COBie.Type.WarrantyDurationParts",
            "COBie.Type.ExpectedLife"
        };

        public List<string> RequiredInstanceParameters { get; set; } = new List<string>
        {
            "COBie.Component.Space",
            "COBie.Component.SerialNumber",
            "COBie.Component.InstallationDate"
        };

        // Yes/No parameter; elements where it is "No" are skipped.
        public string? InclusionParameter { get; set; } = "COBie";
    }
}
