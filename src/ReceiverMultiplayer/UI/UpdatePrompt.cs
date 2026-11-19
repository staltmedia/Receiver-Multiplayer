using System;
using System.Collections;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using ReceiverMultiplayer.Session;
using UnityEngine;
using UnityEngine.Networking;

namespace ReceiverMultiplayer.UI
{
    /// <summary>
    /// At launch, once: is a newer release out on GitHub? If so (and the player hasn't turned this off), ask
    /// "Update to X?" with OK, Cancel and "Don't show again". OK hands over to the updater (which waits for the game
    /// to close, installs the release and starts the game again) and closes the game; Cancel carries on with this
    /// version. "Don't show again" turns the question off for good: from then on, updating means running the updater
    /// (BepInEx\plugins\ReceiverMultiplayer\ReceiverMultiplayer-Updater.exe), which can also turn it back on.
    /// Only the release number is fetched; nothing about the player is sent.
    /// </summary>
    static class UpdatePrompt
    {
        public const string Repo = "staltmedia/Receiver-Multiplayer";
        public const string UpdaterName = "ReceiverMultiplayer-Updater.exe";
        const string LatestApi = "https://api.github.com/repos/" + Repo + "/releases/latest";
        const string ReleasesPage = "https://github.com/" + Repo + "/releases/latest";

        static bool _checked, _show, _dontShowAgain, _openedMenu;
        static string _latest, _error;
        static GUIStyle _text, _button, _toggle;

        public static void CheckOnce()
        {
            if (_checked) return;
            _checked = true;
            if (!Plugin.AskToUpdate.Value) return;
            Runner.Instance.StartCoroutine(Check());
        }

        static IEnumerator Check()
        {
            using (var req = UnityWebRequest.Get(LatestApi))
            {
                req.SetRequestHeader("User-Agent", "ReceiverMultiplayer-UpdateCheck");
                req.SetRequestHeader("Accept", "application/vnd.github+json");
                req.timeout = 15;
                yield return req.SendWebRequest();
                if (req.result != UnityWebRequest.Result.Success)
                {
                    Plugin.Log.LogInfo($"Couldn't check for a newer version ({req.error}).");
                    yield break;
                }
                var m = Regex.Match(req.downloadHandler.text ?? "", "\"tag_name\"\\s*:\\s*\"v?(\\d+(?:\\.\\d+){1,3})\"");
                if (!m.Success) yield break;
                string latest = m.Groups[1].Value;
                if (!IsNewer(latest, Protocol.ModVersion))
                {
                    Plugin.Log.LogInfo($"Receiver Multiplayer {Protocol.ModVersion} is the latest version.");
                    yield break;
                }
                Plugin.Log.LogInfo($"Receiver Multiplayer {latest} is out (you have {Protocol.ModVersion}); asking whether to update.");
                _latest = latest;
                _show = true;
            }
        }

        public static bool IsNewer(string candidate, string current)
        {
            Version a, b;
            return Version.TryParse(candidate, out a) && Version.TryParse(current, out b) && a > b;
        }

        public static bool Showing => _show;

        /// <summary>Every frame while the question is up: the pointer has to be free to answer it.</summary>
        public static void Frame()
        {
            if (!_show) return;
            if (!_openedMenu)
            {
                var menu = UnityEngine.Object.FindObjectOfType<optionsmenuscript>();
                if (menu != null && FindAim() != null)
                {
                    _openedMenu = true;
                    if (!optionsmenuscript.show_menu) menu.ShowMenu(); // in a level: pause it behind the question
                }
            }
            Cursor.visible = true;
            Cursor.lockState = CursorLockMode.None;
        }

        static AimScript FindAim() => UnityEngine.Object.FindObjectOfType<AimScript>();

        /// <summary>Drawn from the runner's OnGUI for every event (the buttons need clicks, not just repaints).</summary>
        public static void OnGUI()
        {
            if (!_show) return;
            if (_text == null)
            {
                _text = new GUIStyle(GUI.skin.label) { fontSize = 18, wordWrap = true, alignment = TextAnchor.MiddleCenter };
                _button = new GUIStyle(GUI.skin.button) { fontSize = 17 };
                _toggle = new GUIStyle(GUI.skin.toggle) { fontSize = 15 };
            }
            const float w = 440f, h = 190f;
            var rect = new Rect((Screen.width - w) * 0.5f, (Screen.height - h) * 0.5f, w, h);
            GUI.depth = -1000;
            GUI.ModalWindow(0x524D5550, rect, Draw, "Receiver Multiplayer");
        }

        static void Draw(int id)
        {
            GUILayout.Space(8f);
            GUILayout.Label(_error ?? $"Update to {_latest}?", _text, GUILayout.ExpandWidth(true), GUILayout.Height(56f));
            GUILayout.Label($"You have {Protocol.ModVersion}. Players only connect to the same version.", GUI.skin.label);
            _dontShowAgain = GUILayout.Toggle(_dontShowAgain, " Don't show again", _toggle);
            GUILayout.Space(6f);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("OK", _button, GUILayout.Height(34f))) Ok();
            if (GUILayout.Button("Cancel", _button, GUILayout.Height(34f))) Close();
            GUILayout.EndHorizontal();
        }

        static void Remember()
        {
            if (_dontShowAgain) Plugin.AskToUpdate.Value = false; // saved to the config straight away
        }

        static void Ok()
        {
            if (_error != null) { Application.OpenURL(ReleasesPage); Close(); return; }
            Remember();
            try
            {
                string updater = Path.Combine(Path.Combine(BepInEx.Paths.PluginPath, "ReceiverMultiplayer"), UpdaterName);
                if (!File.Exists(updater))
                {
                    _error = $"The updater isn't in BepInEx\\plugins\\ReceiverMultiplayer. OK opens the download page for {_latest}.";
                    return;
                }
                int pid = Process.GetCurrentProcess().Id;
                // No trailing backslash: before the closing quote it would escape it.
                var start = new ProcessStartInfo(updater, $"--update --wait {pid} --game \"{BepInEx.Paths.GameRootPath.TrimEnd('\\', '/')}\"")
                {
                    UseShellExecute = true,
                    WorkingDirectory = Path.GetDirectoryName(updater),
                };
                Process.Start(start);
                Plugin.Log.LogInfo($"Updating to {_latest}: the updater takes over and the game closes.");
                _show = false;
                Application.Quit();
            }
            catch (Exception e)
            {
                _error = $"The updater couldn't start ({e.Message}). OK opens the download page.";
            }
        }

        static void Close()
        {
            Remember();
            _show = false;
            if (_dontShowAgain) Plugin.Log.LogInfo("Update questions turned off; run the updater to update from now on.");
        }
    }
}
