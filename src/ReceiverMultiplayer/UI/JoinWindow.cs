using System;
using System.Globalization;
using ReceiverMultiplayer.Net;
using ReceiverMultiplayer.Session;
using UnityEngine;
using UnityEngine.UI;

namespace ReceiverMultiplayer.UI
{
    /// <summary>Server list (Steam public lobbies, friends' lobbies, LAN hosts) plus a Direct Connect field.</summary>
    sealed class JoinWindow : MonoBehaviour
    {
        UiKit _kit;
        RectTransform _list;
        Text _status;
        InputField _address, _password;
        SteamLobbyBrowser _steam;
        LanDiscovery _lan;
        bool _dirty;

        public static JoinWindow Create(UiKit kit)
        {
            var win = kit.CreateWindow("MP_Join", 440f);
            var w = win.Root.AddComponent<JoinWindow>();
            w._kit = kit;
            w.Build(win.Content);
            return w;
        }

        void Build(RectTransform c)
        {
            _kit.Header(c, "Join Game");
            _status = _kit.Label(c, "", 34f);
            _kit.Button(c, "Refresh", Refresh);
            _kit.Header(c, "Games");
            _list = UiKit.Column(c, "GameList");
            _kit.Header(c, "Direct connect");
            _address = _kit.Input(c, "Address:", "IP or IP:port", "", InputField.ContentType.Standard, 64, _ => { });
            _password = _kit.Input(c, "Password (if the host set one):", "No password", "", InputField.ContentType.Password, 32, _ => { });
            _kit.Button(c, "Connect", ConnectDirect);
            _kit.Button(c, "Back", () => SessionUi.OpenMultiplayer());
        }

        public void SetStatus(string text)
        {
            if (_status != null) _status.text = text ?? "";
        }

        void OnEnable()
        {
            if (_steam == null && SteamP2PTransport.Available)
            {
                _steam = new SteamLobbyBrowser();
                _steam.Updated += () => _dirty = true;
            }
            if (_lan == null)
            {
                try
                {
                    _lan = new LanDiscovery();
                    _lan.Updated += () => _dirty = true;
                }
                catch (Exception e)
                {
                    Plugin.Log.LogWarning("LAN discovery unavailable: " + e.Message);
                }
            }
            Refresh();
        }

        void OnDestroy()
        {
            _steam?.Dispose();
            _lan?.Dispose();
        }

        void Refresh()
        {
            _steam?.Refresh();
            _lan?.Probe(Protocol.DefaultPort);
            _dirty = true;
        }

        void Update()
        {
            _lan?.Poll();
            if (!_dirty) return;
            _dirty = false;
            Rebuild();
        }

        void Rebuild()
        {
            for (int i = _list.childCount - 1; i >= 0; i--)
                Destroy(_list.GetChild(i).gameObject);

            int count = 0;
            if (_steam != null)
                foreach (var e in _steam.Results)
                {
                    var lobby = e.Lobby;
                    AddRow(e.Name + (e.Friend ? " (friend)" : ""), $"{e.Members} in game  {e.State}", () => Join(lobby));
                    count++;
                }
            if (_lan != null)
                foreach (var e in _lan.Results)
                {
                    var ep = e.EndPoint;
                    AddRow(e.Name + " (LAN)", $"{e.Players} in game{(e.HasPassword ? "  password" : "")}",
                        () => JoinDirect(ep.Address.ToString(), ep.Port));
                    count++;
                }

            if (count == 0)
                _kit.Label(_list, _steam != null && _steam.Busy ? "Searching..." :
                    "No games found. Ask a friend to invite you on Steam, or use Direct connect with the host's address.", 48f);

            if (!SteamP2PTransport.Available)
                SetStatus("Steam isn't running: only LAN and Direct connect are available.");
        }

        void AddRow(string name, string detail, Action join)
        {
            var row = UiKit.Row(_list, 30f);
            _kit.Label(row, name, 30f);
            _kit.Label(row, detail, 30f, 130f, TextAnchor.MiddleRight);
            _kit.Button(row, "Join", () => join(), 30f, 60f);
        }

        void Join(Steamworks.CSteamID lobby)
        {
            SetStatus("Joining...");
            NetSession.JoinSteam(lobby);
        }

        void JoinDirect(string host, int port)
        {
            SetStatus($"Connecting to {host}:{port}...");
            NetSession.JoinDirect(host, port, _password.text);
        }

        void ConnectDirect()
        {
            if (!TryParseAddress(_address.text, out string host, out int port))
            {
                SetStatus("Enter an address like 192.168.1.20 or 192.168.1.20:27777.");
                return;
            }
            JoinDirect(host, port);
        }

        /// <summary>Accepts host, host:port, [ipv6]:port and bare IPv6.</summary>
        static bool TryParseAddress(string text, out string host, out int port)
        {
            host = (text ?? "").Trim();
            port = Protocol.DefaultPort;
            if (host.Length == 0) return false;

            if (host.StartsWith("["))
            {
                int close = host.IndexOf(']');
                if (close < 0) return false;
                string rest = host.Substring(close + 1);
                host = host.Substring(1, close - 1);
                if (rest.StartsWith(":") && !int.TryParse(rest.Substring(1), NumberStyles.None, CultureInfo.InvariantCulture, out port)) return false;
            }
            else if (host.IndexOf(':') == host.LastIndexOf(':') && host.Contains(":"))
            {
                int colon = host.LastIndexOf(':');
                if (!int.TryParse(host.Substring(colon + 1), NumberStyles.None, CultureInfo.InvariantCulture, out port)) return false;
                host = host.Substring(0, colon);
            }
            return host.Length > 0 && port > 0 && port <= 65535;
        }
    }
}
