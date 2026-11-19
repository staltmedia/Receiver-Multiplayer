using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using ReceiverMultiplayer.Session;
using UnityEngine;
using UnityEngine.UI;

namespace ReceiverMultiplayer.UI
{
    /// <summary>
    /// Ready list (Ready / Unready, 3-second countdown, host Start Game), per-player starting-gun pick,
    /// and the host's settings and connection options. Lives in the pause menu like the Options window.
    /// </summary>
    sealed class WaitingRoomWindow : MonoBehaviour
    {
        sealed class PlayerRow
        {
            public RectTransform Root;
            public Text Ready, Name, Gun;
            public Dropdown Pick;
            public int PickOptionCount = -1;
            public Button Kick;
        }

        UiKit _kit;
        RectTransform _content, _actions, _players, _hostSection;
        Text _title, _connection, _status, _playersHeader, _settingsSummary, _addresses;
        Button _ready, _start, _newRun, _invite, _leave;
        Toggle _weaponChoice, _friendlyFire, _directIp;
        InputField _seed, _port, _password;
        Dropdown _visibility;
        readonly Dictionary<ushort, PlayerRow> _rows = new Dictionary<ushort, PlayerRow>();
        readonly HashSet<ushort> _seen = new HashSet<ushort>();
        NetSession _bound;
        bool _dirty = true;
        string _cachedAddresses;
        float _addressesAt = -100f;

        static NetSession S => NetSession.Current;

        public static WaitingRoomWindow Create(UiKit kit)
        {
            var win = kit.CreateWindow("MP_WaitingRoom", 440f);
            var w = win.Root.AddComponent<WaitingRoomWindow>();
            w._kit = kit;
            w._content = win.Content;
            w.Build();
            return w;
        }

        void Build()
        {
            var c = _content;
            _title = _kit.Header(c, "Multiplayer");
            _connection = _kit.Label(c, "", 34f);
            _status = _kit.Label(c, "", 34f);

            _actions = UiKit.Row(c, 30f);
            _ready = _kit.Button(_actions, "Ready", ToggleReady, 30f, 130f);
            _start = _kit.Button(_actions, "Start Game", () => S?.StartGame(), 30f, 130f);
            _newRun = _kit.Button(_actions, "New Run", () => S?.NewRun(), 30f, 130f);

            _playersHeader = _kit.Header(c, "Players");
            _players = UiKit.Column(c, "PlayerList");
            _settingsSummary = _kit.Label(c, "", 34f);

            _hostSection = UiKit.Column(c, "HostSettings");
            var h = _hostSection;
            _kit.Header(h, "Host settings");
            _weaponChoice = _kit.Toggle(h, "Allow weapon choice", false, v => S?.SetWeaponChoice(v));
            _friendlyFire = _kit.Toggle(h, "Friendly fire", false, v => S?.SetFriendlyFire(v));
            _kit.Label(h, "The game's own Modifiers (pause menu) apply to everyone: the host's are used.", 34f);
            _seed = _kit.Input(h, "Seed:", "Leave empty for a random seed", "", InputField.ContentType.Standard, 16, v => S?.SetSeedText(v));

            _kit.Header(h, "Connections");
            _visibility = _kit.Dropdown(h, new[] { "Steam: friends only", "Steam: public", "Steam: invite only" }, 0,
                i => S?.SetVisibility((LobbyVisibility)i));
            _invite = _kit.Button(h, "Invite Steam friends", () => S?.InviteSteamFriends());
            _directIp = _kit.Toggle(h, "Allow Direct IP (LAN, Radmin, Tailscale)", false, ApplyDirectIp);
            _port = _kit.Input(h, "Direct IP port:", Protocol.DefaultPort.ToString(), Protocol.DefaultPort.ToString(),
                InputField.ContentType.IntegerNumber, 5, _ => { if (_directIp.isOn) ApplyDirectIp(true); });
            _password = _kit.Input(h, "Direct IP password (optional):", "No password", "",
                InputField.ContentType.Password, 32, _ => { if (_directIp.isOn) ApplyDirectIp(true); });
            _addresses = _kit.Label(h, "", 34f);

            Skins.SkinPanel.Build(c,
                (parent, label, onClick, height) => _kit.Button(parent, label, onClick, height),
                (parent, text, height) => _kit.Label(parent, text, height, -1f, TextAnchor.MiddleCenter));

            _leave = _kit.Button(c, "Leave", () => S?.Leave());
        }

        public void MarkDirty() => _dirty = true;

        void OnEnable() => _dirty = true;

        void Update()
        {
            if (_bound != S)
            {
                if (_bound != null) _bound.Changed -= MarkDirty;
                _bound = S;
                if (_bound != null) _bound.Changed += MarkDirty;
                _dirty = true;
            }
            if (_bound == null)
            {
                gameObject.SetActive(false);
                return;
            }
            if (_bound.Phase == LobbyPhase.Countdown) _dirty = true; // keep the countdown text live
            if (_dirty)
            {
                _dirty = false;
                Refresh(_bound);
            }
        }

        void OnDestroy()
        {
            if (_bound != null) _bound.Changed -= MarkDirty;
        }

        void ToggleReady()
        {
            var s = S;
            var me = s?.Me;
            if (me != null) s.SetReady(!me.Ready);
        }

        void ApplyDirectIp(bool on)
        {
            var s = S;
            if (s == null) return;
            int port = int.TryParse(_port.text, out int p) ? p : Protocol.DefaultPort;
            if (!s.SetDirectIp(on, port, _password.text, out _))
                _directIp.SetIsOnWithoutNotify(false);
            _dirty = true;
        }

        void Refresh(NetSession s)
        {
            bool host = s.IsHost;
            bool joined = s.Joined;
            var me = s.Me;

            _title.text = host ? "Hosting" : joined ? "Multiplayer" : "Connecting";
            _connection.text = ConnectionText(s);
            _status.text = StatusText(s);

            _actions.gameObject.SetActive(joined);
            _ready.gameObject.SetActive(joined && s.Phase != LobbyPhase.InRun);
            UiKit.SetLabel(_ready.gameObject, me != null && me.Ready ? "Unready" : "Ready");
            _start.gameObject.SetActive(host && s.Phase != LobbyPhase.InRun);
            _newRun.gameObject.SetActive(host && s.Phase == LobbyPhase.InRun);

            _playersHeader.gameObject.SetActive(joined);
            _players.gameObject.SetActive(joined);
            _playersHeader.text = $"Players ({s.Players.Count})";
            RefreshRows(s);

            _settingsSummary.gameObject.SetActive(joined && !host);
            _settingsSummary.text = $"Weapon choice {(s.Settings.WeaponChoice ? "on" : "off")}   Friendly fire {(s.Settings.FriendlyFire ? "on" : "off")}   " +
                                    "Modifiers: the host's";

            _hostSection.gameObject.SetActive(host);
            if (host)
            {
                _weaponChoice.SetIsOnWithoutNotify(s.Settings.WeaponChoice);
                _friendlyFire.SetIsOnWithoutNotify(s.Settings.FriendlyFire);
                _visibility.gameObject.SetActive(s.HasSteamLobby);
                _visibility.SetValueWithoutNotify((int)s.Options.Visibility);
                _invite.gameObject.SetActive(s.HasSteamLobby);
                _invite.interactable = s.SteamLobbyReady;
                _directIp.SetIsOnWithoutNotify(s.Options.DirectIp);
                _addresses.gameObject.SetActive(s.Options.DirectIp);
                if (s.Options.DirectIp)
                    _addresses.text = $"Share one of these with players: {LocalAddresses()}  (port {s.Options.Port})";
            }

            UiKit.SetLabel(_leave.gameObject, host ? "Close session" : "Leave");
        }

        void RefreshRows(NetSession s)
        {
            _seen.Clear();
            int index = 0;
            foreach (var p in s.Players)
            {
                _seen.Add(p.Id);
                if (!_rows.TryGetValue(p.Id, out var row))
                    _rows[p.Id] = row = CreateRow(p.Id);
                row.Root.SetSiblingIndex(index++);

                bool mine = p.Id == s.LocalId;
                row.Ready.text = s.Phase == LobbyPhase.InRun ? "" : p.Ready ? "Ready" : "...";
                row.Name.text = p.Name + (p.IsHost ? " (host)" : "") + (mine ? " (you)" : "");

                bool choice = s.Settings.WeaponChoice;
                bool canPick = mine && choice && s.Phase != LobbyPhase.InRun && s.GunNames.Count > 0;
                row.Pick.gameObject.SetActive(canPick);
                if (canPick && row.PickOptionCount != s.GunNames.Count)
                {
                    // Set once: afterwards the local dropdown is the source of truth, so a pending
                    // round-trip never snaps it back.
                    var options = new List<string> { "Default" };
                    options.AddRange(s.GunNames);
                    UiKit.SetOptions(row.Pick, options, p.GunPick + 1);
                    row.PickOptionCount = s.GunNames.Count;
                }
                row.Gun.gameObject.SetActive(choice && !canPick);
                row.Gun.text = p.GunPick >= 0 && p.GunPick < s.GunNames.Count ? s.GunNames[p.GunPick] : "Default";
                row.Kick.gameObject.SetActive(s.IsHost && !mine);
            }

            foreach (var id in _rows.Keys.Where(id => !_seen.Contains(id)).ToList())
            {
                Destroy(_rows[id].Root.gameObject);
                _rows.Remove(id);
            }
        }

        PlayerRow CreateRow(ushort id)
        {
            var r = UiKit.Row(_players, 30f);
            var row = new PlayerRow { Root = r };
            row.Ready = _kit.Label(r, "", 30f, 56f);
            row.Name = _kit.Label(r, "", 30f);
            row.Gun = _kit.Label(r, "", 30f, 140f, TextAnchor.MiddleRight);
            row.Pick = _kit.Dropdown(r, new[] { "Default" }, 0, i => S?.SetGunPick(i - 1), 150f);
            row.Kick = _kit.Button(r, "Kick", () => S?.Kick(id), 30f, 56f);
            return row;
        }

        static string ConnectionText(NetSession s)
        {
            if (!s.IsHost) return s.Joined ? $"Connected via {s.ConnectionKind}." : "";
            string steam = !s.HasSteamLobby ? "Steam isn't running."
                : s.SteamLobbyReady ? "Steam lobby open." : "Opening the Steam lobby...";
            string direct = s.Options.DirectIp ? $"Direct IP on (port {s.Options.Port}{(s.Options.Password.Length > 0 ? ", password set" : "")})." : "Direct IP off.";
            return steam + " " + direct;
        }

        static string StatusText(NetSession s)
        {
            if (!s.Joined) return s.Status;
            string extra = string.IsNullOrEmpty(s.Status) ? "" : "\n" + s.Status;
            switch (s.Phase)
            {
                case LobbyPhase.Countdown:
                    return $"Starting in {Math.Max(1, s.CountdownSeconds)}...";
                case LobbyPhase.InRun:
                    return (s.IsHost ? "Run in progress. New Run brings everyone back here." : "Run in progress.") + extra;
                default:
                    int ready = s.Players.Count(p => p.Ready);
                    return $"{ready}/{s.Players.Count} ready. When everyone is ready, the match starts in 3 seconds." + extra;
            }
        }

        /// <summary>This machine's IPv4 addresses (LAN and VPN adapters), shown only to the host.</summary>
        string LocalAddresses()
        {
            if (_cachedAddresses != null && Time.unscaledTime - _addressesAt < 10f) return _cachedAddresses;
            _addressesAt = Time.unscaledTime;
            try
            {
                var list = NetworkInterface.GetAllNetworkInterfaces()
                    .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                    .SelectMany(n => n.GetIPProperties().UnicastAddresses)
                    .Select(a => a.Address)
                    .Where(a => a.AddressFamily == AddressFamily.InterNetwork)
                    .Select(a => a.ToString())
                    .Distinct()
                    .Take(5)
                    .ToList();
                _cachedAddresses = list.Count > 0 ? string.Join(", ", list) : "none found";
            }
            catch (Exception)
            {
                _cachedAddresses = "unavailable";
            }
            return _cachedAddresses;
        }
    }
}
