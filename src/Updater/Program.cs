using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace ReceiverMultiplayerUpdater
{
    /// <summary>
    /// Command line (all optional):
    ///   --game "dir"   the Receiver folder (otherwise found: beside this program, then the Steam libraries)
    ///   --zip "file"   install this release zip instead of downloading the latest
    ///   --update       started by the game after the player chose OK: update straight away, then start the game again
    ///   --wait pid     wait for that process (the game) to exit before writing anything
    ///   --yes          start at once and close when done
    ///   --no-launch    never start the game afterwards
    /// </summary>
    sealed class Options
    {
        public string Game, Zip;
        public bool Update, Yes, NoLaunch, Elevated;
        public int WaitPid;

        public static Options Parse(string[] args)
        {
            var o = new Options();
            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i].ToLowerInvariant();
                string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{args[i]} needs a value.");
                switch (a)
                {
                    case "--game": o.Game = Next(); break;
                    case "--zip": o.Zip = Next(); break;
                    case "--update": o.Update = true; break;
                    case "--wait": o.WaitPid = int.TryParse(Next(), out var pid) ? pid : 0; break;
                    case "--yes": case "--silent": o.Yes = true; break;
                    case "--no-launch": o.NoLaunch = true; break;
                    case "--elevated": o.Elevated = true; break;
                    default: throw new ArgumentException($"Unknown option '{args[i]}'.");
                }
            }
            return o;
        }

        /// <summary>The same job for an elevated copy of this program: it installs, the original starts the game.</summary>
        public string ForElevated(string game)
        {
            var sb = new StringBuilder($"--elevated --yes --no-launch --game {Quote(game)}");
            if (Zip != null) sb.Append(" --zip ").Append(Quote(Zip));
            return sb.ToString();
        }

        static string Quote(string s) => "\"" + s.TrimEnd('\\') + "\"";
    }

    static class Program
    {
        static Mutex _lock;

        [STAThread]
        static int Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Options o;
            try { o = Options.Parse(args); }
            catch (ArgumentException e)
            {
                MessageBox.Show(e.Message, UpdaterForm.Title, MessageBoxButtons.OK, MessageBoxIcon.Error);
                return 2;
            }

            // One updater at a time (an elevated copy is started by the one holding the lock, which waits for it).
            if (!o.Elevated)
            {
                _lock = new Mutex(false, @"Local\ReceiverMultiplayer-Updater");
                bool mine;
                try { mine = _lock.WaitOne(0); }
                catch (AbandonedMutexException) { mine = true; }
                if (!mine)
                {
                    MessageBox.Show("The Receiver Multiplayer updater is already running.", UpdaterForm.Title, MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return 3;
                }
            }

            Core.CleanUp();
            var form = new UpdaterForm(o);
            Application.Run(form);
            return form.ExitCode;
        }
    }
}
