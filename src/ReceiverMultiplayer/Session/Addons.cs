using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using HarmonyLib;
using ReceiverMultiplayer.Net;
using UnityEngine;

namespace ReceiverMultiplayer.Session
{
    /// <summary>One add-on (gun, tapes or level tiles): a Steam Workshop item, or a local mod folder.</summary>
    sealed class Addon
    {
        public ulong WorkshopId;   // 0 for a local mod
        public string Folder = ""; // the folder it loads from ("modfile_…" for a local mod; for Workshop items, informational)
        public string Title = "";
        public string Hash = "";   // local mods: hash of the folder's files (see LocalMods); Workshop items: ""
        public long Size;          // local mods: total bytes of those files
        public bool Shareable;     // local mods: the host can send it to a player who lacks it
        public string Kind = "add-on"; // this game's own list only (for the log): Gun, Tapes or Tile
        public string Dir;         // this game's own list only: the folder it was loaded from. Never sent.

        public bool OnWorkshop => WorkshopId != 0;
        public string Key => OnWorkshop ? "w" + WorkshopId : "l" + Folder.ToLowerInvariant() + ":" + Hash;
        public override string ToString() => Title.Length > 0 ? Title : OnWorkshop ? "Workshop item " + WorkshopId : Folder;
    }

    /// <summary>
    /// The add-ons that change the game (extra guns, tapes, level tiles). Everyone in a session must have the same
    /// ones, because the gun list and the level tiles come from them (the shared seed only builds the same tower
    /// from the same tiles). The client sends its list in the handshake; the host compares and, on a mismatch,
    /// sends back what the client is missing and has extra, so the client can subscribe to the missing ones.
    /// </summary>
    static class Addons
    {
        public const int MaxAddons = 512;
        public const int MaxTextBytes = 128;

        static readonly Regex WorkshopFolder = new Regex(@"[\\/]234190[\\/](\d{4,20})(?:[\\/]|$)", RegexOptions.Compiled);
        static readonly AccessTools.FieldRef<SteamworksUGCItem, char[]> ItemName = AccessTools.FieldRefAccess<SteamworksUGCItem, char[]>("name");

        /// <summary>
        /// Every add-on this game has imported (guns, tapes and level tiles), whether or not this player has mods
        /// turned on: in a session the host's mod settings are used (see <see cref="ModifierSync"/>), so what
        /// matters is what's installed. Steam Workshop items are imported even with mods off; local mod folders
        /// only when mods were on at launch.
        /// </summary>
        public static List<Addon> Local()
        {
            var list = LoadOrder();
            list.Sort((x, y) => string.CompareOrdinal(x.Key, y.Key));
            return list;
        }

        /// <summary>
        /// This game's add-ons in the order it loaded them, which is the order of the gun list and of the level
        /// tiles the seeded tower picks from. Local mods load at launch, Workshop items once Steam answers, and
        /// fetched ones when they arrive, so the same add-ons can be in a different order on each machine.
        /// </summary>
        public static List<Addon> LoadOrder()
        {
            var list = new List<Addon>();
            try
            {
                var seen = new HashSet<string>();
                foreach (var mod in ModManager.importedMods.ToList())
                {
                    if (mod == null || string.IsNullOrEmpty(mod.path)) continue;
                    var a = Describe(mod);
                    if (seen.Add(a.Key)) list.Add(a);
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"Couldn't read the loaded add-ons: {e.Message}");
            }
            return list;
        }

        /// <summary>Folders of the loaded local mods (to hash them ahead of a session).</summary>
        static List<string> LocalDirs()
        {
            var dirs = new List<string>();
            try
            {
                foreach (var mod in ModManager.importedMods.ToList())
                    if (mod != null && !string.IsNullOrEmpty(mod.path) && !IsWorkshopPath(mod))
                        dirs.Add(Path.GetDirectoryName(mod.path));
            }
            catch { /* Local() reports it */ }
            return dirs;
        }

        /// <summary>
        /// Put this game's add-ons in the host's load order (the ones the host has, in its order; anything else
        /// after them), so the gun list and level tiles line up. Returns whether anything moved.
        /// </summary>
        public static bool MatchOrder(IList<string> hostKeys)
        {
            if (hostKeys == null || hostKeys.Count == 0) return false;
            var rank = new Dictionary<string, int>();
            for (int i = 0; i < hostKeys.Count; i++)
                if (!rank.ContainsKey(hostKeys[i])) rank[hostKeys[i]] = i;
            var mods = ModManager.importedMods;
            var keyed = mods.Select((m, i) => (m, i, key: m != null && !string.IsNullOrEmpty(m.path) ? Describe(m).Key : "")).ToList();
            var sorted = keyed
                .OrderBy(x => rank.TryGetValue(x.key, out int r) ? r : int.MaxValue)
                .ThenBy(x => x.i)
                .Select(x => x.m)
                .ToList();
            if (sorted.SequenceEqual(mods)) return false;
            mods.Clear();
            mods.AddRange(sorted);
            Plugin.Log.LogInfo("Put the add-ons in the host's load order.");
            return true;
        }

        /// <summary>
        /// Leave an add-on out of this game until it restarts (nothing on disk changes, Workshop subscriptions
        /// included): it no longer counts as installed, and levels built from now on don't use it. Its asset bundle
        /// is closed so another version of the same files can load. Returns how many loaded copies were removed.
        /// </summary>
        public static int Unload(Addon a)
        {
            int n = 0;
            foreach (var mod in ModManager.importedMods.ToList())
            {
                if (mod == null || string.IsNullOrEmpty(mod.path) || Describe(mod).Key != a.Key) continue;
                ModManager.importedMods.Remove(mod);
                try
                {
                    if (mod.assetBundle != null) mod.assetBundle.Unload(false); // what's already in use stays
                }
                catch (Exception e)
                {
                    Plugin.Log.LogWarning($"Couldn't close the files of '{a}': {e.Message}");
                }
                n++;
            }
            if (n > 0) Plugin.Log.LogInfo($"Left out '{a}' until Receiver restarts.");
            return n;
        }

        /// <summary>One log line: what this game would send/compare (so a mismatch can be checked from the logs).</summary>
        public static void LogLocal(string when)
        {
            var list = Local();
            string mods = ModManager.IsModsEnabled() ? "mods on" : "mods off";
            string loading = $"gun/tape/tile loading {PlayerPrefs.GetInt("mod_gun_loading", 0)}/{PlayerPrefs.GetInt("mod_tape_loading", 0)}/{PlayerPrefs.GetInt("mod_tile_loading", 0)}";
            Plugin.Log.LogInfo($"Add-ons installed ({when}; {mods}, {loading}): " + (list.Count == 0
                ? "none"
                : string.Join(", ", list.Select(a => $"{a} [{a.Kind}, " + (a.OnWorkshop
                    ? $"Workshop {a.WorkshopId}]"
                    : $"local {a.Folder} {(a.Hash.Length >= 8 ? a.Hash.Substring(0, 8) : "unhashed")}, {LocalMods.FormatSize(a.Size)}]")))));
        }

        // ---------------- readiness ----------------

        /// <summary>The longest a host or join waits for add-ons to finish loading before going ahead anyway.</summary>
        public const float MaxWaitSeconds = 60f;

        static readonly FieldInfo SteamLoadingItems = AccessTools.Field(typeof(SteamScript), "loadItems");
        static readonly FieldInfo WeaponsOrigin = AccessTools.Field(typeof(GUISkinHolder), "weapons_origin");

        /// <summary>
        /// The game is still loading add-ons: Steam hasn't yet said which Workshop items you're subscribed to, or
        /// some are still importing. The game doesn't wait for this before building the first level, so a list
        /// taken in the first seconds after launch can be incomplete.
        /// </summary>
        public static bool StillLoading(out string what)
        {
            what = null;
            try
            {
                if (ModImporter.IsImportingMods())
                {
                    int n = ModImporter.GetRemainingModsCount();
                    what = n > 0 ? $"{n} add-on{(n == 1 ? "" : "s")} still loading" : "add-ons still loading";
                    return true;
                }
                if (SteamManager.Initialized && SteamLoadingItems != null)
                {
                    var steam = UnityEngine.Object.FindObjectOfType<SteamScript>();
                    if (steam != null && (bool)SteamLoadingItems.GetValue(steam))
                    {
                        what = "asking Steam for your Workshop add-ons";
                        return true;
                    }
                }
                LocalMods.Prewarm(LocalDirs());
                if (LocalMods.Hashing)
                {
                    what = "checking your non-Workshop mods";
                    return true;
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"Couldn't check whether add-ons are still loading: {e.Message}");
            }
            return false;
        }

        /// <summary>
        /// Rebuild this scene's gun list the way the game builds it when a level loads (base guns, plus mod guns
        /// per the current mod settings). The level that was built at launch may predate the add-on imports, or the
        /// host's mod settings now in effect; a run's level is always built fresh, and this makes the list match it.
        /// </summary>
        public static void RefreshGunList()
        {
            try
            {
                var holder = UnityEngine.Object.FindObjectOfType<GUISkinHolder>();
                if (holder == null) return;
                if (!(WeaponsOrigin?.GetValue(holder) is IEnumerable<GameObject> origin)) return;
                if (ModManager.IsModsEnabled()) holder.InsertGunMods(); // starts again from the base list each time
                else holder.weapons = origin.ToArray();
                Guns.LoadAll(holder);
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"Couldn't refresh the gun list: {e.Message}");
            }
        }

        // A mod's path is its asset bundle file, inside the folder it was loaded from.
        static bool IsWorkshopPath(Mod mod)
        {
            string path = mod.path.Replace('\\', '/').TrimEnd('/');
            if (WorkshopFolder.IsMatch(path + "/")) return true;
            return !mod.IsLocalMod() && ulong.TryParse(Path.GetFileName(Path.GetDirectoryName(path) ?? ""), out _);
        }

        static Addon Describe(Mod mod)
        {
            string path = mod.path.Replace('\\', '/').TrimEnd('/');
            string dir = Path.GetDirectoryName(path) ?? "";
            var a = new Addon { Folder = Path.GetFileName(dir) ?? "", Kind = SafeType(mod), Dir = dir };
            var m = WorkshopFolder.Match(path + "/");
            if (m.Success && ulong.TryParse(m.Groups[1].Value, out ulong id)) a.WorkshopId = id;
            else if (ulong.TryParse(a.Folder, out id) && !mod.IsLocalMod()) a.WorkshopId = id;
            a.Title = TitleOf(mod) ?? "";
            if (!a.OnWorkshop)
            {
                var info = LocalMods.Read(dir);
                a.Hash = info.Hash;
                a.Size = info.Size;
                a.Shareable = info.NotShareable == null && LocalMods.IsSafeName(a.Folder);
            }
            return a;
        }

        static string SafeType(Mod mod)
        {
            try
            {
                var t = mod.GetTypeString(); // "Gun", "Tapes" or "Tile"
                return string.IsNullOrEmpty(t) ? "add-on" : t;
            }
            catch
            {
                return "add-on";
            }
        }

        static string TitleOf(Mod mod)
        {
            try
            {
                var gun = mod.mainAsset != null ? mod.mainAsset.GetComponent<WeaponHolder>() : null;
                if (gun != null && !string.IsNullOrEmpty(gun.display_name)) return gun.display_name;
                if (mod.steamworksItem != null)
                {
                    var chars = ItemName(mod.steamworksItem);
                    if (chars != null)
                    {
                        var s = new string(chars).TrimEnd('\0').Trim();
                        if (s.Length > 0) return s;
                    }
                }
                if (mod.mainAsset != null) return mod.mainAsset.name;
            }
            catch { /* fall back to the folder */ }
            return null;
        }

        // ---------------- wire ----------------

        public const int MaxKeyBytes = 256;

        public static void Write(NetWriter w, IList<Addon> list)
        {
            int n = Math.Min(list.Count, MaxAddons);
            w.U16((ushort)n);
            for (int i = 0; i < n; i++)
            {
                var a = list[i];
                w.U32((uint)(a.WorkshopId & 0xFFFFFFFF));
                w.U32((uint)(a.WorkshopId >> 32));
                w.Str(a.Folder, MaxTextBytes);
                w.Str(a.Title, MaxTextBytes);
                w.Str(a.OnWorkshop ? "" : a.Hash, LocalMods.HashChars);
                long size = a.OnWorkshop ? 0 : a.Size;
                w.U32((uint)(size & 0xFFFFFFFF));
                w.U32((uint)(size >> 32));
                w.Bool(!a.OnWorkshop && a.Shareable);
            }
        }

        public static List<Addon> Read(ref NetReader rd)
        {
            int n = rd.U16();
            if (n > MaxAddons) throw new ProtocolException("too many add-ons");
            var list = new List<Addon>(n);
            for (int i = 0; i < n; i++)
            {
                ulong lo = rd.U32(), hi = rd.U32();
                var a = new Addon
                {
                    WorkshopId = lo | (hi << 32),
                    Folder = Protocol.SanitizeReason(rd.Str(MaxTextBytes)),
                    Title = Protocol.SanitizeReason(rd.Str(MaxTextBytes)),
                };
                string hash = rd.Str(LocalMods.HashChars);
                ulong slo = rd.U32(), shi = rd.U32();
                bool shareable = rd.Bool();
                if (hash.Length > 0 && !LocalMods.IsHash(hash)) throw new ProtocolException("bad add-on hash");
                ulong size = slo | (shi << 32);
                if (size > (ulong)LocalMods.MaxModBytes * 2) throw new ProtocolException("bad add-on size");
                a.Hash = a.OnWorkshop ? "" : hash;
                a.Size = a.OnWorkshop ? 0 : (long)size;
                a.Shareable = !a.OnWorkshop && shareable;
                list.Add(a);
            }
            return list;
        }

        /// <summary>Add-on keys in load order (sent in the Welcome so a joining game can match the host's order).</summary>
        public static void WriteOrder(NetWriter w, IList<Addon> order)
        {
            int n = Math.Min(order.Count, MaxAddons);
            w.U16((ushort)n);
            for (int i = 0; i < n; i++) w.Str(order[i].Key, MaxKeyBytes);
        }

        public static List<string> ReadOrder(ref NetReader rd)
        {
            int n = rd.U16();
            if (n > MaxAddons) throw new ProtocolException("too many add-ons");
            var keys = new List<string>(n);
            for (int i = 0; i < n; i++) keys.Add(rd.Str(MaxKeyBytes));
            return keys;
        }

        /// <summary>What <paramref name="theirs"/> lacks of <paramref name="mine"/>, and has that mine doesn't.</summary>
        public static void Diff(IList<Addon> mine, IList<Addon> theirs, out List<Addon> missing, out List<Addon> extra)
        {
            var theirKeys = new HashSet<string>(theirs.Select(a => a.Key));
            var myKeys = new HashSet<string>(mine.Select(a => a.Key));
            missing = mine.Where(a => !theirKeys.Contains(a.Key)).ToList();
            extra = theirs.Where(a => !myKeys.Contains(a.Key)).ToList();
        }
    }

    /// <summary>
    /// The last add-on mismatch the host reported. The host keeps the connection open while the joining player
    /// sorts it out (two windows: Workshop add-ons through Steam, and other mods straight from the host); once
    /// nothing differs any more the player says Hello again on the same connection.
    /// </summary>
    static class AddonGate
    {
        public static List<Addon> Missing { get; private set; }
        public static List<Addon> Extra { get; private set; }
        public static bool Pending => Missing != null;

        public static void Set(List<Addon> missing, List<Addon> extra)
        {
            Missing = missing ?? new List<Addon>();
            Extra = extra ?? new List<Addon>();
        }

        public static void Clear()
        {
            Missing = null;
            Extra = null;
        }

        public static bool AnyWorkshop => Pending && (Missing.Any(a => a.OnWorkshop) || Extra.Any(a => a.OnWorkshop));
        public static bool AnyLocal => Pending && (Missing.Any(a => !a.OnWorkshop) || Extra.Any(a => !a.OnWorkshop));

        /// <summary>What still differs: the host's add-ons this game hasn't loaded yet, and extras it still has loaded.</summary>
        public static void Remaining(out List<Addon> missing, out List<Addon> extra)
        {
            missing = new List<Addon>();
            extra = new List<Addon>();
            if (!Pending) return;
            var have = new HashSet<string>(Addons.Local().Select(a => a.Key));
            missing = Missing.Where(a => !have.Contains(a.Key)).ToList();
            extra = Extra.Where(a => have.Contains(a.Key)).ToList();
        }

        /// <summary>Nothing differs any more: ask the host again (same connection). Returns whether it asked.</summary>
        public static bool TryJoin()
        {
            var s = NetSession.Current;
            if (s == null || !s.Resolving) return false;
            Remaining(out var missing, out var extra);
            if (missing.Count > 0 || extra.Count > 0) return false;
            s.RetryJoin();
            return true;
        }
    }
}
