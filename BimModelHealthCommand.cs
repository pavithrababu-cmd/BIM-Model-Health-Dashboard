using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using BIMModelHealthDashboard.Services;
using BIMModelHealthDashboard.Views;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace BIMModelHealthDashboard
{
    [Transaction(TransactionMode.ReadOnly)]
    [Guid("958DCB1C-8AA5-4449-A430-11B6170B720A")]
    public class BimModelHealthCommand : IExternalCommand
    {
        public Result Execute(
            ExternalCommandData commandData,
            ref string message,
            ElementSet elements)
        {
            // Re-use the open dashboard instead of opening a second one.
            MainWindow? existing = App.DashboardWindow;

            if (existing != null)
            {
                if (existing.WindowState == WindowState.Minimized)
                    existing.WindowState = WindowState.Normal;

                existing.Activate();
                existing.RequestRefresh();
                return Result.Succeeded;
            }

            var handler = new DashboardRequestHandler();
            ExternalEvent externalEvent = ExternalEvent.Create(handler);

            var window = new MainWindow(handler, externalEvent);

            // Keep the dashboard above Revit.
            new WindowInteropHelper(window)
            {
                Owner = commandData.Application.MainWindowHandle
            };

            window.Closed += (sender, args) =>
            {
                App.DashboardWindow = null;
                externalEvent.Dispose();
            };

            App.DashboardWindow = window;
            window.Show();

            return Result.Succeeded;
        }
    }
}
