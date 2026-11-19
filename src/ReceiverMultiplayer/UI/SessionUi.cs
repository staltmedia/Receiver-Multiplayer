using ReceiverMultiplayer.Net;
using ReceiverMultiplayer.Session;

namespace ReceiverMultiplayer.UI
{
    /// <summary>
    /// Which multiplayer window is showing, for the current pause menu. The menu (and our windows with it)
    /// is rebuilt on every scene load; <see cref="Attach"/> rebinds to the new one.
    /// </summary>
    static class SessionUi
    {
        static optionsmenuscript _menu;
        static UiKit _kit;
        static MultiplayerWindow _multiplayer;
        static WaitingRoomWindow _room;
        static JoinWindow _join;
        static AddonWindow _addons;
        static LocalModWindow _localMods;

        static bool _showRoomWhenMenuExists;
        static string _lastEndReason = "";

        public static void Init()
        {
            NetSession.Started += OnSessionStarted;
            NetSession.Ended += OnSessionEnded;
            SteamInvites.JoinRequested += lobby =>
            {
                Plugin.Log.LogInfo("Joining a Steam lobby from an invite.");
                NetSession.JoinSteam(lobby);
            };
        }

        static bool Alive => _menu != null && _kit != null;

        public static void Attach(optionsmenuscript menu, UiKit kit, MultiplayerWindow multiplayer)
        {
            _menu = menu;
            _kit = kit;
            _multiplayer = multiplayer;
            _room = null;
            _join = null;
            _addons = null;
            _localMods = null;
            if (_showRoomWhenMenuExists && NetSession.Current != null)
            {
                _showRoomWhenMenuExists = false;
                OpenRoom(true);
            }
        }

        static void OnSessionStarted(NetSession session)
        {
            _lastEndReason = "";
            session.ReturnedToWaitingRoom += () => OpenRoom(true);
            session.AddonsDiffer += ShowAddonWindows;
            session.JoinedAfterAddons += () =>
            {
                HideAddonWindows();
                OpenRoom(true);
            };
            OpenRoom(true);
        }

        static void OnSessionEnded(string reason)
        {
            _lastEndReason = reason;
            if (!Alive) return;
            if (AddonGate.Pending && _kit.IsComplete)
            {
                // Refused over add-ons and the connection has closed meanwhile (or before the windows could open):
                // keep (or bring back) the add-on windows, which now say the connection closed.
                if (!AddonWindowsOpen) ShowAddonWindows();
                _addons?.OnConnectionClosed();
                _localMods?.OnConnectionClosed();
                return;
            }
            bool roomOpen = _room != null && _room.gameObject.activeSelf;
            bool joinOpen = _join != null && _join.gameObject.activeSelf;
            if (roomOpen || joinOpen)
            {
                OpenJoinOrMultiplayer(reason);
                return;
            }
            // Dropped mid-game: bring the menu up so the player sees why.
            if (!optionsmenuscript.show_menu) _menu.ShowMenu();
            OpenMultiplayer();
        }

        static void OpenJoinOrMultiplayer(string reason)
        {
            HideAll();
            if (_join != null && reason != null && !reason.StartsWith("You "))
            {
                _join.gameObject.SetActive(true);
                _join.SetStatus(reason);
            }
            else
            {
                _multiplayer.Show(reason);
            }
        }

        /// <summary>The pause-menu Multiplayer button.</summary>
        public static void OnMultiplayerButton()
        {
            if (!Alive) return;
            if (AnyOpen())
            {
                HideOurs();
                return;
            }
            OpenMultiplayer();
        }

        public static void OpenMultiplayer()
        {
            if (!Alive) return;
            if (NetSession.Current != null && NetSession.Current.Resolving && _kit.IsComplete)
            {
                ShowAddonWindows(); // still sorting out add-ons with the host
                return;
            }
            if (NetSession.Current != null)
            {
                OpenRoom(false);
                return;
            }
            HideAll();
            _multiplayer.Show(_lastEndReason);
        }

        // ---------------- add-on windows ----------------

        static bool AddonWindowsOpen =>
            (_addons != null && _addons.gameObject.activeSelf) || (_localMods != null && _localMods.gameObject.activeSelf);

        /// <summary>
        /// The host's add-ons differ: the Workshop window, the non-Workshop window, or both side by side. The host
        /// keeps the connection open meanwhile (closing the windows doesn't leave; Cancel or the waiting room does).
        /// </summary>
        static void ShowAddonWindows()
        {
            if (!Alive) return;
            if (!_kit.IsComplete)
            {
                NetSession.Current?.Leave();
                _multiplayer.Show("Your add-ons don't match the host's (the add-on windows couldn't be built).");
                return;
            }
            if (!ModFetch.Busy) ModFetch.Clear();
            if (!optionsmenuscript.show_menu) _menu.ShowMenu();
            HideAll();
            bool workshop = AddonGate.AnyWorkshop, local = AddonGate.AnyLocal;
            if (!workshop && !local) workshop = true; // nothing listed (shouldn't happen): the Workshop window says so
            if (workshop)
            {
                if (_addons == null) _addons = AddonWindow.Create(_kit);
                _addons.Show();
            }
            if (local)
            {
                if (_localMods == null) _localMods = LocalModWindow.Create(_kit);
                _localMods.Show();
            }
            // One window sits where the game's dialogs do (right of the menu buttons). Two side by side: the
            // non-Workshop one there, the Workshop one to its left, nudged so the pair stays on screen.
            if (_addons != null) Place(_addons.transform, 0f);
            if (_localMods != null) Place(_localMods.transform, 0f);
            if (workshop && local)
            {
                var left = (UnityEngine.RectTransform)_addons.transform;
                var right = (UnityEngine.RectTransform)_localMods.transform;
                Place(left, -(left.rect.width + 12f));
                KeepOnScreen(left, right);
            }
        }

        static readonly System.Collections.Generic.Dictionary<UnityEngine.Transform, float> _baseX = new System.Collections.Generic.Dictionary<UnityEngine.Transform, float>();

        static void Place(UnityEngine.Transform t, float dx)
        {
            var rt = (UnityEngine.RectTransform)t;
            if (!_baseX.TryGetValue(t, out float x)) _baseX[t] = x = rt.anchoredPosition.x;
            rt.anchoredPosition = new UnityEngine.Vector2(x + dx, rt.anchoredPosition.y);
        }

        /// <summary>Shift both windows sideways (in screen space) so neither runs off an edge, if they fit at all.</summary>
        static void KeepOnScreen(UnityEngine.RectTransform left, UnityEngine.RectTransform right)
        {
            var canvas = left.GetComponentInParent<UnityEngine.Canvas>();
            var cam = canvas != null && canvas.renderMode != UnityEngine.RenderMode.ScreenSpaceOverlay ? canvas.worldCamera : null;
            var a = new UnityEngine.Vector3[4];
            var b = new UnityEngine.Vector3[4];
            left.GetWorldCorners(a);
            right.GetWorldCorners(b);
            float minX = UnityEngine.RectTransformUtility.WorldToScreenPoint(cam, a[0]).x;
            float maxX = UnityEngine.RectTransformUtility.WorldToScreenPoint(cam, b[2]).x;
            float margin = UnityEngine.Screen.width * 0.01f;
            float shift = 0f;
            if (maxX > UnityEngine.Screen.width - margin) shift = UnityEngine.Screen.width - margin - maxX;
            if (minX + shift < margin) shift = margin - minX; // too wide for the screen: keep the left one whole
            if (UnityEngine.Mathf.Abs(shift) < 0.5f) return;
            float scale = canvas != null ? canvas.scaleFactor : 1f;
            foreach (var rt in new[] { left, right })
                rt.anchoredPosition += new UnityEngine.Vector2(shift / UnityEngine.Mathf.Max(0.01f, scale), 0f);
        }

        static void HideAddonWindows()
        {
            if (_addons != null) _addons.gameObject.SetActive(false);
            if (_localMods != null) _localMods.gameObject.SetActive(false);
        }

        /// <summary>Cancel in either add-on window: stop, leave the session, and go back to Multiplayer.</summary>
        public static void CancelAddons()
        {
            ModFetch.Clear();
            AddonGate.Clear();
            HideAddonWindows();
            var s = NetSession.Current;
            if (s != null && !s.Joined) s.Leave();
            OpenMultiplayer();
        }

        /// <summary>One add-on window changed what's loaded: the other shows what's left.</summary>
        public static void AddonsChanged()
        {
            _addons?.OnAddonsChanged();
            _localMods?.OnAddonsChanged();
        }

        public static void OpenRoom(bool openMenu)
        {
            if (!Alive || !_kit.IsComplete)
            {
                _showRoomWhenMenuExists = true;
                return;
            }
            if (openMenu && !optionsmenuscript.show_menu) _menu.ShowMenu();
            HideAll();
            if (_room == null) _room = WaitingRoomWindow.Create(_kit);
            _room.gameObject.SetActive(true);
            _room.transform.SetAsLastSibling();
            _room.MarkDirty();
        }

        public static void OpenJoin()
        {
            if (!Alive) return;
            if (!_kit.IsComplete)
            {
                _multiplayer.Show("The game's menu widgets weren't found, so the Join screen can't be built.");
                return;
            }
            HideAll();
            if (_join == null) _join = JoinWindow.Create(_kit);
            _join.gameObject.SetActive(true);
            _join.transform.SetAsLastSibling();
            _join.SetStatus("");
        }

        static bool AnyOpen() =>
            (_multiplayer != null && _multiplayer.gameObject.activeSelf)
            || (_room != null && _room.gameObject.activeSelf)
            || (_join != null && _join.gameObject.activeSelf)
            || AddonWindowsOpen;

        /// <summary>Hide our windows only (the game opened one of its own).</summary>
        public static void HideOurs()
        {
            if (_multiplayer != null) _multiplayer.Hide();
            if (_room != null) _room.gameObject.SetActive(false);
            if (_join != null) _join.gameObject.SetActive(false);
            HideAddonWindows();
        }

        static void HideAll()
        {
            HideOurs();
            if (_menu == null) return;
            if (_menu.menuOptions != null) _menu.menuOptions.SetActive(false);
            if (_menu.menuModifiers != null) _menu.menuModifiers.SetActive(false);
        }
    }
}
