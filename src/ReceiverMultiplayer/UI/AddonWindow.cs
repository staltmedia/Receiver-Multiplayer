using System.Collections.Generic;
using System.Linq;
using ReceiverMultiplayer.Session;
using Steamworks;
using UnityEngine;
using UnityEngine.UI;

namespace ReceiverMultiplayer.UI
{
    /// <summary>
    /// The first of the two add-on windows, shown when the host's Steam Workshop add-ons differ from yours: the
    /// ones you're missing (Subscribe to all) and the ones the host doesn't use (Unsubscribe extras, or Leave out
    /// for now, which takes them out of this game until it restarts without changing your subscriptions). Steam
    /// changes show progress; subscribing needs a restart of Receiver before joining. The host keeps the
    /// connection open meanwhile; once nothing differs you join on your own. Non-Workshop mods are in the second
    /// window (<see cref="LocalModWindow"/>). Cancel leaves.
    /// </summary>
    sealed class AddonWindow : MonoBehaviour
    {
        UiKit _kit;
        RectTransform _content, _list;
        Text _status;
        Button _subscribe, _unsubscribe, _leaveOut;
        readonly List<GameObject> _rows = new List<GameObject>();
        List<Addon> _missing = new List<Addon>(), _extra = new List<Addon>();
        float _nextPoll;
        string _note = "";

        public static AddonWindow Create(UiKit kit)
        {
            var win = kit.CreateWindow("MP_Addons", 440f);
            var w = win.Root.AddComponent<AddonWindow>();
            w._kit = kit;
            w._content = win.Content;
            w.Build();
            return w;
        }

        void Build()
        {
            var c = _content;
            _kit.Header(c, "Workshop add-ons don't match");
            _list = UiKit.Column(c, "AddonList");
            _status = _kit.Label(c, "", 68f);
            var row = UiKit.Row(c, 30f);
            _subscribe = _kit.Button(row, "Subscribe to all", OnSubscribe, 30f, 200f);
            _unsubscribe = _kit.Button(row, "Unsubscribe extras", OnUnsubscribe, 30f, 200f);
            var row2 = UiKit.Row(c, 30f);
            _leaveOut = _kit.Button(row2, "Leave extras out for now", OnLeaveOut, 30f, 200f);
            _kit.Button(row2, "Cancel", OnCancel, 30f, 200f);
        }

        public void Show()
        {
            _note = "";
            Refresh();
            gameObject.SetActive(true);
            transform.SetAsLastSibling();
        }

        /// <summary>Rebuild the lists from what still differs (an extra left out drops off).</summary>
        void Refresh()
        {
            foreach (var r in _rows) if (r != null) Destroy(r);
            _rows.Clear();
            AddonGate.Remaining(out var stillMissing, out var stillExtra);
            var missing = _missing = stillMissing.Where(a => a.OnWorkshop).ToList();
            var extra = _extra = stillExtra.Where(a => a.OnWorkshop).ToList();

            if (missing.Count > 0)
            {
                Line("The host uses Workshop add-ons you don't have:", 24f);
                foreach (var a in missing) Line("  • " + a, 22f);
            }
            if (extra.Count > 0)
            {
                Line("You have Workshop add-ons the host doesn't use: unsubscribe, or leave them out of this game until you restart it (your subscriptions stay):", 58f);
                foreach (var a in extra) Line("  • " + a, 22f);
            }
            if (missing.Count == 0 && extra.Count == 0)
                Line(AddonGate.Pending ? "Your Workshop add-ons match the host's now." : "Nothing to change here.", 24f);

            bool steam = SteamAvailable();
            _subscribe.gameObject.SetActive(missing.Count > 0);
            _unsubscribe.gameObject.SetActive(extra.Count > 0);
            _leaveOut.gameObject.SetActive(extra.Count > 0);
            _subscribe.interactable = steam && !AddonDownloads.Started;
            _unsubscribe.interactable = steam && !AddonRemovals.Started;
            _leaveOut.interactable = true;
            UpdateStatus();
        }

        void UpdateStatus()
        {
            bool steam = SteamAvailable();
            bool live = NetSession.Current != null && NetSession.Current.Resolving;
            string text = !steam && (_missing.Count > 0 || _extra.Count > 0)
                ? "Steam isn't running, so Workshop subscriptions can't be changed from here."
                : CombinedStatus();
            if (_missing.Count > 0 && !AddonDownloads.Started) text = Join(text, "Subscribing needs a restart of Receiver before you can join.");
            if (!live && AddonGate.Pending) text = Join(text, "The connection to the host has closed: join again when you're ready.");
            _status.text = Join(text, _note);
        }

        static string Join(string a, string b) => string.IsNullOrEmpty(a) ? b ?? "" : string.IsNullOrEmpty(b) ? a : a + "\n" + b;

        static string CombinedStatus()
        {
            var parts = new List<string>();
            if (AddonDownloads.Started) parts.Add(AddonDownloads.Status());
            if (AddonRemovals.Started) parts.Add(AddonRemovals.Status());
            return string.Join("\n", parts);
        }

        /// <summary>The connection closed (or the windows came back after it did): say so.</summary>
        public void OnConnectionClosed()
        {
            if (gameObject.activeSelf) Refresh();
        }

        void OnLeaveOut()
        {
            int n = 0;
            foreach (var a in _extra) n += Addons.Unload(a);
            if (n > 0) Addons.RefreshGunList();
            _note = n > 0 ? "Left out until you restart Receiver." : "";
            Refresh();
            if (AddonGate.TryJoin()) _note = "Joining…";
            Refresh();
            SessionUi.AddonsChanged();
        }

        void Line(string text, float height)
        {
            var t = _kit.Label(_list, text, height);
            _rows.Add(t.gameObject);
        }

        static bool SteamAvailable() => Net.SteamP2PTransport.Available;

        void OnSubscribe()
        {
            var ids = _missing.Where(a => a.OnWorkshop).Select(a => a.WorkshopId).ToList();
            if (ids.Count == 0) return;
            bool turnedOn = false;
            if (!ModManager.IsModsEnabled())
            {
                PlayerPrefs.SetInt("mods_enabled", 1); // the add-ons won't load with mods turned off
                PlayerPrefs.Save();
                turnedOn = true;
            }
            AddonDownloads.Start(ids, turnedOn);
            _subscribe.interactable = false;
            _status.text = CombinedStatus();
        }

        void OnUnsubscribe()
        {
            var ids = _extra.Where(a => a.OnWorkshop).Select(a => a.WorkshopId).ToList();
            if (ids.Count == 0) return;
            AddonRemovals.Start(ids);
            _unsubscribe.interactable = false;
            _status.text = CombinedStatus();
        }

        void OnCancel() => SessionUi.CancelAddons();

        void Update()
        {
            if (Time.unscaledTime < _nextPoll) return;
            _nextPoll = Time.unscaledTime + 0.5f;
            if (AddonDownloads.Started || AddonRemovals.Started) UpdateStatus();
        }

        /// <summary>The other window changed something (a mod loaded or left out): show what's left.</summary>
        public void OnAddonsChanged()
        {
            if (gameObject.activeSelf) Refresh();
        }
    }

    /// <summary>Unsubscribing from Workshop add-ons the host doesn't use.</summary>
    static class AddonRemovals
    {
        static readonly List<ulong> _ids = new List<ulong>();
        static readonly List<CallResult<RemoteStorageUnsubscribePublishedFileResult_t>> _calls = new List<CallResult<RemoteStorageUnsubscribePublishedFileResult_t>>();
        static int _done, _failed;

        public static bool Started => _ids.Count > 0;

        public static void Start(List<ulong> ids)
        {
            _ids.Clear();
            _calls.Clear();
            _done = _failed = 0;
            foreach (var id in ids.Distinct())
            {
                _ids.Add(id);
                var call = CallResult<RemoteStorageUnsubscribePublishedFileResult_t>.Create((result, ioFailure) =>
                {
                    if (ioFailure || result.m_eResult != EResult.k_EResultOK)
                    {
                        _failed++;
                        Plugin.Log.LogWarning($"Unsubscribing from Workshop item {id} failed: {(ioFailure ? "no response" : result.m_eResult.ToString())}.");
                        return;
                    }
                    _done++;
                });
                call.Set(SteamUGC.UnsubscribeItem(new PublishedFileId_t(id)));
                _calls.Add(call);
            }
            Plugin.Log.LogInfo($"Unsubscribing from {_ids.Count} Workshop add-on(s) the host doesn't use.");
        }

        public static string Status()
        {
            if (_done + _failed < _ids.Count) return $"Unsubscribing: {_done} of {_ids.Count} done…";
            if (_failed > 0) return $"{_failed} add-on(s) couldn't be unsubscribed; {_done} done. Restart Receiver, then join again.";
            return $"Unsubscribed from {_done} add-on(s). Restart Receiver (it still has them loaded), then join again.";
        }
    }

    /// <summary>Subscribing to Workshop items and following their downloads until installed.</summary>
    static class AddonDownloads
    {
        static readonly List<ulong> _ids = new List<ulong>();
        static readonly List<CallResult<RemoteStorageSubscribePublishedFileResult_t>> _calls = new List<CallResult<RemoteStorageSubscribePublishedFileResult_t>>();
        static int _failed;
        static bool _turnedOnMods;

        public static bool Started => _ids.Count > 0;

        public static void Start(List<ulong> ids, bool turnedOnMods)
        {
            _ids.Clear();
            _calls.Clear();
            _failed = 0;
            _turnedOnMods = turnedOnMods;
            foreach (var id in ids.Distinct())
            {
                _ids.Add(id);
                var file = new PublishedFileId_t(id);
                var call = CallResult<RemoteStorageSubscribePublishedFileResult_t>.Create((result, ioFailure) =>
                {
                    if (ioFailure || result.m_eResult != EResult.k_EResultOK)
                    {
                        _failed++;
                        Plugin.Log.LogWarning($"Subscribing to Workshop item {id} failed: {(ioFailure ? "no response" : result.m_eResult.ToString())}.");
                        return;
                    }
                    SteamUGC.DownloadItem(file, true); // start now, ahead of other downloads
                });
                call.Set(SteamUGC.SubscribeItem(file));
                _calls.Add(call);
            }
            Plugin.Log.LogInfo($"Subscribing to {_ids.Count} Workshop add-on(s).");
        }

        public static string Status()
        {
            int ready = 0;
            ulong done = 0, total = 0;
            foreach (var id in _ids)
            {
                var file = new PublishedFileId_t(id);
                var state = (EItemState)SteamUGC.GetItemState(file);
                bool busy = (state & (EItemState.k_EItemStateNeedsUpdate | EItemState.k_EItemStateDownloading | EItemState.k_EItemStateDownloadPending)) != 0;
                if ((state & EItemState.k_EItemStateInstalled) != 0 && !busy) { ready++; continue; }
                if (SteamUGC.GetItemDownloadInfo(file, out ulong d, out ulong t)) { done += d; total += t; }
            }
            if (_failed > 0 && ready + _failed >= _ids.Count)
                return $"{_failed} add-on(s) couldn't be subscribed to (removed from the Workshop, or private). {ready} installed.";
            if (ready < _ids.Count)
            {
                string mb = total > 0 ? $" ({done / 1048576f:0.0} / {total / 1048576f:0.0} MB)" : "";
                return $"Downloading add-ons: {ready} of {_ids.Count} ready{mb}…";
            }
            return "All add-ons are installed. Restart Receiver to load them, then join again."
                   + (_turnedOnMods ? " (Mods were turned on in your settings.)" : "");
        }
    }
}
