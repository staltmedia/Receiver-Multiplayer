using System;
using HarmonyLib;

namespace ReceiverMultiplayer.UI
{
    /// <summary>The pause menu (optionsmenuscript.menu) is the game's only menu; hook it once it has initialised.</summary>
    [HarmonyPatch(typeof(optionsmenuscript), nameof(optionsmenuscript.Start))]
    static class OptionsMenuStartPatch
    {
        static void Postfix(optionsmenuscript __instance)
        {
            try
            {
                MultiplayerMenu.Inject(__instance);
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"Multiplayer menu injection failed: {e}");
            }
        }
    }

    /// <summary>Opening the game's own Options or Modifiers window closes ours so they never overlap.</summary>
    [HarmonyPatch(typeof(optionsmenuscript))]
    static class OptionsMenuWindowTogglePatch
    {
        [HarmonyPostfix]
        [HarmonyPatch(nameof(optionsmenuscript.ToggleOptions))]
        [HarmonyPatch(nameof(optionsmenuscript.ToggleModifierWindow))]
        static void Postfix()
        {
            SessionUi.HideOurs();
        }
    }
}
