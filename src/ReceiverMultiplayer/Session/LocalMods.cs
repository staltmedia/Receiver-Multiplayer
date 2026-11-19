using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using UnityEngine;

namespace ReceiverMultiplayer.Session
{
    /// <summary>One file of a local mod folder, as hashed and shared.</summary>
    sealed class ModFile
    {
        public string Name = "";   // file name only, never a path
        public long Size;
        public string Sha256 = ""; // lowercase hex
    }

    /// <summary>A local mod folder as the session sees it: its mod files and their combined hash.</summary>
    sealed class ModFolderInfo
    {
        public string Dir = "";
        public string Hash = "";        // "" when the folder couldn't be read
        public long Size;
        public readonly List<ModFile> Files = new List<ModFile>();
        public string NotShareable;     // why it can't be sent to another player (null: it can)
        internal string Signature = "";
    }

    /// <summary>
    /// Mods that aren't on the Steam Workshop: the folders the game loads from its Mods folder (and the copies
    /// fetched from hosts). Two players have the same local mod only if the folder name and every file match,
    /// so each folder is identified by a hash of its files. A guest that lacks one fetches the host's copy into
    /// a cache of its own (never into the Mods folder) and loads it from there until the game restarts.
    /// </summary>
    static class LocalMods
    {
        public const int MaxFiles = 64;
        public const long MaxFileBytes = 1L << 30; // 1 GB
        public const long MaxModBytes = 2L << 30;  // 2 GB
        public const int MaxNameChars = 120;
        public const int HashChars = 64;

        static readonly object _lock = new object();
        static readonly Dictionary<string, ModFolderInfo> _cache = new Dictionary<string, ModFolderInfo>();
        static int _jobs;

        /// <summary>Where mods fetched from hosts are kept: one folder per version, named by its hash.</summary>
        public static string CacheRoot => Path.Combine(Application.persistentDataPath, "Multiplayer", "Mods from hosts");

        public static string CacheDir(string folder, string hash) => Path.Combine(CacheRoot, hash.Substring(0, 16), folder);

        public static string IncomingDir(string folder, string hash) => Path.Combine(CacheRoot, "incoming", hash.Substring(0, 16) + "-" + folder);

        /// <summary>The path is inside the cache (so it may be replaced or deleted: it holds nothing of the player's).</summary>
        public static bool InCache(string path)
        {
            try
            {
                string root = Path.GetFullPath(CacheRoot).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
                return Path.GetFullPath(path).StartsWith(root, StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        // ---------------- names ----------------

        static readonly HashSet<string> Reserved = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "CON", "PRN", "AUX", "NUL",
            "COM0", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
            "LPT0", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
        };

        // Mod folders hold asset bundles, manifests, metadata.json and a thumbnail. Nothing that runs.
        static readonly HashSet<string> BlockedExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".exe", ".dll", ".bat", ".cmd", ".com", ".scr", ".pif", ".cpl", ".msi", ".msp", ".msc", ".lnk", ".url",
            ".ps1", ".psm1", ".psd1", ".vbs", ".vbe", ".js", ".jse", ".wsf", ".wsh", ".hta", ".reg", ".inf", ".scf",
            ".jar", ".sh", ".bash", ".command", ".so", ".dylib", ".app", ".py", ".pyw", ".application", ".gadget",
            ".appref-ms", ".sys", ".drv", ".ocx", ".iso", ".img", ".vhd", ".vhdx", ".chm", ".xll", ".wsc", ".msh",
            ".msh1", ".msh2", ".mshxml", ".ps1xml", ".ps2", ".ps2xml", ".psc1", ".psc2", ".settingcontent-ms",
            ".library-ms", ".searchconnector-ms", ".diagcab", ".appx", ".appxbundle", ".msix", ".msixbundle", ".htm",
            ".html", ".mht", ".mhtml", ".xht", ".xhtml", ".svg", ".website", ".job", ".lnk", ".shb", ".shs", ".ws",
            ".mde", ".mdb", ".accde", ".xlam", ".xlsm", ".docm", ".pptm", ".one", ".vb", ".csh", ".ksh", ".pl",
            ".rb", ".ins", ".isp", ".crt", ".cer", ".der", ".pfx", ".p12", ".mof", ".theme", ".themepack", ".cab",
        };

        const string NameChars = " _-.()+[]',&!#";

        /// <summary>A plain file or folder name: no path parts, no device names, nothing a file system treats specially.</summary>
        public static bool IsSafeName(string name)
        {
            if (string.IsNullOrEmpty(name) || name.Length > MaxNameChars) return false;
            if (name[0] == '.' || name[0] == ' ' || name.EndsWith(".") || name.EndsWith(" ") || name.Contains("..")) return false;
            foreach (char c in name)
            {
                bool ok = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || NameChars.IndexOf(c) >= 0;
                if (!ok) return false;
            }
            string stem = name.Split('.')[0].Trim();
            return !Reserved.Contains(stem);
        }

        public static bool IsBlockedFile(string name) => BlockedExtensions.Contains(Path.GetExtension(name) ?? "");

        /// <summary>Files the game never reads and file systems add on their own: not part of a mod.</summary>
        public static bool IsJunk(string name) =>
            name.StartsWith(".") || name.Equals("thumbs.db", StringComparison.OrdinalIgnoreCase)
            || name.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase);

        public static bool IsHash(string s)
        {
            if (s == null || s.Length != HashChars) return false;
            foreach (char c in s)
                if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'))) return false;
            return true;
        }

        // ---------------- hashing ----------------

        /// <summary>
        /// The folder's mod files (top level only, like the game) and their hash, from the cache while the folder
        /// is unchanged. Reading a large folder for the first time takes a moment; <see cref="Prewarm"/> does it
        /// off the main thread before a session needs it.
        /// </summary>
        public static ModFolderInfo Read(string dir)
        {
            string key = Key(dir);
            List<FileInfo> files;
            string sig;
            try
            {
                files = List(dir);
                sig = Signature(files);
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"Couldn't read mod folder '{Path.GetFileName(dir)}': {e.Message}");
                return new ModFolderInfo { Dir = dir, NotShareable = "its folder couldn't be read" }; // the details (paths) stay here
            }
            lock (_lock)
                if (_cache.TryGetValue(key, out var cached) && cached.Signature == sig) return cached;
            var info = Compute(dir, files, sig);
            lock (_lock) _cache[key] = info;
            return info;
        }

        static bool IsFresh(string dir)
        {
            try
            {
                string sig = Signature(List(dir));
                lock (_lock) return _cache.TryGetValue(Key(dir), out var c) && c.Signature == sig;
            }
            catch
            {
                return true; // unreadable: Read reports it
            }
        }

        /// <summary>Hashing local mod folders in the background.</summary>
        public static bool Hashing => Volatile.Read(ref _jobs) > 0;

        /// <summary>Hash any of these folders that changed or are new, on a worker thread.</summary>
        public static void Prewarm(IList<string> dirs)
        {
            if (Hashing || dirs == null) return;
            var todo = dirs.Where(d => !string.IsNullOrEmpty(d) && !IsFresh(d)).Distinct().ToList();
            if (todo.Count == 0) return;
            Interlocked.Increment(ref _jobs);
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    foreach (var d in todo)
                        try { Read(d); }
                        catch { /* Read reports it on the main thread */ }
                }
                finally
                {
                    Interlocked.Decrement(ref _jobs);
                }
            });
        }

        static string Key(string dir) => Path.GetFullPath(dir).TrimEnd('\\', '/').ToLowerInvariant();

        static List<FileInfo> List(string dir)
        {
            var list = new DirectoryInfo(dir).GetFiles().Where(f => !IsJunk(f.Name)).ToList();
            list.Sort((a, b) => CompareNames(a.Name, b.Name));
            return list;
        }

        static int CompareNames(string a, string b)
        {
            int c = string.CompareOrdinal(a.ToLowerInvariant(), b.ToLowerInvariant());
            return c != 0 ? c : string.CompareOrdinal(a, b);
        }

        static string Signature(List<FileInfo> files) =>
            string.Join("|", files.Select(f => f.Name + ":" + f.Length + ":" + f.LastWriteTimeUtc.Ticks));

        static ModFolderInfo Compute(string dir, List<FileInfo> files, string sig)
        {
            var info = new ModFolderInfo { Dir = dir, Signature = sig };
            try
            {
                using (var sha = SHA256.Create())
                {
                    var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var f in files)
                    {
                        string hex;
                        using (var s = new FileStream(f.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16))
                            hex = Hex(sha.ComputeHash(s));
                        info.Files.Add(new ModFile { Name = f.Name, Size = f.Length, Sha256 = hex });
                        info.Size += f.Length;
                        if (info.NotShareable != null) continue;
                        if (!IsSafeName(f.Name)) info.NotShareable = $"its file '{f.Name}' has an unusual name";
                        else if (IsBlockedFile(f.Name)) info.NotShareable = $"it contains '{f.Name}', which isn't mod data";
                        else if (f.Length > MaxFileBytes) info.NotShareable = $"'{f.Name}' is larger than 1 GB";
                        else if (!names.Add(f.Name)) info.NotShareable = "two of its files differ only in capitals";
                    }
                }
                if (info.NotShareable == null && info.Files.Count > MaxFiles) info.NotShareable = $"it has more than {MaxFiles} files";
                if (info.NotShareable == null && info.Size > MaxModBytes) info.NotShareable = "it's larger than 2 GB";
                if (info.NotShareable == null && info.Files.Count == 0) info.NotShareable = "its folder is empty";
                info.Hash = Combine(info.Files);
            }
            catch (Exception e)
            {
                info.Files.Clear();
                info.Size = 0;
                info.Hash = "";
                Plugin.Log.LogWarning($"Couldn't read the files of mod folder '{Path.GetFileName(dir)}': {e.Message}");
                info.NotShareable = "its files couldn't be read"; // the details (paths) stay here
            }
            return info;
        }

        /// <summary>The folder hash: every file's name, size and SHA-256, in name order.</summary>
        public static string Combine(IEnumerable<ModFile> files)
        {
            var sorted = files.ToList();
            sorted.Sort((a, b) => CompareNames(a.Name, b.Name));
            var sb = new StringBuilder();
            foreach (var f in sorted) sb.Append(f.Name).Append('\n').Append(f.Size).Append('\n').Append(f.Sha256).Append('\n');
            using (var sha = SHA256.Create())
                return Hex(sha.ComputeHash(Encoding.UTF8.GetBytes(sb.ToString())));
        }

        public static string Hex(byte[] bytes)
        {
            var sb = new StringBuilder(bytes.Length * 2);
            foreach (byte b in bytes) sb.Append(b.ToString("x2"));
            return sb.ToString();
        }

        public static string FormatSize(long bytes) =>
            bytes >= 1L << 30 ? $"{bytes / (double)(1L << 30):0.00} GB"
            : bytes >= 1L << 20 ? $"{bytes / (double)(1L << 20):0.0} MB"
            : $"{Math.Max(1, (bytes + 1023) / 1024)} KB";
    }
}
