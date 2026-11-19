using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace ReceiverMultiplayerUpdater
{
    /// <summary>
    /// One small window: which Receiver folder, what's installed, what's out, and OK / Cancel. Started by the game
    /// (--update) it gets on with it at once and starts the game again afterwards; Cancel there starts the game with
    /// the version the player already has.
    /// </summary>
    sealed class UpdaterForm : Form
    {
        public const string Title = "Receiver Multiplayer Updater";
        public int ExitCode { get; private set; } = 1;

        enum Mode { Checking, NoGame, Offer, UpToDate, CheckFailed, Working, Done, Failed }

        readonly Options _o;
        readonly Label _header, _detail, _status;
        readonly TextBox _folder;
        readonly Button _change, _ok, _cancel;
        readonly CheckBox _dontAsk;
        readonly ProgressBar _bar;

        Mode _mode;
        string _game, _checkError, _error;
        bool _checking, _installing, _dontAskLoaded;
        Version _installed, _target;
        Release _release;
        byte[] _zipBytes; // --zip: the release, read once

        public UpdaterForm(Options o)
        {
            _o = o;
            SuspendLayout();
            AutoScaleDimensions = new SizeF(96f, 96f);
            AutoScaleMode = AutoScaleMode.Dpi;
            Font = SystemFonts.MessageBoxFont;
            Text = Title;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(500, 262);

            _header = new Label { Bounds = new Rectangle(16, 14, 468, 28), Font = new Font(Font.FontFamily, 12f, FontStyle.Bold) };
            _detail = new Label { Bounds = new Rectangle(16, 46, 468, 40) };
            var folderLabel = new Label { Bounds = new Rectangle(16, 92, 468, 18), Text = "Receiver folder:" };
            _folder = new TextBox { Bounds = new Rectangle(16, 112, 382, 23), ReadOnly = true, TabStop = false };
            _change = new Button { Bounds = new Rectangle(404, 111, 80, 25), Text = "Change…" };
            _dontAsk = new CheckBox { Bounds = new Rectangle(16, 146, 468, 22), Text = "Don't show the update question in game (update from here instead)" };
            _bar = new ProgressBar { Bounds = new Rectangle(16, 180, 468, 14), Visible = false };
            _status = new Label { Bounds = new Rectangle(16, 198, 468, 18), ForeColor = SystemColors.GrayText };
            _ok = new Button { Bounds = new Rectangle(318, 222, 80, 27), Text = "OK" };
            _cancel = new Button { Bounds = new Rectangle(404, 222, 80, 27), Text = "Cancel" };
            Controls.AddRange(new Control[] { _header, _detail, folderLabel, _folder, _change, _dontAsk, _bar, _status, _ok, _cancel });
            AcceptButton = _ok;
            CancelButton = _cancel;

            _change.Click += (s, e) => ChooseFolder();
            _ok.Click += (s, e) => OkClicked();
            _cancel.Click += (s, e) => CancelClicked();
            ResumeLayout(false);
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            string game = null;
            try
            {
                game = _o.Game != null ? Path.GetFullPath(_o.Game.Trim('"')).TrimEnd('\\') : Core.FindGame();
            }
            catch (Exception ex) { Core.Log(null, "Bad game folder: " + ex.Message); }
            if (game != null && !Core.IsGameDir(game)) game = null;
            SetGame(game);
            StartCheck();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (_installing) { e.Cancel = true; return; } // never leave a half-written install
            SaveAsk();
            base.OnFormClosing(e);
        }

        // ---------------- state ----------------

        void SetGame(string game)
        {
            SaveAsk();
            _game = game;
            _folder.Text = game ?? "(not found)";
            _installed = game != null ? Core.Installed(game) : null;
            _dontAskLoaded = game != null && !Core.AskInGame(game);
            _dontAsk.Checked = _dontAskLoaded;
        }

        /// <summary>The "don't show again" box mirrors the game's own setting; a change is saved to its config.</summary>
        void SaveAsk()
        {
            if (_game == null || _dontAsk.Checked == _dontAskLoaded) return;
            try
            {
                Core.SetAskInGame(_game, !_dontAsk.Checked);
                _dontAskLoaded = _dontAsk.Checked;
                Core.Log(_game, "In-game update question " + (_dontAsk.Checked ? "off." : "on."));
            }
            catch (Exception ex) { Core.Log(_game, "Couldn't save the in-game question setting: " + ex.Message); }
        }

        async void StartCheck()
        {
            _checking = true;
            _checkError = null;
            _release = null;
            _target = null;
            Show(Mode.Checking);
            string zip = _o.Zip;
            try
            {
                if (zip != null)
                {
                    _zipBytes = await Task.Run(() => Core.ReadZip(zip));
                    var bytes = _zipBytes;
                    _target = await Task.Run(() => Core.ZipVersion(bytes));
                    if (_target == null) throw new Exception(Path.GetFileName(zip) + " isn't a Receiver Multiplayer release.");
                }
                else
                {
                    _release = await Task.Run(Core.Latest);
                    _target = _release.Version;
                }
            }
            catch (Exception ex)
            {
                _checkError = ex is System.Net.WebException ? "No connection to GitHub (" + ex.Message + ")" : ex.Message;
                Core.Log(_game, "Update check failed: " + ex.Message);
            }
            _checking = false;
            Decide();
        }

        void Decide()
        {
            if (_checking) { Show(Mode.Checking); return; }
            if (_game == null) { Show(Mode.NoGame); return; }
            if (_target == null) { Show(Mode.CheckFailed); return; }
            _installed = Core.Installed(_game);
            bool offer = _installed == null || _target > _installed || _o.Zip != null;
            if (!offer)
            {
                Show(Mode.UpToDate);
                if (_o.Update) LaunchAndClose();
                else if (_o.Yes) { ExitCode = 0; Close(); }
                return;
            }
            Show(Mode.Offer);
            if (_o.Update || _o.Yes) Start(); // the player already said OK in game (or asked for no questions)
        }

        void Show(Mode m)
        {
            _mode = m;
            bool idle = m != Mode.Working && m != Mode.Checking;
            _change.Enabled = idle;
            _dontAsk.Enabled = _game != null;
            _bar.Visible = !idle;
            if (!idle) _bar.Style = ProgressBarStyle.Marquee;
            _ok.Enabled = true;
            _cancel.Enabled = true;
            _ok.Text = "OK";
            _cancel.Text = "Cancel";
            _status.Text = "";
            string have = _installed != null ? $"You have {_installed}." : "It isn't installed yet.";
            const string sameVersion = "Players only connect to the same version.";
            switch (m)
            {
                case Mode.Checking:
                    _header.Text = "Checking for updates…";
                    _detail.Text = "";
                    _ok.Enabled = false;
                    break;
                case Mode.NoGame:
                    _header.Text = "Where is Receiver?";
                    _detail.Text = "Couldn't find Receiver. Press Change… and choose its folder (the one with Receiver.exe in it).";
                    _ok.Enabled = false;
                    break;
                case Mode.Offer:
                    _header.Text = _installed == null ? $"Install Receiver Multiplayer {_target}?"
                        : _target > _installed ? $"Update to {_target}?"
                        : $"Install {_target} over {_installed}?";
                    _detail.Text = (_installed == null ? "It goes into the Receiver folder below." : have) + " " + sameVersion;
                    if (_o.Zip != null) _status.Text = "From " + Path.GetFileName(_o.Zip);
                    else if (_o.Update) _status.Text = "Cancel starts the game with the version you have.";
                    break;
                case Mode.UpToDate:
                    _header.Text = $"Receiver Multiplayer {_installed} is up to date.";
                    _detail.Text = sameVersion;
                    _ok.Text = "Play";
                    _cancel.Text = "Close";
                    break;
                case Mode.CheckFailed:
                    _header.Text = "Couldn't check for updates.";
                    _detail.Text = _checkError + " " + have;
                    _ok.Text = "Retry";
                    break;
                case Mode.Working:
                    _header.Text = _installed == null ? $"Installing {_target}…" : $"Updating to {_target}…";
                    _detail.Text = "";
                    _ok.Enabled = false;
                    break;
                case Mode.Done:
                    _header.Text = $"Receiver Multiplayer {_installed} is installed.";
                    _detail.Text = "Multiplayer is in the game's pause menu. " + sameVersion;
                    _ok.Text = "Play";
                    _cancel.Text = "Close";
                    break;
                case Mode.Failed:
                    _header.Text = _installed == null ? "The install didn't finish." : "The update didn't finish.";
                    _detail.Text = _error + " " + have;
                    _status.Text = @"Details: BepInEx\ReceiverMultiplayer-Updater.log";
                    _ok.Text = "Retry";
                    break;
            }
        }

        // ---------------- buttons ----------------

        void OkClicked()
        {
            switch (_mode)
            {
                case Mode.Offer: Start(); break;
                case Mode.UpToDate:
                case Mode.Done: LaunchAndClose(); break;
                case Mode.CheckFailed: StartCheck(); break;
                case Mode.Failed: if (_target != null) Start(); else StartCheck(); break;
            }
        }

        void CancelClicked()
        {
            if (_installing) return;
            // Started from the game: Cancel carries on with the version the player has.
            if (_o.Update && _mode != Mode.Done) LaunchAndClose();
            else Close();
        }

        void ChooseFolder()
        {
            using (var d = new FolderBrowserDialog { Description = "Choose the Receiver folder (the one with Receiver.exe in it).", ShowNewFolderButton = false })
            {
                if (_game != null) d.SelectedPath = _game;
                if (d.ShowDialog(this) != DialogResult.OK) return;
                if (!Core.IsGameDir(d.SelectedPath))
                {
                    MessageBox.Show(this, "That folder doesn't have Receiver.exe in it.", Title, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                SetGame(d.SelectedPath.TrimEnd('\\'));
                Decide();
            }
        }

        // ---------------- the work ----------------

        async void Start()
        {
            if (_game == null || _target == null) return;
            if (_o.WaitPid == 0 && Core.GameRunning(_game))
            {
                MessageBox.Show(this, "Receiver is running. Close it first, then press OK.", Title, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            Show(Mode.Working);
            string game = _game, zip = _o.Zip;
            var release = _release;
            Core.Log(game, $"Installing {_target} (had {_installed?.ToString() ?? "none"}) from {zip ?? release.ZipUrl}");
            try
            {
                if (_o.WaitPid != 0)
                {
                    _status.Text = "Waiting for Receiver to close…";
                    int pid = _o.WaitPid;
                    await Task.Run(() => Core.WaitForExit(pid, TimeSpan.FromSeconds(60)));
                    _o.WaitPid = 0;
                    await Task.Delay(500); // let Windows release the game's files
                }
                if (!_o.Elevated && !Core.CanWrite(game)) { await InstallElevated(game); return; }

                byte[] data = _zipBytes;
                if (data == null)
                {
                    _status.Text = "Downloading…";
                    _bar.Style = ProgressBarStyle.Continuous;
                    _bar.Value = 0;
                    data = await Task.Run(() => Core.Download(release, pct => Ui(() =>
                    {
                        _bar.Value = Math.Max(0, Math.Min(100, pct));
                        _status.Text = $"Downloading… {pct}%";
                    })));
                }
                _status.Text = "Installing…";
                _bar.Style = ProgressBarStyle.Marquee;
                _installing = true;
                _cancel.Enabled = false;
                int files;
                try { files = await Task.Run(() => Core.Install(data, game)); }
                finally { _installing = false; }
                _installed = Core.Installed(game);
                Core.Log(game, $"Installed {_installed} ({files} files).");
                Finished();
            }
            catch (Exception ex)
            {
                Core.Log(game, "Failed: " + ex.Message);
                Fail(ex.Message);
            }
        }

        /// <summary>The folder needs admin rights: a second copy of this program installs with them, this one waits.</summary>
        async Task InstallElevated(string game)
        {
            _status.Text = "Windows asks for admin rights to write to the Receiver folder…";
            Process p;
            try
            {
                p = Process.Start(new ProcessStartInfo(Application.ExecutablePath, _o.ForElevated(game)) { UseShellExecute = true, Verb = "runas" });
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
            {
                Fail("Writing to the Receiver folder needs admin rights, and they weren't given.");
                return;
            }
            using (p) await Task.Run(() => p.WaitForExit());
            _installed = Core.Installed(game);
            if (_installed != null && _installed == _target) Finished();
            else Fail("It was canceled or didn't finish.");
        }

        void Finished()
        {
            ExitCode = 0;
            Show(Mode.Done);
            if (_o.Update && !_o.NoLaunch) LaunchAndClose();
            else if (_o.Yes) Close();
        }

        void Fail(string message)
        {
            ExitCode = 1;
            _error = message;
            Show(Mode.Failed);
        }

        void LaunchAndClose()
        {
            SaveAsk();
            if (_game != null && !_o.NoLaunch)
            {
                try { Core.Launch(_game); }
                catch (Exception ex) { MessageBox.Show(this, "Couldn't start Receiver: " + ex.Message, Title, MessageBoxButtons.OK, MessageBoxIcon.Warning); }
            }
            Close();
        }

        void Ui(Action a)
        {
            if (IsHandleCreated && !IsDisposed) BeginInvoke(a);
        }
    }
}
