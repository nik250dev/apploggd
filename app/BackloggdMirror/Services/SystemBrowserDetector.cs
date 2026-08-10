using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace BackloggdMirror.Services
{
    /// <summary>
    /// A Chromium-based browser found on the machine.
    ///
    /// <c>Channel</c> is the Playwright channel ("chrome" / "msedge") where one exists, and null for
    /// binaries with no equivalent channel (distro Chromium, snap...), which can only be launched by
    /// path. Careful: "chromium" is NOT a valid channel — to Playwright that name means "the Chromium
    /// I download", not the system one.
    /// </summary>
    public sealed record SystemBrowserCandidate(string? Channel, string? ExecutablePath, string DisplayName)
    {
        public override string ToString() => $"{DisplayName} (channel={Channel ?? "-"}, path={ExecutablePath ?? "-"})";
    }

    public interface ISystemBrowserDetector
    {
        /// <summary>
        /// Browsers found on disk, in order of preference (Chrome, Edge, Chromium). A candidate
        /// existing is no guarantee it launches: it still has to be probed.
        /// </summary>
        IReadOnlyList<SystemBrowserCandidate> FindCandidates();
    }

    /// <summary>
    /// Looks for a Chrome, Edge or Chromium already installed on the machine, so Playwright can
    /// drive it and the ~400 MB download of the bundled Chromium can be avoided.
    /// </summary>
    public sealed class SystemBrowserDetector : ISystemBrowserDetector
    {
        private readonly IAppLogger _logger;

        public SystemBrowserDetector(IAppLogger logger)
        {
            _logger = logger;
        }

        public IReadOnlyList<SystemBrowserCandidate> FindCandidates()
        {
            var found = new List<SystemBrowserCandidate>();

            try
            {
                if (OperatingSystem.IsWindows()) CollectWindows(found);
                else if (OperatingSystem.IsMacOS()) CollectMacOs(found);
                else CollectLinux(found);
            }
            catch (Exception ex)
            {
                // Detection must never bring down startup: with no candidates the user gets asked.
                _logger.Warning($"[SystemBrowserDetector] Detection failed: {ex.Message}");
            }

            // Deduplicate by path, preserving the preference order (Chrome before Edge).
            var deduped = found
                .Where(c => c.ExecutablePath != null)
                .GroupBy(c => c.ExecutablePath!.ToLowerInvariant())
                .Select(g => g.First())
                .ToList();

            _logger.Info(deduped.Count == 0
                ? "[SystemBrowserDetector] No system Chrome/Edge/Chromium found."
                : $"[SystemBrowserDetector] Candidates: {string.Join(" | ", deduped)}");

            return deduped;
        }

        // ---------------- Windows ----------------

        [SupportedOSPlatform("windows")]
        private void CollectWindows(List<SystemBrowserCandidate> found)
        {
            AddWindows(found, "chrome", "Google Chrome", "chrome.exe", @"Google\Chrome\Application\chrome.exe");
            AddWindows(found, "msedge", "Microsoft Edge", "msedge.exe", @"Microsoft\Edge\Application\msedge.exe");
        }

        [SupportedOSPlatform("windows")]
        private void AddWindows(List<SystemBrowserCandidate> found, string channel, string name,
                                string exeName, string relativePath)
        {
            // 1) App Paths is the source Windows itself treats as authoritative for "where is chrome.exe".
            foreach (var path in ReadAppPaths(exeName))
            {
                var expanded = Environment.ExpandEnvironmentVariables(path);
                if (File.Exists(expanded)) found.Add(new SystemBrowserCandidate(channel, expanded, name));
            }

            // 2) Known install roots, for when App Paths is missing or stale.
            //    ProgramW6432 avoids WOW64 redirection should this ever be built as x86.
            var roots = new[]
            {
                Environment.GetEnvironmentVariable("ProgramW6432"),
                Environment.GetEnvironmentVariable("ProgramFiles"),
                Environment.GetEnvironmentVariable("ProgramFiles(x86)"),
                Environment.GetEnvironmentVariable("LOCALAPPDATA")   // per-user Chrome install
            };

            foreach (var root in roots)
            {
                if (string.IsNullOrWhiteSpace(root)) continue;

                var full = Path.Combine(root, relativePath);
                if (File.Exists(full)) found.Add(new SystemBrowserCandidate(channel, full, name));
            }
        }

        [SupportedOSPlatform("windows")]
        private IEnumerable<string> ReadAppPaths(string exeName)
        {
            const string subKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\";

            foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
            {
                foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
                {
                    string? value = null;
                    try
                    {
                        using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                        using var key = baseKey.OpenSubKey(subKey + exeName);
                        value = key?.GetValue(null) as string;   // the (Default) value is the full path
                    }
                    catch (Exception ex)
                    {
                        _logger.Warning($"[SystemBrowserDetector] Registry read failed ({hive}/{view}/{exeName}): {ex.Message}");
                    }

                    if (!string.IsNullOrWhiteSpace(value)) yield return value.Trim().Trim('"');
                }
            }
        }

        // ---------------- Linux ----------------

        private static void CollectLinux(List<SystemBrowserCandidate> found)
        {
            AddUnix(found, "chrome", "Google Chrome",
                new[] { "/opt/google/chrome/chrome", "/opt/google/chrome/google-chrome",
                        "/usr/bin/google-chrome", "/usr/bin/google-chrome-stable",
                        "/usr/local/bin/google-chrome" },
                new[] { "google-chrome", "google-chrome-stable" });

            AddUnix(found, "msedge", "Microsoft Edge",
                new[] { "/opt/microsoft/msedge/msedge", "/opt/microsoft/msedge/microsoft-edge",
                        "/usr/bin/microsoft-edge", "/usr/bin/microsoft-edge-stable" },
                new[] { "microsoft-edge", "microsoft-edge-stable" });

            // No channel: can only be launched via ExecutablePath.
            AddUnix(found, null, "Chromium",
                new[] { "/usr/bin/chromium", "/usr/bin/chromium-browser", "/snap/bin/chromium" },
                new[] { "chromium", "chromium-browser" });
        }

        private static void AddUnix(List<SystemBrowserCandidate> found, string? channel, string name,
                                    string[] absolutePaths, string[] pathNames)
        {
            foreach (var path in absolutePaths)
            {
                if (File.Exists(path)) found.Add(new SystemBrowserCandidate(channel, path, name));
            }

            foreach (var executable in pathNames)
            {
                var resolved = FindOnPath(executable);
                if (resolved != null) found.Add(new SystemBrowserCandidate(channel, resolved, name));
            }
        }

        private static string? FindOnPath(string executableName)
        {
            var pathVar = Environment.GetEnvironmentVariable("PATH");
            if (string.IsNullOrEmpty(pathVar)) return null;

            foreach (var dir in pathVar.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                string candidate;
                try { candidate = Path.Combine(dir.Trim(), executableName); }
                catch (ArgumentException) { continue; }   // malformed PATH entry

                if (File.Exists(candidate)) return candidate;
            }

            return null;
        }

        // ---------------- macOS ----------------

        private static void CollectMacOs(List<SystemBrowserCandidate> found)
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var roots = new[] { "/Applications", Path.Combine(home, "Applications") };

            void AddMac(string? channel, string name, string relative)
            {
                foreach (var root in roots)
                {
                    var full = Path.Combine(root, relative);
                    if (File.Exists(full)) found.Add(new SystemBrowserCandidate(channel, full, name));
                }
            }

            AddMac("chrome", "Google Chrome", "Google Chrome.app/Contents/MacOS/Google Chrome");
            AddMac("msedge", "Microsoft Edge", "Microsoft Edge.app/Contents/MacOS/Microsoft Edge");
            AddMac(null, "Chromium", "Chromium.app/Contents/MacOS/Chromium");
        }
    }
}
