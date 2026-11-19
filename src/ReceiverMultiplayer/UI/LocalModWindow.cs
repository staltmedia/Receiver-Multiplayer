using System.Collections.Generic;
using System.Linq;
using ReceiverMultiplayer.Session;
using UnityEngine;
using UnityEngine.UI;

namespace ReceiverMultiplayer.UI
{
    /// <summary>
    /// The second add-on window, for mods that aren't on the Steam Workshop. It lists what the host has that
    /// you don't (with sizes), mods where the host has another version, and mods the host doesn't have, and
    /// fixes all of it with one button: the host's copies are downloaded into a cache of their own (your Mods
    /// folder isn't touched) and loaded, yours that the host lacks are left out, both until you restart
    /// Receiver. Then, if nothing else differs, you join. Cancel leaves.
    /// </summary>
    sealed class LocalModWindow : MonoBehaviour
    {
        UiKit _kit;
        RectTransform _content, _list;
        Text _status;
        Button _go;
        readonly List<GameObject> _rows = new List<GameObject>();
        List<Addon> _missing = new List<Addon>(), _extra = new List<Addon>();
        float _nextPoll;

        public static LocalModWindow Create(UiKit kit)
        {
            var win = kit.CreateWindow("MP_LocalMods", 440f);
            var w = win.Root.AddComponent<LocalModWindow>();
            w._kit = kit;
            w._content = win.Content;
            w.Build();
            return w;
        }

        void Build()
        {
            var c = _content;
            _kit.Header(c, "Other mods don't match");
            _kit.Label(c, "These aren't on the Steam Workshop, so they come from the host. Downloads go to a folder of their own, not your Mods folder, and are used until you restart Receiver. Only take mods from hosts you trust.", 92f);
            _list = UiKit.Column(c, "LocalModList");
            _status = _kit.Label(c, "", 58f);
            var row = UiKit.Row(c, 30f);
            _go = _kit.Button(row, "Download and join", OnGo, 30f, 270f);
            _kit.Button(row, "Cancel", OnCancel, 30f, 130f);
        }

        public void Show()
        {
            Refresh();
            gameObject.SetActive(true);
            transform.SetAsLastSibling();
        }

        void Refresh()
        {
            foreach (var r in _rows) if (r != null) Destroy(r);
            _rows.Clear();
            AddonGate.Remaining(out var stillMissing, out var stillExtra);
            _missing = stillMissing.Where(a => !a.OnWorkshop).ToList();
            _extra = stillExtra.Where(a => !a.OnWorkshop).ToList();
            var extraFolders = new HashSet<string>(_extra.Select(a => a.Folder.ToLowerInvariant()));
            var missingFolders = new HashSet<string>(_missing.Select(a => a.Folder.ToLowerInvariant()));
            var fresh = _missing.Where(a => !extraFolders.Contains(a.Folder.ToLowerInvariant())).ToList();
            var replaced = _missing.Where(a => extraFolders.Contains(a.Folder.ToLowerInvariant())).ToList();
            var leftOut = _extra.Where(a => !missingFolders.Contains(a.Folder.ToLowerInvariant())).ToList();

            long fetch = ModFetch.BytesToFetch(_missing);
            if (fresh.Count > 0)
            {
                Line("The host has mods you don't" + (fetch > 0 ? $" (download: {LocalMods.FormatSize(fetch)}):" : ":"), 24f);
                foreach (var a in fresh) Line("  • " + Describe(a), 22f);
            }
            if (replaced.Count > 0)
            {
                Line("The host has another version of these; theirs is used instead of yours:", 40f);
                foreach (var a in replaced) Line("  • " + Describe(a), 22f);
            }
            if (leftOut.Count > 0)
            {
                Line("The host doesn't have these, so they're left out until you restart:", 40f);
                foreach (var a in leftOut) Line("  • " + a, 22f);
            }
            if (_missing.Count == 0 && _extra.Count == 0)
                Line(AddonGate.Pending ? "Your other mods match the host's now." : "Nothing to change here.", 24f);

            bool needed = _missing.Count > 0 || _extra.Count > 0;
            bool workshopToo = AddonGate.Pending && Workshop();
            string verb = fetch > 0 ? (_extra.Count > 0 ? "Download, leave out" : "Download")
                : _missing.Count > 0 ? (_extra.Count > 0 ? "Load and leave out" : "Load")
                : "Leave out";
            UiKit.SetLabel(_go.gameObject, workshopToo ? verb : verb + " and join");
            _go.gameObject.SetActive(needed);
            UpdateStatus();
        }

        static bool Workshop()
        {
            AddonGate.Remaining(out var m, out var e);
            return m.Any(a => a.OnWorkshop) || e.Any(a => a.OnWorkshop);
        }

        static string Describe(Addon a)
        {
            string here = ModFetch.FindHere(a);
            string where = here == null ? LocalMods.FormatSize(a.Size)
                : LocalMods.InCache(here) ? "already downloaded" : "in your Mods folder";
            if (here == null && !a.Shareable) where = "the host can't send this one";
            return $"{a}  ({where})";
        }

        void UpdateStatus()
        {
            bool live = NetSession.Current != null && NetSession.Current.Resolving;
            _go.interactable = live && !ModFetch.Busy && (_missing.Count > 0 || _extra.Count > 0);
            string text = ModFetch.Status;
            if (!live && AddonGate.Pending && !ModFetch.Busy)
                text = Join(text, "The connection to the host has closed: join again when you're ready.");
            else if (live && Workshop() && (ModFetch.Done || (_missing.Count == 0 && _extra.Count == 0)))
                text = Join(text, "The Workshop add-ons still differ: see the other window.");
            _status.text = text;
        }

        static string Join(string a, string b) => string.IsNullOrEmpty(a) ? b ?? "" : string.IsNullOrEmpty(b) ? a : a + "\n" + b;

        void Line(string text, float height)
        {
            var t = _kit.Label(_list, text, height);
            _rows.Add(t.gameObject);
        }

        void OnGo()
        {
            ModFetch.Start(_missing, _extra);
            UpdateStatus();
        }

        void OnCancel() => SessionUi.CancelAddons();

        bool _wasBusy;

        void Update()
        {
            if (Time.unscaledTime < _nextPoll) return;
            _nextPoll = Time.unscaledTime + 0.2f;
            bool busy = ModFetch.Busy;
            if (_wasBusy && !busy)
            {
                Refresh(); // finished or failed: the lists change
                SessionUi.AddonsChanged();
            }
            else UpdateStatus();
            _wasBusy = busy;
        }

        public void OnAddonsChanged()
        {
            if (gameObject.activeSelf) Refresh();
        }

        public void OnConnectionClosed()
        {
            if (gameObject.activeSelf) Refresh();
        }
    }
}
