using System;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;

namespace Forge.SolidWorks
{
    /// <summary>
    /// Trial clock + identity. On first run we stamp a first-run time and derive a stable, hashed
    /// machine id (never reversible to a person). Expiry = first run + 14 days, computed live.
    /// The server-side trial_events row (keyed by the same hashed id) is the durable record,
    /// so a casual reinstall can't silently extend the trial.
    /// </summary>
    internal static class TrialInit
    {
        private const int TrialDays = 14;
        private const string RegPath = @"Software\Forge\Trial";
        public const string ContactLine = "Trial ended — contact Ravi 628-363-1774";

        public static string TrialId { get; private set; }
        public static DateTime FirstRunUtc { get; private set; }
        public static bool Ready { get; private set; }

        public static void Init()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.CreateSubKey(RegPath))
                {
                    string first = key.GetValue("FirstRunUtc") as string;
                    DateTime dt;
                    if (string.IsNullOrEmpty(first) ||
                        !DateTime.TryParse(first, CultureInfo.InvariantCulture,
                            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out dt))
                    {
                        FirstRunUtc = DateTime.UtcNow;
                        key.SetValue("FirstRunUtc", FirstRunUtc.ToString("o", CultureInfo.InvariantCulture));
                    }
                    else FirstRunUtc = dt;

                    TrialId = key.GetValue("TrialId") as string;
                    if (string.IsNullOrEmpty(TrialId)) { TrialId = MachineId(); key.SetValue("TrialId", TrialId); }
                }

                SafePaths.EnsureUsable();   // verify the sandbox is writable before we let anything run
                Ready = true;
            }
            catch { Ready = false; }        // if we can't even set up safely, we run nothing (handlers gate on Ready)
        }

        public static bool IsExpired()
        {
            // TRIAL GATE DISABLED for dev/demo/recording builds (2026-07-24). This was a test feature that expired
            // 14 days after first run and blocked the panel with "Trial ended — contact Ravi". For a real customer
            // trial build, restore:  return DateTime.UtcNow > FirstRunUtc.AddDays(TrialDays);
            return false;
        }

        public static int DaysLeft()
        {
            double d = Math.Ceiling((FirstRunUtc.AddDays(TrialDays) - DateTime.UtcNow).TotalDays);
            return d < 0 ? 0 : (int)d;
        }

        private static string MachineId()
        {
            string raw;
            try
            {
                using (RegistryKey k = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64)
                    .OpenSubKey(@"SOFTWARE\Microsoft\Cryptography"))
                {
                    raw = (k != null ? k.GetValue("MachineGuid") as string : null) ?? Environment.MachineName;
                }
            }
            catch { raw = Environment.MachineName; }

            using (SHA256 sha = SHA256.Create())
            {
                byte[] h = sha.ComputeHash(Encoding.UTF8.GetBytes("forge-trial:" + raw));
                return BitConverter.ToString(h).Replace("-", "").Substring(0, 32).ToLowerInvariant();
            }
        }
    }
}