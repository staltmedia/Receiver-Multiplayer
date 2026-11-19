using ReceiverMultiplayer.Net;
using ReceiverMultiplayer.Session;
using UnityEngine;

namespace ReceiverMultiplayer
{
    /// <summary>
    /// Long-lived host for coroutines and the network tick. BepInEx's own manager object does not survive
    /// Receiver's scene loads, so the plugin keeps a hidden object of its own and recreates it whenever it
    /// has been destroyed (game hooks call <see cref="Ensure"/> after every scene start).
    /// </summary>
    sealed class Runner : MonoBehaviour
    {
        static Runner _instance;

        public static Runner Instance
        {
            get
            {
                // Unity's == also reports destroyed objects as null.
                if (_instance == null)
                {
                    // HideAndDontSave keeps it out of FindObjectsOfType and scene teardown.
                    var go = new GameObject("ReceiverMultiplayer.Runner") { hideFlags = HideFlags.HideAndDontSave };
                    DontDestroyOnLoad(go);
                    _instance = go.AddComponent<Runner>();
                }
                return _instance;
            }
        }

        public static void Ensure() => _ = Instance;

        bool _keepingAlive, _savedRunInBackground;

        void Update()
        {
            // Unity stops the whole game when its window loses focus (alt-tab), which silences the network and
            // times everyone out. Keep running in the background for as long as a session exists.
            bool session = NetSession.Current != null;
            if (session != _keepingAlive)
            {
                if (session) { _savedRunInBackground = Application.runInBackground; Application.runInBackground = true; }
                else Application.runInBackground = _savedRunInBackground;
                _keepingAlive = session;
            }

            // The game pauses (time scale 0) while its menu is open, and players host or join from that menu.
            // In a session the world must keep running for everyone, so undo the pause every frame; during a run
            // the time scale is the team's (normal, or the slomo cheat's 10% for everyone).
            if (session) World.CheatSync.HoldTimeScale();
            SteamInvites.Tick();
            Skins.SkinStore.Tick();
            NetSession.TickCurrent(Time.unscaledDeltaTime);
            UI.UpdatePrompt.Frame();
            ModFetch.Tick();
        }

        /// <summary>After every game script has moved the camera: pin camera-relative displays to it.</summary>
        void LateUpdate()
        {
            if (NetSession.Current == null) return;
            try { World.WorldSync.LateFrame(); }
            catch (System.Exception e) { Plugin.Log.LogError($"World sync (late) failed: {e}"); }
        }

        void OnGUI()
        {
            UI.UpdatePrompt.OnGUI(); // every event: its buttons need clicks
            if (Event.current.type != EventType.Repaint) return;
            World.Spectator.OnGUI();
            UI.Notice.OnGUI();
        }

        void OnApplicationQuit()
        {
            NetSession.Current?.Leave();
        }
    }
}
