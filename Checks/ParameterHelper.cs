using Autodesk.Revit.DB;
using BIMModelHealthDashboard.Config;
using System;
using System.Collections.Generic;
using System.Linq;

namespace BIMModelHealthDashboard.Checks
{
    public static class ParameterHelper
    {
        private const string BuiltInPrefix = "BuiltIn:";

        // Finds a parameter on the instance first, then on its type.
        public static Parameter? Find(Element element, Document doc, string name)
        {
            Parameter? parameter = FindOn(element, name);

            if (parameter != null)
                return parameter;

            ElementId typeId = element.GetTypeId();

            if (typeId == ElementId.InvalidElementId)
                return null;

            Element? type = doc.GetElement(typeId);

            return type == null ? null : FindOn(type, name);
        }

        public static Parameter? FindOn(Element element, string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return null;

            if (name.StartsWith(BuiltInPrefix, StringComparison.OrdinalIgnoreCase))
            {
                string bipName = name.Substring(BuiltInPrefix.Length).Trim();

                return Enum.TryParse(bipName, true, out BuiltInParameter bip)
                    ? element.get_Parameter(bip)
                    : null;
            }

            return element.LookupParameter(name.Trim());
        }

        public static bool IsBuiltInNameValid(string name)
        {
            if (!name.StartsWith(BuiltInPrefix, StringComparison.OrdinalIgnoreCase))
                return true;

            return Enum.TryParse(
                name.Substring(BuiltInPrefix.Length).Trim(),
                true,
                out BuiltInParameter _);
        }

        public static bool HasValue(Parameter parameter)
        {
            if (!parameter.HasValue)
                return false;

            switch (parameter.StorageType)
            {
                case StorageType.String:
                    return !string.IsNullOrWhiteSpace(parameter.AsString());

                case StorageType.ElementId:
                    return parameter.AsElementId() != ElementId.InvalidElementId;

                default:
                    return true;
            }
        }

        public static string GetValueText(Parameter parameter)
        {
            switch (parameter.StorageType)
            {
                case StorageType.String:
                    return parameter.AsString() ?? string.Empty;

                case StorageType.Integer:
                    return parameter.AsValueString() ?? parameter.AsInteger().ToString();

                case StorageType.Double:
                    return parameter.AsValueString() ?? parameter.AsDouble().ToString("0.###");

                case StorageType.ElementId:
                    return parameter.AsValueString()
                           ?? parameter.AsElementId().Value.ToString();

                default:
                    return string.Empty;
            }
        }

        // Friendly label: the Revit parameter name when known.
        public static string DisplayName(string configuredName, Parameter? found)
        {
            if (found?.Definition != null)
                return found.Definition.Name;

            if (configuredName.StartsWith(BuiltInPrefix, StringComparison.OrdinalIgnoreCase))
                return configuredName.Substring(BuiltInPrefix.Length).Trim();

            return configuredName;
        }
    }

    public static class ParameterRuleEvaluator
    {
        // Returns a list of human-readable problems; empty when all good.
        public static List<string> Evaluate(
            CheckContext context,
            Element element,
            IEnumerable<ParameterRequirement> requirements)
        {
            var problems = new List<string>();

            foreach (ParameterRequirement requirement in requirements)
            {
                if (string.IsNullOrWhiteSpace(requirement.Name))
                    continue;

                if (!ParameterHelper.IsBuiltInNameValid(requirement.Name))
                {
                    context.ReportConfigError(
                        $"Unknown built-in parameter '{requirement.Name}'.");
                    continue;
                }

                Parameter? parameter = ParameterHelper.Find(element, context.Doc, requirement.Name);
                string label = ParameterHelper.DisplayName(requirement.Name, parameter);

                if (parameter == null)
                {
                    problems.Add($"{label}: parameter not present");
                    continue;
                }

                if (!ParameterHelper.HasValue(parameter))
                {
                    problems.Add($"{label}: empty");
                    continue;
                }

                string value = ParameterHelper.GetValueText(parameter);

                if (!string.IsNullOrWhiteSpace(requirement.Pattern) &&
                    !context.Patterns.IsMatch(requirement.Pattern, value))
                {
                    problems.Add($"{label}: '{value}' is not in the required format");
                }

                if (requirement.AllowedValues != null &&
                    requirement.AllowedValues.Count > 0 &&
                    !requirement.AllowedValues.Any(v =>
                        string.Equals(v?.Trim(), value.Trim(), StringComparison.OrdinalIgnoreCase)))
                {
                    problems.Add($"{label}: '{value}' is not an allowed value");
                }
            }

            return problems;
        }
    }
}
