using System;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BIMModelHealthDashboard.Config
{
    // Loads HealthRules.json.
    //  1. A HealthRules.json next to the add-in DLL (company-wide rules), else
    //  2. %AppData%\BIMModelHealthDashboard\HealthRules.json (created on first use).
    public static class RulesRepository
    {
        private const string FileName = "HealthRules.json";

        private static readonly JsonSerializerOptions Options = new JsonSerializerOptions
        {
            WriteIndented = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
            PropertyNameCaseInsensitive = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.Never
        };

        public static string UserRulesPath =>
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "BIMModelHealthDashboard",
                FileName);

        public static string? CompanyRulesPath
        {
            get
            {
                string? folder = Path.GetDirectoryName(
                    Assembly.GetExecutingAssembly().Location);

                return string.IsNullOrEmpty(folder)
                    ? null
                    : Path.Combine(folder, FileName);
            }
        }

        // The file actually used for scans.
        public static string ActiveRulesPath
        {
            get
            {
                string? company = CompanyRulesPath;

                return company != null && File.Exists(company)
                    ? company
                    : UserRulesPath;
            }
        }

        public static HealthRules Load(out string source, out string? error)
        {
            error = null;
            source = ActiveRulesPath;

            try
            {
                EnsureExists();

                string json = File.ReadAllText(source);
                HealthRules? rules = JsonSerializer.Deserialize<HealthRules>(json, Options);

                // Upgrade an older per-user rules file to the new defaults.
                // (A company rules file next to the DLL is never overwritten.)
                if (rules != null &&
                    rules.Version < HealthRules.CurrentVersion &&
                    string.Equals(source, UserRulesPath, StringComparison.OrdinalIgnoreCase))
                {
                    string backup = source + $".v{rules.Version}.bak";
                    File.Copy(source, backup, true);

                    rules = new HealthRules();
                    File.WriteAllText(source, JsonSerializer.Serialize(rules, Options));

                    error = $"The rules file was updated to version {HealthRules.CurrentVersion} " +
                            $"(improved defaults). Your previous rules were saved as {Path.GetFileName(backup)}.";
                }

                return rules ?? new HealthRules();
            }
            catch (Exception ex) when (
                ex is IOException ||
                ex is UnauthorizedAccessException ||
                ex is JsonException ||
                ex is NotSupportedException)
            {
                error = $"Could not read the rules file ({ex.Message}). Built-in default rules were used.";
                source = "Built-in defaults";
                return new HealthRules();
            }
        }

        // Creates the per-user rules file with defaults if no rules file exists.
        public static void EnsureExists()
        {
            if (File.Exists(ActiveRulesPath))
                return;

            string path = UserRulesPath;
            string? folder = Path.GetDirectoryName(path);

            if (!string.IsNullOrEmpty(folder))
                Directory.CreateDirectory(folder);

            File.WriteAllText(path, JsonSerializer.Serialize(new HealthRules(), Options));
        }
    }
}
