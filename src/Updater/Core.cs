using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;
using Microsoft.Win32;

namespace ReceiverMultiplayerUpdater
{
    /// <summary>A published release: its version and the drag-and-drop zip (with its SHA-256 when GitHub gives one).</summary>
    sealed class Release
    {
        public Version Version;
        public string Tag, ZipUrl, ZipName, Sha256;
        public long Size;
    }

    /// <summary>Finding the game, reading what's installed, fetching releases, installing them.</summary>
    static class Core
    {
        public const string Repo = "staltmedia/Receiver-Multiplayer";
        public const string SteamAppId = "234190";
        public const string ModFolder = @"BepInEx\plugins\ReceiverMultiplayer";
        const string LatestApi = "https://api.github.com/repos/" + Repo + "/releases/latest";
        const string ConfigFile = @"BepInEx\config\receiver.multiplayer.cfg";
        const long MaxZipBytes = 64L * 1024 * 1024;

        // ---------------- the game ----------------

        public static bool IsGameDir(string dir) =>
            !string.IsNullOrEmpty(dir) && File.Exists(Path.Combine(dir, "Receiver.exe")) && Directory.Exists(Path.Combine(dir, "Receiver_Data"));

        /// <summary>Where Receiver is: beside this updater (it lives in the mod's folder), else in a Steam library.</summary>
        public static string FindGame()
        {
            string here = AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\');
            for (var d = new DirectoryInfo(here); d != null; d = d.Parent)
                if (IsGameDir(d.FullName)) return d.FullName;
            foreach (var lib in SteamLibraries())
            {
                string manifest = Path.Combine(lib, "steamapps", "appmanifest_" + SteamAppId + ".acf");
                string dir = null;
                try
                {
                    if (File.Exists(manifest))
                    {
                        var m = Regex.Match(File.ReadAllText(manifest), "\"installdir\"\\s*\"([^\"]+)\"");
                        if (m.Success) dir = Path.Combine(lib, "steamapps", "common", m.Groups[1].Value);
                    }
                }
                catch { /* unreadable manifest: try the usual folder */ }
                if (dir == null) dir = Path.Combine(lib, "steamapps", "common", "Receiver");
                if (IsGameDir(dir)) return dir;
            }
            return null;
        }

        static IEnumerable<string> SteamLibraries()
        {
            var roots = new List<string>();
            foreach (var (hive, key, value) in new[]
            {
                (Registry.CurrentUser, @"Software\Valve\Steam", "SteamPath"),
                (Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath"),
                (Registry.LocalMachine, @"SOFTWARE\Valve\Steam", "InstallPath"),
            })
            {
                try
                {
                    using (var k = hive.OpenSubKey(key))
                        if (k?.GetValue(value) is string p && Directory.Exists(p)) roots.Add(Path.GetFullPath(p.Replace('/', '\\')));
                }
                catch { /* no such key */ }
            }
            var libs = new List<string>();
            foreach (var root in roots.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                libs.Add(root);
                try
                {
                    string vdf = Path.Combine(root, "steamapps", "libraryfolders.vdf");
                    if (!File.Exists(vdf)) continue;
                    foreach (Match m in Regex.Matches(File.ReadAllText(vdf), "\"path\"\\s*\"([^\"]+)\""))
                        libs.Add(m.Groups[1].Value.Replace(@"\\", @"\"));
                }
                catch { /* unreadable: the root library still counts */ }
            }
            return libs.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase);
        }

        public static bool IsSteamInstall(string game) =>
            game.IndexOf(@"\steamapps\common\", StringComparison.OrdinalIgnoreCase) >= 0;

        /// <summary>The mod version installed in this game folder, or null.</summary>
        public static Version Installed(string game)
        {
            try
            {
                string dll = Path.Combine(game, ModFolder, "ReceiverMultiplayer.dll");
                return File.Exists(dll) ? DllVersion(dll) : null;
            }
            catch { return null; }
        }

        static Version DllVersion(string dll)
        {
            var info = FileVersionInfo.GetVersionInfo(dll);
            return Parse(info.ProductVersion?.Split('+')[0]) ?? Parse(info.FileVersion);
        }

        /// <summary>"0.7.0", "v0.7.0" or "0.7.0.0" all read as 0.7.0 (so they compare equal).</summary>
        public static Version Parse(string s)
        {
            if (s == null || !Version.TryParse(s.Trim().TrimStart('v', 'V'), out var v)) return null;
            return new Version(v.Major, v.Minor, Math.Max(v.Build, 0));
        }

        /// <summary>The mod version inside a release zip, or null if it isn't one.</summary>
        public static Version ZipVersion(byte[] zip)
        {
            string temp = Path.Combine(Path.GetTempPath(), "rmp-" + Guid.NewGuid().ToString("N") + ".dll");
            try
            {
                using (var archive = new ZipArchive(new MemoryStream(zip, false), ZipArchiveMode.Read))
                {
                    var entry = archive.Entries.FirstOrDefault(e =>
                        e.FullName.Replace('/', '\\').Equals(ModFolder + @"\ReceiverMultiplayer.dll", StringComparison.OrdinalIgnoreCase));
                    if (entry == null || entry.Length > MaxZipBytes) return null;
                    entry.ExtractToFile(temp, true);
                }
                return DllVersion(temp);
            }
            catch { return null; }
            finally { TryDelete(temp); }
        }

        // ---------------- the in-game question ----------------

        static string ConfigPath(string game) => Path.Combine(game, ConfigFile);

        /// <summary>Whether the game asks "Update to X?" at launch (on unless the player ticked "Don't show again").</summary>
        public static bool AskInGame(string game)
        {
            try
            {
                string path = ConfigPath(game);
                if (!File.Exists(path)) return true;
                var m = Regex.Match(File.ReadAllText(path), @"^\s*AskToUpdate\s*=\s*(\w+)", RegexOptions.Multiline);
                return !m.Success || !m.Groups[1].Value.Equals("false", StringComparison.OrdinalIgnoreCase);
            }
            catch { return true; }
        }

        public static void SetAskInGame(string game, bool on)
        {
            string path = ConfigPath(game);
            string value = on ? "true" : "false";
            string text = File.Exists(path) ? File.ReadAllText(path) : "";
            var line = new Regex(@"^(\s*AskToUpdate\s*=\s*)\w+", RegexOptions.Multiline);
            if (line.IsMatch(text)) text = line.Replace(text, "${1}" + value, 1);
            else
            {
                string entry = "## When a newer version is out, ask on launch whether to update.\r\n# Setting type: Boolean\r\n# Default value: true\r\nAskToUpdate = " + value + "\r\n";
                var section = new Regex(@"^\[Updates\]\s*$", RegexOptions.Multiline).Match(text);
                if (section.Success) text = text.Insert(section.Index + section.Length, "\r\n\r\n" + entry);
                else text = text.TrimEnd() + (text.Length > 0 ? "\r\n\r\n" : "") + "[Updates]\r\n\r\n" + entry;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, text, new UTF8Encoding(false));
        }

        // ---------------- releases ----------------

        static WebClient Client()
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12 | (SecurityProtocolType)12288; // TLS 1.2 and 1.3
            var c = new WebClient();
            c.Headers[HttpRequestHeader.UserAgent] = "ReceiverMultiplayer-Updater";
            return c;
        }

        /// <summary>The newest release on GitHub.</summary>
        public static Release Latest()
        {
            string json;
            using (var c = Client())
            {
                c.Headers[HttpRequestHeader.Accept] = "application/vnd.github+json";
                json = c.DownloadString(LatestApi);
            }
            var root = new JavaScriptSerializer().DeserializeObject(json) as Dictionary<string, object>;
            if (root == null || !(root.TryGetValue("tag_name", out var tagObj) && tagObj is string tag)) throw new Exception("GitHub sent no release.");
            var version = Parse(tag);
            if (version == null) throw new Exception($"Release '{tag}' has no version number.");
            var r = new Release { Version = version, Tag = tag };
            if (root.TryGetValue("assets", out var assetsObj) && assetsObj is IEnumerable assets)
                foreach (var a in assets.OfType<Dictionary<string, object>>())
                {
                    string name = a.TryGetValue("name", out var n) ? n as string : null;
                    if (name == null || !Regex.IsMatch(name, @"^ReceiverMultiplayer-[\d.]+\.zip$", RegexOptions.IgnoreCase)) continue;
                    r.ZipName = name;
                    r.ZipUrl = a.TryGetValue("browser_download_url", out var u) ? u as string : null;
                    r.Size = a.TryGetValue("size", out var s) ? Convert.ToInt64(s) : 0;
                    string digest = a.TryGetValue("digest", out var d) ? d as string : null;
                    if (digest != null && digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)) r.Sha256 = digest.Substring(7).ToLowerInvariant();
                    break;
                }
            if (r.ZipUrl == null || !r.ZipUrl.StartsWith("https://github.com/" + Repo + "/releases/download/", StringComparison.OrdinalIgnoreCase))
                throw new Exception($"Release {tag} has no mod download.");
            return r;
        }

        /// <summary>
        /// Download the release's zip and check it against GitHub's checksum. It's kept in memory, never in a
        /// temporary file: nothing else can swap it between the check and the install.
        /// </summary>
        public static byte[] Download(Release r, Action<int> progress)
        {
            if (r.Size > MaxZipBytes) throw new Exception("The download is unexpectedly large.");
            byte[] data = null;
            using (var c = Client())
            using (var done = new ManualResetEventSlim())
            {
                Exception error = null;
                c.DownloadProgressChanged += (s, e) =>
                {
                    if (e.BytesReceived > MaxZipBytes) c.CancelAsync();
                    progress?.Invoke(e.ProgressPercentage);
                };
                c.DownloadDataCompleted += (s, e) =>
                {
                    if (e.Cancelled) error = new Exception("it was unexpectedly large");
                    else if (e.Error != null) error = e.Error;
                    else data = e.Result;
                    done.Set();
                };
                c.DownloadDataAsync(new Uri(r.ZipUrl));
                if (!done.Wait(TimeSpan.FromMinutes(10))) { c.CancelAsync(); throw new Exception("The download took too long."); }
                if (error != null) throw new Exception("The download failed: " + error.Message);
            }
            if (data == null || data.Length > MaxZipBytes) throw new Exception("The download is unexpectedly large.");
            if (r.Sha256 != null && Sha256(data) != r.Sha256)
                throw new Exception("The download doesn't match the release (checksum differs); nothing was changed.");
            return data;
        }

        public static string Sha256(byte[] data)
        {
            using (var sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(data)).Replace("-", "").ToLowerInvariant();
        }

        /// <summary>A release zip on disk (one the player chose), read once.</summary>
        public static byte[] ReadZip(string path)
        {
            var info = new FileInfo(path);
            if (!info.Exists) throw new FileNotFoundException("The zip isn't there: " + path);
            if (info.Length > MaxZipBytes) throw new Exception(Path.GetFileName(path) + " is too large to be a Receiver Multiplayer release.");
            return File.ReadAllBytes(path);
        }
        static void TryDelete(string path) { try { File.Delete(path); } catch { } }

        // ---------------- installing ----------------

        /// <summary>The game is running from this folder.</summary>
        public static bool GameRunning(string game)
        {
            foreach (var p in Process.GetProcessesByName("Receiver"))
                try
                {
                    if (string.Equals(Path.GetDirectoryName(p.MainModule.FileName), game, StringComparison.OrdinalIgnoreCase)) return true;
                }
                catch { return true; } // can't tell: assume it is
            return false;
        }

        public static void WaitForExit(int pid, TimeSpan timeout)
        {
            try
            {
                using (var p = Process.GetProcessById(pid))
                    if (!p.WaitForExit((int)timeout.TotalMilliseconds)) throw new Exception("Receiver is still running; close it and run the updater again.");
            }
            catch (ArgumentException) { /* already gone */ }
        }

        /// <summary>Can we write here as we are (else the updater asks for admin rights)?</summary>
        public static bool CanWrite(string game)
        {
            try
            {
                string probe = Path.Combine(game, ".rmp-write-test");
                File.WriteAllText(probe, "");
                File.Delete(probe);
                return true;
            }
            catch (UnauthorizedAccessException) { return false; }
            catch (IOException) { return false; }
        }

        // Everything a release may write: BepInEx's loader at the game root, BepInEx itself, and the mod's own folder.
        static readonly Regex Allowed = new Regex(
            @"^(winhttp\.dll|doorstop_config\.ini|BepInEx/core/[^/\\:]+\.dll|BepInEx/plugins/ReceiverMultiplayer/[^/\\:]+)$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        /// <summary>
        /// Unpack the release into the game folder. Every entry must be one of the files a release ships, in its
        /// place (anything else, a name that climbs out, or a drive or absolute path refuses the whole zip before
        /// anything is written). The updater can replace itself: the running file is moved aside first.
        /// </summary>
        public static int Install(byte[] zip, string game)
        {
            string root = Path.GetFullPath(game).TrimEnd('\\') + "\\";
            using (var archive = new ZipArchive(new MemoryStream(zip, false), ZipArchiveMode.Read))
            {
                var plan = new List<(ZipArchiveEntry entry, string dest)>();
                long total = 0;
                foreach (var e in archive.Entries)
                {
                    if (string.IsNullOrEmpty(e.Name)) continue; // a folder
                    if (!Allowed.IsMatch(e.FullName.Replace('\\', '/')))
                        throw new Exception($"The zip holds a file a release never has ('{e.FullName}'); nothing was installed.");
                    string name = e.FullName.Replace('/', '\\');
                    if (name.StartsWith("\\") || name.Contains(":") || name.Split('\\').Any(part => part == ".."))
                        throw new Exception($"The zip holds an unsafe path ('{e.FullName}'); nothing was installed.");
                    string dest = Path.GetFullPath(Path.Combine(root, name));
                    if (!dest.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                        throw new Exception($"The zip holds a path outside the game folder ('{e.FullName}'); nothing was installed.");
                    total += e.Length;
                    plan.Add((e, dest));
                }
                if (total > 4 * MaxZipBytes) throw new Exception("The zip unpacks to an unexpectedly large size; nothing was installed.");
                if (!plan.Any(p => p.dest.EndsWith(@"\" + ModFolder + @"\ReceiverMultiplayer.dll", StringComparison.OrdinalIgnoreCase)))
                    throw new Exception("That zip isn't Receiver Multiplayer; nothing was installed.");

                string self = Path.GetFullPath(Process.GetCurrentProcess().MainModule.FileName);
                foreach (var (entry, dest) in plan)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(dest));
                    if (string.Equals(dest, self, StringComparison.OrdinalIgnoreCase))
                    {
                        string old = self + ".old";
                        TryDelete(old);
                        File.Move(self, old); // a running program can be moved, not overwritten
                    }
                    entry.ExtractToFile(dest, true);
                }
                return plan.Count;
            }
        }

        /// <summary>Leftovers from replacing a running updater last time.</summary>
        public static void CleanUp()
        {
            try { TryDelete(Process.GetCurrentProcess().MainModule.FileName + ".old"); } catch { }
        }

        /// <summary>Start the game again: through Steam for a Steam copy (so Steam features work), else directly.</summary>
        public static void Launch(string game)
        {
            if (IsSteamInstall(game)) Process.Start(new ProcessStartInfo("steam://rungameid/" + SteamAppId) { UseShellExecute = true });
            else Process.Start(new ProcessStartInfo(Path.Combine(game, "Receiver.exe")) { UseShellExecute = true, WorkingDirectory = game });
        }

        public static void Log(string game, string line)
        {
            try
            {
                string dir = game != null && Directory.Exists(Path.Combine(game, "BepInEx")) ? Path.Combine(game, "BepInEx") : Path.GetTempPath();
                File.AppendAllText(Path.Combine(dir, "ReceiverMultiplayer-Updater.log"), $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {line}\r\n");
            }
            catch { }
        }
    }
}
