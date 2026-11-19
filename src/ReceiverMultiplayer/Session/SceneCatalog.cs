using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

namespace ReceiverMultiplayer.Session
{
    /// <summary>
    /// The scenes a session can take everyone to: the main level, and whatever the game's own pause menu offers
    /// under Scene Loading (its buttons call <c>optionsmenuscript.OpenScene("…")</c> with the name baked into the
    /// button). Only these are ever loaded on the host's word, and only if this game has them.
    /// </summary>
    static class SceneCatalog
    {
        public const string GameScene = "scene";
        public const string GunRange = "range";
        public const int MaxNameBytes = 64;

        // What the pause menu offers in Receiver 1 (Load Apartments, Load Gun Range); more are picked up from it.
        static readonly HashSet<string> _menuScenes = new HashSet<string> { GameScene, GunRange };

        static readonly FieldInfo PersistentCallsField = typeof(UnityEventBase).GetField("m_PersistentCalls", BindingFlags.Instance | BindingFlags.NonPublic);
        static FieldInfo _calls, _arguments, _stringArgument;

        /// <summary>The game's main level or a scene its own menu can open, and present in this game.</summary>
        public static bool IsAllowed(string name)
        {
            if (string.IsNullOrEmpty(name) || name.Length > MaxNameBytes) return false;
            if (name != GameScene)
            {
                Refresh();
                if (!_menuScenes.Contains(name)) return false;
            }
            return Application.CanStreamedLevelBeLoaded(name);
        }

        public static string DisplayName(string scene)
        {
            if (scene == GameScene) return "the main level";
            if (scene == GunRange) return "the gun range";
            return "'" + scene + "'";
        }

        /// <summary>Collect the scene names from the pause menu's OpenScene buttons in the open scene(s).</summary>
        public static void Refresh()
        {
            foreach (var b in Resources.FindObjectsOfTypeAll<Button>())
            {
                if (b == null || !b.gameObject.scene.IsValid()) continue; // prefabs and assets, not menus
                var e = b.onClick;
                for (int i = 0; i < e.GetPersistentEventCount(); i++)
                {
                    if (e.GetPersistentMethodName(i) != nameof(optionsmenuscript.OpenScene)) continue;
                    var arg = StringArgument(e, i);
                    if (!string.IsNullOrEmpty(arg)) _menuScenes.Add(arg);
                }
            }
        }

        public static IEnumerable<string> MenuScenes
        {
            get
            {
                Refresh();
                return _menuScenes;
            }
        }

        /// <summary>The string a persistent listener passes (UnityEvent keeps it in private serialized data).</summary>
        public static string StringArgument(UnityEventBase e, int index)
        {
            try
            {
                var group = PersistentCallsField?.GetValue(e);
                if (group == null) return null;
                _calls = _calls ?? group.GetType().GetField("m_Calls", BindingFlags.Instance | BindingFlags.NonPublic);
                var list = _calls?.GetValue(group) as IList;
                if (list == null || index < 0 || index >= list.Count || list[index] == null) return null;
                var call = list[index];
                _arguments = _arguments ?? call.GetType().GetField("m_Arguments", BindingFlags.Instance | BindingFlags.NonPublic);
                var args = _arguments?.GetValue(call);
                if (args == null) return null;
                _stringArgument = _stringArgument ?? args.GetType().GetField("m_StringArgument", BindingFlags.Instance | BindingFlags.NonPublic);
                return _stringArgument?.GetValue(args) as string;
            }
            catch
            {
                return null;
            }
        }
    }
}
