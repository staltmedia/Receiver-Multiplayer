using HarmonyLib;
using UnityEngine;

namespace ReceiverMultiplayer.Session
{
    /// <summary>Single-player behaviours that would break a shared session.</summary>
    static class SessionPatches
    {
        static bool InRun => NetSession.Current != null && NetSession.Current.Phase == LobbyPhase.InRun;

        /// <summary>Multiplayer can't pause: opening the menu keeps the world running.</summary>
        [HarmonyPatch(typeof(optionsmenuscript), nameof(optionsmenuscript.ShowMenu))]
        static class NoPause
        {
            static void Postfix()
            {
                if (NetSession.Current != null) Time.timeScale = 1f;
            }
        }

        /// <summary>
        /// The pause menu's Scene Loading buttons (Load Apartments, Load Gun Range) and Restart, in a session.
        /// The host's choice takes everyone: a fresh shared run in that scene. Restart during a run restarts the
        /// scene everyone is in. A client can't move the group: during a run their buttons only show a notice
        /// (in the waiting room they still load their own scene, until the host starts).
        /// </summary>
        [HarmonyPatch(typeof(optionsmenuscript))]
        static class SharedSceneChange
        {
            [HarmonyPrefix]
            [HarmonyPatch(nameof(optionsmenuscript.OpenScene))]
            static bool OpenScene(string scene_name) => Route(scene_name, restart: false);

            [HarmonyPrefix]
            [HarmonyPatch(nameof(optionsmenuscript.ReopenCurrentScene))]
            static bool Restart() => Route(null, restart: true);

            static bool Route(string scene, bool restart)
            {
                var s = NetSession.Current;
                if (s == null || RunStarter.Loading) return true; // not in a session: the game's own behaviour
                bool inRun = s.Phase == LobbyPhase.InRun;

                if (!s.IsHost)
                {
                    if (!inRun) return true;
                    UI.Notice.Show("Only the host can change the level during a multiplayer run.");
                    Plugin.Log.LogInfo("Scene change ignored: only the host can change the level during a run.");
                    return false;
                }

                if (restart)
                {
                    if (!inRun) return true; // waiting room: the host's own level isn't shared yet
                    scene = s.RunScene;
                }
                if (!SceneCatalog.IsAllowed(scene))
                {
                    Plugin.Log.LogWarning($"Scene '{scene}' can't be shared; loading it is disabled in a session.");
                    UI.Notice.Show("That level can't be loaded in a multiplayer session.");
                    return false;
                }
                Plugin.Log.LogInfo($"Host {(restart ? "restarted" : "loaded")} {SceneCatalog.DisplayName(scene)}: taking everyone there.");
                s.StartGame(scene);
                return false;
            }
        }

        [HarmonyPatch(typeof(AimScript), "UpdateLevelResetButton")]
        static class NoLocalReset
        {
            static bool Prefix() => !InRun;
        }

        [HarmonyPatch(typeof(AimScript), nameof(AimScript.Start))]
        static class PlayerSpawned
        {
            static void Postfix(AimScript __instance)
            {
                Runner.Ensure();
                if (__instance.main_client_control)
                {
                    World.LocalPlayer.Set(__instance);
                    RunStarter.OnPlayerSpawned(__instance);
                }
            }
        }
    }
}
