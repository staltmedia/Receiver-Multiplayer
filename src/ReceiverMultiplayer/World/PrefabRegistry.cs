using System.Collections.Generic;
using UnityEngine;

namespace ReceiverMultiplayer.World
{
    enum PrefabKind : byte { Gun, Magazine, Round, Casing, EmptyCasing, FullCasing, Flashlight, Tape }

    /// <summary>
    /// Every item prefab a player can hold, carry or drop, in a fixed order every machine agrees on
    /// (the gun list order, which the join gate keeps identical). Network messages refer to items by index.
    /// </summary>
    static class PrefabRegistry
    {
        public struct Entry
        {
            public PrefabKind Kind;
            public int Weapon; // index into the gun list, or -1
            public GameObject Prefab;
        }

        static readonly List<Entry> _entries = new List<Entry>();
        static readonly Dictionary<string, List<int>> _byName = new Dictionary<string, List<int>>();
        static bool _built;

        public static int Count { get { Ensure(); return _entries.Count; } }

        public static bool TryGet(int index, out Entry entry)
        {
            Ensure();
            if (index >= 0 && index < _entries.Count) { entry = _entries[index]; return entry.Prefab != null; }
            entry = default;
            return false;
        }

        public static void Invalidate() { _built = false; }

        /// <summary>Find the prefab an instance was made from (by name), preferring the given weapon's items.</summary>
        public static int IndexOf(GameObject instance, int preferWeapon = -1)
        {
            if (instance == null) return -1;
            Ensure();
            string name = BaseName(instance.name);
            if (!_byName.TryGetValue(name, out var list) || list.Count == 0) return -1;
            if (preferWeapon >= 0)
                foreach (int i in list)
                    if (_entries[i].Weapon == preferWeapon) return i;
            return list[0];
        }

        public static int IndexOf(PrefabKind kind, int weapon)
        {
            Ensure();
            for (int i = 0; i < _entries.Count; i++)
                if (_entries[i].Kind == kind && _entries[i].Weapon == weapon) return i;
            return -1;
        }

        public static string BaseName(string name)
        {
            if (string.IsNullOrEmpty(name)) return "";
            int cut = name.IndexOf("(Clone)", System.StringComparison.Ordinal);
            return (cut >= 0 ? name.Substring(0, cut) : name).Trim();
        }

        static void Ensure()
        {
            if (_built && _entries.Count > 0) return;
            _built = true;
            _entries.Clear();
            _byName.Clear();
            var holder = Object.FindObjectOfType<GUISkinHolder>();
            if (holder == null || holder.weapons == null) return;
            // Every machine must list the same prefabs in the same order: an add-on gun left as an empty
            // placeholder here would shift every index after it.
            Session.Guns.LoadAll(holder);
            for (int w = 0; w < holder.weapons.Length; w++)
            {
                var wh = holder.weapons[w] != null ? holder.weapons[w].GetComponent<WeaponHolder>() : null;
                if (wh == null) continue;
                Add(PrefabKind.Gun, w, wh.gun_object);
                Add(PrefabKind.Magazine, w, wh.mag_object);
                Add(PrefabKind.Round, w, wh.bullet_object);
                Add(PrefabKind.Casing, w, wh.casing_object);
                var gun = wh.gun_object != null ? wh.gun_object.GetComponent<GunScript>() : null;
                if (gun != null)
                {
                    Add(PrefabKind.EmptyCasing, w, gun.empty_casing);
                    Add(PrefabKind.FullCasing, w, gun.full_casing);
                }
            }
            Add(PrefabKind.Flashlight, -1, holder.flashlight_object);
            Add(PrefabKind.Tape, -1, holder.tape_object);
        }

        static void Add(PrefabKind kind, int weapon, GameObject prefab)
        {
            if (prefab == null) return;
            int index = _entries.Count;
            _entries.Add(new Entry { Kind = kind, Weapon = weapon, Prefab = prefab });
            string name = BaseName(prefab.name);
            if (!_byName.TryGetValue(name, out var list)) _byName[name] = list = new List<int>();
            list.Add(index);
        }
    }
}
