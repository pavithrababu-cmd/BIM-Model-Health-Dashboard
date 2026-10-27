using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using BIMModelHealthDashboard.Config;
using BIMModelHealthDashboard.Models;
using System;
using System.Collections.Generic;
using System.Linq;

namespace BIMModelHealthDashboard.Services
{
    public enum DashboardRequestKind
    {
        RefreshMetrics,
        RunChecks,
        SelectElements,
        FixIssues
    }

    public sealed class DashboardRequest
    {
        public DashboardRequestKind Kind { get; set; }

        // RunChecks: groups to run (null = all checks).
        public IReadOnlyCollection<string>? Groups { get; set; }

        // SelectElements: ids to select, and the model they came from.
        public IReadOnlyCollection<long> ElementIds { get; set; } = Array.Empty<long>();
        public string? ExpectedModelKey { get; set; }

        // FixIssues: ids from FixCatalog to apply.
        public IReadOnlyCollection<string> FixIds { get; set; } = Array.Empty<string>();
    }

    // Runs dashboard requests on Revit's API thread via ExternalEvent.
    public class DashboardRequestHandler : IExternalEventHandler
    {
        private readonly object _sync = new object();
        private readonly Queue<DashboardRequest> _queue = new Queue<DashboardRequest>();
        private readonly ModelDashboardService _metricsService = new ModelDashboardService();

        public Action<DashboardSummary>? MetricsCompleted { get; set; }
        public Action<ValidationReport>? ChecksCompleted { get; set; }
        public Action<string>? Failed { get; set; }
        public Action<string>? Info { get; set; }
        public Action<List<FixResult>>? FixesCompleted { get; set; }

        public void Enqueue(DashboardRequest request)
        {
            lock (_sync)
            {
                _queue.Enqueue(request);
            }
        }

        public void Execute(UIApplication application)
        {
            while (true)
            {
                DashboardRequest request;

                lock (_sync)
                {
                    if (_queue.Count == 0)
                        return;

                    request = _queue.Dequeue();
                }

                try
                {
                    UIDocument? uiDocument = application.ActiveUIDocument;
                    Document? document = uiDocument?.Document;

                    if (uiDocument == null || document == null)
                    {
                        Failed?.Invoke("Please open a Revit project first.");
                        continue;
                    }

                    if (document.IsFamilyDocument)
                    {
                        Failed?.Invoke("The dashboard works on project files. The active document is a family.");
                        continue;
                    }

                    switch (request.Kind)
                    {
                        case DashboardRequestKind.RefreshMetrics:
                            MetricsCompleted?.Invoke(_metricsService.GetSummary(document));
                            break;

                        case DashboardRequestKind.RunChecks:
                            RunChecks(document, request);
                            break;

                        case DashboardRequestKind.SelectElements:
                            SelectElements(uiDocument, document, request);
                            break;

                        case DashboardRequestKind.FixIssues:
                            FixIssues(uiDocument, document, request);
                            break;
                    }
                }
                catch (Exception ex)
                {
                    Failed?.Invoke(ex.Message);
                }
            }
        }

        private void RunChecks(Document document, DashboardRequest request)
        {
            HealthRules rules = RulesRepository.Load(out string source, out string? rulesError);

            List<CheckResult> results = ValidationService.Run(document, rules, request.Groups);

            var report = new ValidationReport
            {
                ModelKey = ValidationService.ModelKey(document),
                ModelTitle = document.Title,
                ModelPath = string.IsNullOrWhiteSpace(document.PathName)
                    ? "Model has not been saved"
                    : document.PathName,
                ScannedAt = DateTime.Now,
                RulesSource = source,
                RulesWarning = rulesError
            };

            report.Merge(results);

            // Metrics too, so the warning count etc. match the scan.
            MetricsCompleted?.Invoke(_metricsService.GetSummary(document));
            ChecksCompleted?.Invoke(report);
        }

        // Applies the chosen fixes, then re-runs all 16 checks so the
        // dashboard shows the new issue counts and health score.
        private void FixIssues(UIDocument uiDocument, Document document, DashboardRequest request)
        {
            if (request.ExpectedModelKey != null &&
                request.ExpectedModelKey != ValidationService.ModelKey(document))
            {
                Failed?.Invoke(
                    "The scan results belong to a different model. Run Full Scan on the active model first.");
                return;
            }

            if (document.IsReadOnly)
            {
                Failed?.Invoke("The active model is read-only, so it cannot be fixed.");
                return;
            }

            HealthRules rules = RulesRepository.Load(out string _, out string? _);
            List<FixResult> fixes = AutoFixService.Run(uiDocument, rules, request.FixIds);

            RunChecks(document, new DashboardRequest { Kind = DashboardRequestKind.RunChecks });

            FixesCompleted?.Invoke(fixes);
        }

        private void SelectElements(UIDocument uiDocument, Document document, DashboardRequest request)
        {
            if (request.ExpectedModelKey != null &&
                request.ExpectedModelKey != ValidationService.ModelKey(document))
            {
                Failed?.Invoke(
                    "These results belong to a different model. Switch back to that model, " +
                    "or re-run the scan on the active model.");
                return;
            }

            List<Element> elements = request.ElementIds
                .Distinct()
                .Select(id => document.GetElement(new ElementId(id)))
                .Where(e => e != null)
                .ToList();

            if (elements.Count == 0)
            {
                Info?.Invoke("The element(s) no longer exist in the model. Re-run the scan.");
                return;
            }

            // A single view: open it.
            if (elements.Count == 1 && elements[0] is View view && !view.IsTemplate)
            {
                try
                {
                    uiDocument.ActiveView = view;
                    Info?.Invoke($"Opened view '{view.Name}'.");
                }
                catch (Autodesk.Revit.Exceptions.ApplicationException)
                {
                    Info?.Invoke($"View '{view.Name}' cannot be opened from here.");
                }

                return;
            }

            List<ElementId> selectable = elements
                .Where(e => !(e is ElementType) &&
                            !(e is View) &&
                            !(e is Family) &&
                            !(e is Material) &&
                            e.Category != null)
                .Select(e => e.Id)
                .ToList();

            if (selectable.Count == 0)
            {
                Info?.Invoke("These items (types, families, materials or views) cannot be selected in a model view.");
                return;
            }

            uiDocument.Selection.SetElementIds(selectable);

            try
            {
                uiDocument.ShowElements(selectable);
                Info?.Invoke($"Selected and zoomed to {selectable.Count:N0} element(s) in Revit.");
            }
            catch (Autodesk.Revit.Exceptions.ApplicationException)
            {
                Info?.Invoke($"Selected {selectable.Count:N0} element(s). They are not visible in an open view, so Revit could not zoom to them.");
            }
        }

        public string GetName() => "BIM Model Health Dashboard";
    }
}
