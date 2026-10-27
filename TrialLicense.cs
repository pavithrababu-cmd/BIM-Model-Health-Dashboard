using Autodesk.Revit.UI;
using Microsoft.Win32;
using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace BIMModelHealthDashboard
{
    // 7-day trial for the BIM Model Health Dashboard (one file, no other setup).
    //
    // How it works:
    //  - The trial starts the first time the dashboard is opened on this PC/user.
    //  - The start date is saved in TWO places (registry + hidden file) and signed,
    //    so editing or deleting one of them doesn't reset the trial.
    //  - Setting the PC clock back is detected.
    //
    // Usage:
    //  1. App.OnStartup – shows "7-day trial activated" the first time Revit
    //     starts after the plugin is installed:
    //         application.Idling += ...  ->  TrialLicense.NotifyIfNewTrial();
    //  2. BimModelHealthCommand.Execute, first line:
    //         if (!TrialLicense.CheckAndNotify())
    //             return Result.Cancelled;
    public static class TrialLicense
    {
        // Length of the trial. Set to 0 to test the "expired" message.
        public const int TrialDays = 7;

        private const string RegistryPath = @"Software\BIMModelHealthDashboard";
        private const string RegistryValue = "S";

        // Protects the saved dates against hand editing.
        private static readonly byte[] Secret =
            Encoding.UTF8.GetBytes("BMHD|trial|c3a9f1e07b|" + Environment.MachineName + "|" + Environment.UserName);

        private static string TrialFile =>
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "BIMModelHealthDashboard",
                "session.dat");

        // ------------------------------------------------------------------
        // Public API
        // ------------------------------------------------------------------

        // True while the trial is running.
        public static bool IsActive => DaysLeft > 0;

        // Days left in the trial (0 = expired or tampered).
        public static int DaysLeft
        {
            get
            {
                TrialState state = Evaluate(out _, out int daysLeft, out _);
                return state == TrialState.Active ? daysLeft : 0;
            }
        }

        // Date the trial ends (null if it hasn't started yet).
        public static DateTime? EndsOn
        {
            get
            {
                Evaluate(out DateTime? endsOn, out _, out _);
                return endsOn;
            }
        }

        // Starts the trial the first time it's called on this PC/user and shows
        // "Your 7-day trial has been activated". Does nothing afterwards.
        // Call it once when Revit starts (see App.cs).
        public static void NotifyIfNewTrial()
        {
            TrialState state = Evaluate(out DateTime? endsOn, out _, out bool justStarted);

            if (justStarted && state == TrialState.Active)
                ShowActivatedMessage(endsOn);
        }

        // Call before opening the dashboard. Starts the trial on first use,
        // shows a message when the trial has ended, and returns whether the
        // dashboard may open.
        public static bool CheckAndNotify()
        {
            TrialState state = Evaluate(out DateTime? endsOn, out int daysLeft, out bool justStarted);

            switch (state)
            {
                case TrialState.Active:
                    if (justStarted)
                        ShowActivatedMessage(endsOn);
                    else if (daysLeft <= 2)
                    {
                        TaskDialog.Show(
                            "BIM Model Health – Trial",
                            $"Your trial ends in {daysLeft} day{(daysLeft == 1 ? "" : "s")} " +
                            $"({endsOn:dd MMM yyyy}).\n\nContact your BIM manager for the full version.");
                    }

                    return true;

                case TrialState.Expired:
                    TaskDialog.Show(
                        "BIM Model Health – Trial ended",
                        $"Your {TrialDays}-day trial ended on {endsOn:dd MMM yyyy}.\n\n" +
                        "Contact your BIM manager for the full version.");
                    return false;

                default: // Tampered
                    TaskDialog.Show(
                        "BIM Model Health – Trial",
                        "The trial information on this computer is not valid " +
                        "(it was changed, or the computer's date was set back).\n\n" +
                        "Correct the date or contact your BIM manager.");
                    return false;
            }
        }

        private static void ShowActivatedMessage(DateTime? endsOn)
        {
            TaskDialog.Show(
                "BIM Model Health – Trial activated",
                $"Your {TrialDays}-day trial of the BIM Model Health Dashboard has been activated.\n\n" +
                $"You can use all features until {endsOn:dd MMM yyyy}.\n\n" +
                "Open it from Add-Ins → BIM Model Health → Model Health Dashboard.");
        }

        // Short text for the dashboard header, e.g. "Trial: 5 days left".
        public static string StatusText
        {
            get
            {
                int days = DaysLeft;
                return days > 0
                    ? $"Trial: {days} day{(days == 1 ? "" : "s")} left"
                    : "Trial ended";
            }
        }

        // ------------------------------------------------------------------
        // Trial logic
        // ------------------------------------------------------------------

        private enum TrialState
        {
            Active,
            Expired,
            Tampered
        }

        private static TrialState Evaluate(out DateTime? endsOn, out int daysLeft, out bool justStarted)
        {
            endsOn = null;
            daysLeft = 0;
            justStarted = false;

            DateTime now = DateTime.Now;

            (DateTime Start, DateTime LastSeen)? saved = Load(out bool tampered);

            if (tampered)
                return TrialState.Tampered;

            justStarted = saved == null;   // no trial saved yet: it starts now

            DateTime start = saved?.Start ?? now;          // first use: trial starts now
            DateTime lastSeen = saved?.LastSeen ?? now;

            // Clock set back by more than a day.
            if (now < lastSeen.AddDays(-1))
                return TrialState.Tampered;

            Save(start, now > lastSeen ? now : lastSeen);

            DateTime end = start.AddDays(TrialDays);
            endsOn = end;

            if (now >= end)
                return TrialState.Expired;

            daysLeft = Math.Max(1, (int)Math.Ceiling((end - now).TotalDays));
            return TrialState.Active;
        }

        // Reads both copies; the earliest start and latest "last seen" win.
        private static (DateTime Start, DateTime LastSeen)? Load(out bool tampered)
        {
            tampered = false;

            var records = new[] { Parse(ReadRegistry(), ref tampered), Parse(ReadFile(), ref tampered) }
                .Where(r => r.HasValue)
                .Select(r => r!.Value)
                .ToList();

            if (records.Count == 0)
                return null;

            return (records.Min(r => r.Start), records.Max(r => r.LastSeen));
        }

        // Format: "<start ticks>|<last seen ticks>|<signature>"
        private static (DateTime Start, DateTime LastSeen)? Parse(string? text, ref bool tampered)
        {
            if (string.IsNullOrWhiteSpace(text))
                return null;

            string[] parts = text!.Trim().Split('|');

            if (parts.Length != 3 ||
                !long.TryParse(parts[0], out long start) ||
                !long.TryParse(parts[1], out long lastSeen) ||
                parts[2] != Sign(parts[0] + "|" + parts[1]))
            {
                tampered = true;
                return null;
            }

            return (new DateTime(start), new DateTime(lastSeen));
        }

        private static void Save(DateTime start, DateTime lastSeen)
        {
            string data = $"{start.Ticks}|{lastSeen.Ticks}";
            string text = data + "|" + Sign(data);

            try
            {
                using RegistryKey key = Registry.CurrentUser.CreateSubKey(RegistryPath);
                key.SetValue(RegistryValue, text);
            }
            catch (Exception)
            {
            }

            try
            {
                string? folder = Path.GetDirectoryName(TrialFile);

                if (!string.IsNullOrEmpty(folder))
                    Directory.CreateDirectory(folder);

                if (File.Exists(TrialFile))
                    File.SetAttributes(TrialFile, FileAttributes.Normal);

                File.WriteAllText(TrialFile, text);
                File.SetAttributes(TrialFile, FileAttributes.Hidden);
            }
            catch (Exception)
            {
            }
        }

        // ------------------------------------------------------------------
        // Helpers
        // ------------------------------------------------------------------

        private static string Sign(string data)
        {
            using var hmac = new HMACSHA256(Secret);
            return Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(data)));
        }

        private static string? ReadRegistry()
        {
            try
            {
                using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RegistryPath);
                return key?.GetValue(RegistryValue) as string;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static string? ReadFile()
        {
            try
            {
                return File.Exists(TrialFile) ? File.ReadAllText(TrialFile) : null;
            }
            catch (Exception)
            {
                return null;
            }
        }
    }
}