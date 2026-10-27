using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;
using BIMModelHealthDashboard.Views;
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace BIMModelHealthDashboard
{
    public class App : IExternalApplication
    {
        // Set to false once the icon shows correctly.
        // While true, a dialog explains what is wrong if the icon can't be found.
        private const bool ShowIconDiagnostics = false;

        private const string LargeIconFile = "HealthDashboard32.png";   // 32 x 32, ribbon
        private const string SmallIconFile = "HealthDashboard16.png";   // 16 x 16, Quick Access Toolbar

        // The single open dashboard window (null when closed).
        internal static MainWindow? DashboardWindow { get; set; }

        // Kept so the one-time trial message can unsubscribe from Idling.
        private static UIControlledApplication? _application;

        public Result OnStartup(UIControlledApplication application)
        {
            try
            {
                RibbonPanel panel = application.CreateRibbonPanel("BIM Model Health");

                string assemblyPath = Assembly.GetExecutingAssembly().Location;

                var buttonData = new PushButtonData(
                    "BIMModelHealthDashboardButton",
                    "Model Health\nDashboard",
                    assemblyPath,
                    "BIMModelHealthDashboard.BimModelHealthCommand");

                if (panel.AddItem(buttonData) is PushButton button)
                {
                    button.ToolTip = "Open the BIM Model Health Dashboard.";
                    button.LongDescription =
                        "Scan the active model for missing parameters, wrong family types, duplicates, " +
                        "unconnected walls, room, view and level problems, workset, material, naming, " +
                        "COBie and coordinate issues, and Revit warnings.";

                    button.LargeImage = LoadIcon(LargeIconFile);
                    button.Image = LoadIcon(SmallIconFile);

                    if (ShowIconDiagnostics && button.LargeImage == null)
                        ShowIconHelp();
                }

                // First Revit start after installation: show "7-day trial activated"
                // once Revit has finished loading.
                _application = application;
                application.Idling += OnFirstIdle;

                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                TaskDialog.Show(
                    "BIM Model Health",
                    "Failed to create the dashboard ribbon button.\n\n" + ex);

                return Result.Failed;
            }
        }

        // Runs once, when Revit is ready; then unsubscribes itself.
        private static void OnFirstIdle(object? sender, IdlingEventArgs e)
        {
            if (_application != null)
                _application.Idling -= OnFirstIdle;

            TrialLicense.NotifyIfNewTrial();
        }

        public Result OnShutdown(UIControlledApplication application)
        {
            DashboardWindow?.Close();
            return Result.Succeeded;
        }

        // ------------------------------------------------------------------
        // Icons
        // ------------------------------------------------------------------

        // Loads an icon. Works with any of these set-ups for the PNG file:
        //   1. Build Action = Embedded Resource            (recommended)
        //   2. Build Action = Resource                     (WPF resource)
        //   3. Copy to Output Directory = Copy if newer    (file next to the DLL)
        // Returns null if the icon isn't found, so it never stops the add-in.
        internal static ImageSource? LoadIcon(string fileName)
        {
            return FromEmbeddedResource(fileName)
                   ?? FromWpfResource(fileName)
                   ?? FromFile(fileName);
        }

        private static ImageSource? FromEmbeddedResource(string fileName)
        {
            try
            {
                Assembly assembly = Assembly.GetExecutingAssembly();

                string? resourceName = assembly.GetManifestResourceNames()
                    .FirstOrDefault(n => n.EndsWith(fileName, StringComparison.OrdinalIgnoreCase));

                if (resourceName == null)
                    return null;

                using Stream? stream = assembly.GetManifestResourceStream(resourceName);
                return stream == null ? null : FromStream(stream);
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static ImageSource? FromWpfResource(string fileName)
        {
            try
            {
                // Makes sure the "pack://" URI scheme is registered.
                _ = System.IO.Packaging.PackUriHelper.UriSchemePack;

                string assemblyName = Assembly.GetExecutingAssembly().GetName().Name ?? "BIMModelHealthDashboard";

                foreach (string folder in new[] { "Resources/Icons/", "Resources/", "" })
                {
                    var uri = new Uri($"pack://application:,,,/{assemblyName};component/{folder}{fileName}", UriKind.Absolute);

                    try
                    {
                        var image = new BitmapImage();
                        image.BeginInit();
                        image.UriSource = uri;
                        image.CacheOption = BitmapCacheOption.OnLoad;
                        image.EndInit();
                        image.Freeze();
                        return image;
                    }
                    catch (Exception)
                    {
                        // Not in this folder; try the next one.
                    }
                }
            }
            catch (Exception)
            {
            }

            return null;
        }

        private static ImageSource? FromFile(string fileName)
        {
            try
            {
                string? folder = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);

                if (string.IsNullOrEmpty(folder))
                    return null;

                foreach (string path in new[]
                         {
                             Path.Combine(folder, "Resources", "Icons", fileName),
                             Path.Combine(folder, "Resources", fileName),
                             Path.Combine(folder, fileName)
                         })
                {
                    if (!File.Exists(path))
                        continue;

                    using FileStream stream = File.OpenRead(path);
                    return FromStream(stream);
                }
            }
            catch (Exception)
            {
            }

            return null;
        }

        private static ImageSource FromStream(Stream stream)
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.StreamSource = stream;
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.EndInit();
            image.Freeze();
            return image;
        }

        // Explains why the icon is missing (only while ShowIconDiagnostics = true).
        private static void ShowIconHelp()
        {
            Assembly assembly = Assembly.GetExecutingAssembly();
            string[] resources = assembly.GetManifestResourceNames();
            string folder = Path.GetDirectoryName(assembly.Location) ?? "(unknown)";

            TaskDialog.Show(
                "BIM Model Health – icon not found",
                $"The ribbon icon '{LargeIconFile}' could not be found.\n\n" +
                "Fix:\n" +
                "1. In Visual Studio, select HealthDashboard32.png and HealthDashboard16.png.\n" +
                "2. Properties ? Build Action = Embedded Resource.\n" +
                "3. Check the file names are exactly HealthDashboard32.png / HealthDashboard16.png.\n" +
                "4. Close Revit, Rebuild Solution, open Revit.\n\n" +
                "Embedded resources found in the DLL:\n" +
                (resources.Length == 0 ? "  (none)" : "  " + string.Join("\n  ", resources)) +
                $"\n\nDLL folder:\n  {folder}\n\n" +
                "(Set ShowIconDiagnostics = false in App.cs to hide this message.)");
        }
    }
}