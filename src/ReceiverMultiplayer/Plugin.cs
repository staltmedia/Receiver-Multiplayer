using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;

namespace ReceiverMultiplayer
{
    [BepInPlugin(MyPluginInfo.PLUGIN_GUID, MyPluginInfo.PLUGIN_NAME, MyPluginInfo.PLUGIN_VERSION)]
    [BepInProcess("Receiver.exe")]
    public sealed class Plugin : BaseUnityPlugin
    {
        internal static ManualLogSource Log;
        internal static ConfigEntry<string> PlayerName;
        internal static ConfigEntry<bool> ShowOtherSkins;
        internal static ConfigEntry<bool> AskToUpdate;

        void Awake()
        {
            Log = Logger;

            AskToUpdate = Config.Bind(
                "Updates", "AskToUpdate", true,
                "When a newer version is out, ask on launch whether to update. Off: run ReceiverMultiplayer-Updater.exe (in BepInEx\\plugins\\ReceiverMultiplayer) to update.");
            PlayerName = Config.Bind(
                "Multiplayer", "PlayerName", "Player",
                "Name shown in the waiting room when Steam isn't running (Direct IP only). With Steam, your Steam name is used.");
            ShowOtherSkins = Config.Bind(
                "Multiplayer", "ShowOtherSkins", true,
                "Show the skin images other players chose on their bodies. Off: everyone appears as the plain pill.");

            ApplyPatches();
            Session.ModifierSync.RecoverAfterCrash();
            Skins.SkinStore.Load();
            UI.SessionUi.Init();
            Runner.Ensure();
            UI.UpdatePrompt.CheckOnce();
            Log.LogInfo($"{MyPluginInfo.PLUGIN_NAME} {MyPluginInfo.PLUGIN_VERSION} loaded");
        }

        /// <summary>Patch class by class, so one patch that no longer fits the game is logged and skipped instead of disabling the mod.</summary>
        static void ApplyPatches()
        {
            var harmony = new Harmony(MyPluginInfo.PLUGIN_GUID);
            int ok = 0, failed = 0;
            foreach (var type in AccessTools.GetTypesFromAssembly(typeof(Plugin).Assembly))
            {
                if (!type.IsDefined(typeof(HarmonyPatch), false)) continue;
                try
                {
                    harmony.CreateClassProcessor(type).Patch();
                    ok++;
                }
                catch (System.Exception e)
                {
                    failed++;
                    Log.LogError($"Patch {type.FullName} failed: {e.GetBaseException().Message}");
                }
            }
            Log.LogInfo($"Applied {ok} patch classes" + (failed > 0 ? $", {failed} failed." : "."));
        }
    }
}
