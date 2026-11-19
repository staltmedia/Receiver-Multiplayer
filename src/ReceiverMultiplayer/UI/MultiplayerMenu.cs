using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.UI;

namespace ReceiverMultiplayer.UI
{
    /// <summary>
    /// Adds a "Multiplayer" button to the pause menu by cloning the game's own Resume button,
    /// so it inherits the existing font, sprites, colours, hover states and sounds.
    /// The menu's buttons are hand-placed in a diagonal staircase with no layout group, so the new
    /// button goes beside Resume rather than into the column (which would push Exit off-screen).
    /// </summary>
    static class MultiplayerMenu
    {
        internal const string ButtonName = "MP_MultiplayerButton";
        const string Label = "Multiplayer";
        const float GapBesideResume = 10f;

        // Persistent onClick targets that identify the pause menu's main button column.
        static readonly HashSet<string> MainMenuActions = new HashSet<string>
        {
            "ToggleOptions", "ToggleModifierWindow", "ToggleModUI", "OpenModsFolder",
            "HideMenu", "ReopenCurrentScene", "OpenScene", "ExitGame",
        };


        public static void Inject(optionsmenuscript menuScript)
        {
            var menu = menuScript.menu;
            if (menu == null)
            {
                Plugin.Log.LogWarning("Pause menu root is missing; Multiplayer button not added.");
                return;
            }

            var template = FindTemplateButton(menuScript);
            if (template == null)
            {
                Plugin.Log.LogWarning("No pause-menu button to copy; Multiplayer button not added.");
                return;
            }

            Runner.Ensure();
            var column = template.transform.parent;
            if (column.Find(ButtonName) != null)
                return;

            var window = MultiplayerWindow.Create(menuScript, template);
            var kit = UiKit.Create(menuScript, template, MultiplayerWindow.WindowParent(menuScript));
            SessionUi.Attach(menuScript, kit, window);
            var button = UiFactory.CloneButton(template, column, ButtonName, Label, SessionUi.OnMultiplayerButton);
            UiFactory.PlaceRightOf((RectTransform)button.transform, (RectTransform)template.transform, GapBesideResume);

        }

        /// <summary>
        /// Prefer the Resume button (first row); then Options; then any button wired to a known
        /// pause-menu action. Buttons inside the Options/Modifiers/ad windows are never picked.
        /// </summary>
        static Button FindTemplateButton(optionsmenuscript menuScript)
        {
            var excluded = new[] { menuScript.menuOptions, menuScript.menuModifiers, menuScript.adContent }
                .Where(go => go != null)
                .Select(go => go.transform)
                .ToArray();

            var candidates = menuScript.menu.GetComponentsInChildren<Button>(true)
                .Where(b => b.name != ButtonName && !excluded.Any(ex => b.transform.IsChildOf(ex)))
                .ToList();

            return candidates.FirstOrDefault(b => HasPersistentCall(b, "HideMenu"))
                ?? candidates.FirstOrDefault(b => HasPersistentCall(b, "ToggleOptions"))
                ?? candidates.FirstOrDefault(b => PersistentCalls(b).Any(MainMenuActions.Contains))
                ?? candidates.FirstOrDefault();
        }

        static bool HasPersistentCall(Button b, string method) => PersistentCalls(b).Contains(method);

        internal static IEnumerable<string> PersistentCalls(Button b)
        {
            var e = b.onClick;
            for (int i = 0; i < e.GetPersistentEventCount(); i++)
                yield return e.GetPersistentMethodName(i);
        }

        /// <summary>Like <see cref="PersistentCalls"/>, with any string argument: <c>OpenScene("range")</c>.</summary>
        internal static IEnumerable<string> PersistentCallsWithArgs(Button b)
        {
            var e = b.onClick;
            for (int i = 0; i < e.GetPersistentEventCount(); i++)
            {
                var arg = Session.SceneCatalog.StringArgument(e, i);
                yield return e.GetPersistentMethodName(i) + (string.IsNullOrEmpty(arg) ? "" : $"(\"{arg}\")");
            }
        }
    }
}
