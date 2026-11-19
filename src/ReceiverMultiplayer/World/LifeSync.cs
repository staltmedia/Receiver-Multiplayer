using System.Collections.Generic;
using ReceiverMultiplayer.Net;
using ReceiverMultiplayer.Session;
using UnityEngine;

namespace ReceiverMultiplayer.World
{
    /// <summary>
    /// One life per run. A player who dies stays dead (the level is not reloaded) and spectates a living
    /// teammate. When a living player picks up a tape, every dead player comes back at that spot with the
    /// gear they died with. When everyone is dead, the host ends the run and the waiting room opens.
    /// The host owns the list of who is dead.
    /// </summary>
    static class LifeSync
    {
        const float WipeDelaySeconds = 4f;
        const int MaxDeadInMessage = 4096;
        // A shot player bleeds out over up to a second (the game's "dying") before they're down. A tape found by a
        // teammate in that time still brings them back, as soon as they're down. (The wait ends when they stop
        // bleeding out; this is only a backstop.)
        const float PendingReviveSeconds = 10f;

        static readonly HashSet<ushort> _dead = new HashSet<ushort>();   // host: authoritative; client: last broadcast
        static readonly NetWriter _w = new NetWriter(256);
        static bool _reportedDead;
        static float _time, _allDeadSince = -1f;

        // Every tape sends a revive to everyone; a death report says how many revives that player had received.
        // A death reported with fewer than the host has sent happened before the newest revive reached them, so
        // that revive brings them back and the host doesn't count them as dead (it used to, and then counted a
        // living player toward a team wipe and ignored the tapes they picked up).
        // Counted per player: the finder isn't sent their own tape's revive, and a late joiner never got the earlier ones.
        static readonly Dictionary<ushort, int> _revivesTo = new Dictionary<ushort, int>(); // host: revives sent to each client this run
        static int _revivesSeen;   // client: revives received this run

        static bool _pending;      // a teammate's tape arrived while we were bleeding out
        static float _pendingUntil, _pendingHeight;
        static ushort _pendingFinder;
        static Vector3 _pendingPos;

        public static bool IsDead(ushort playerId) => _dead.Contains(playerId);
        public static int DeadCount => _dead.Count;

        public static void Reset()
        {
            _dead.Clear();
            _reportedDead = false;
            _allDeadSince = -1f;
            _wonSince = -1f;
            _reportedWin = false;
            _tapesFound = 0;
            _revivesTo.Clear();
            _revivesSeen = 0;
            _pending = false;
            _leftDown.Clear();
            Revival.Reset();
            // _join is not cleared: it arrives right after the run's start, for the run being loaded.
        }

        // ---- joining mid-run ----
        // A player who joins a run already under way starts inside a teammate who's alive (not back at the start).
        // One who was down when they left comes back down, spectating, until a tape is found: leaving and rejoining
        // is no way to come back to life.

        static readonly Dictionary<string, bool> _leftDown = new Dictionary<string, bool>(); // host: who left this run, and if they were down
        static int _join = -1;     // client: how to start in the run being joined (-1: as usual, 0: at a teammate, 1: down)
        static ushort _joinRun;    // ...and which run that was for

        /// <summary>Host: a player left mid-run; remember whether they were down.</summary>
        public static void RememberLeaver(string identity, ushort id)
        {
            if (!string.IsNullOrEmpty(identity)) _leftDown[identity] = _dead.Contains(id);
        }

        /// <summary>Host: someone joined mid-run (their run start has just been sent): tell them how to start.</summary>
        public static void HostPlayerJoined(NetSession s, ushort id, string identity)
        {
            _revivesTo.Remove(id); // they've been sent no revives yet
            bool down = !string.IsNullOrEmpty(identity) && _leftDown.TryGetValue(identity, out bool d) && d;
            var name = s.Players.Find(p => p.Id == id)?.Name ?? "A player";
            if (down)
            {
                _dead.Add(id);
                HostBroadcast(s);
                Plugin.Log.LogInfo($"{name} rejoined the run; they were down when they left, so they're down again until a tape is found.");
            }
            else Plugin.Log.LogInfo($"{name} joined the run under way; they start inside a teammate who's alive.");
            var w = WorldSync.Begin(_w, Msg.JoinState);
            w.U8(down ? (byte)1 : (byte)0);
            s.SendToPlayer(id, w, Channel.Reliable);
        }

        public static void ClientReceiveJoinState(ref NetReader rd)
        {
            byte state = rd.U8();
            rd.End();
            if (state > 1) throw new ProtocolException("bad join state");
            _join = state;
            _joinRun = WorldSync.RunTag;
        }

        /// <summary>Client: just spawned in a run joined mid-way; start the way the host said.</summary>
        static void StartJoin(AimScript me)
        {
            int join = _join;
            _join = -1;
            if (join == 1)
            {
                Plugin.Log.LogInfo("You were down when you left this run, so you're back down: you come back when a teammate finds a tape.");
                UI.Notice.Show("You were down when you left, so you're watching until a teammate finds a tape.");
                me.InstaKill();
            }
            else Revival.JoinAtTeammate();
        }

        /// <summary>Every frame in a run.</summary>
        public static void Frame(NetSession s, float dt)
        {
            _time += dt;
            var me = LocalPlayer.Get();
            if (_join >= 0 && _joinRun != WorldSync.RunTag) _join = -1; // for a run that's gone
            if (_join >= 0 && me != null && !s.IsHost) StartJoin(me);
            bool dead = me != null && LocalPlayer.Dead(me);
            if (_pending)
            {
                if (dead)
                {
                    _pending = false;
                    Plugin.Log.LogInfo("Down after bleeding out; the tape found meanwhile brings you back.");
                    _reportedDead = false;
                    Revival.ReviveLocal(_pendingFinder, _pendingPos, _pendingHeight);
                }
                else if (_time > _pendingUntil || me == null || !Revival.IsDying(me)) _pending = false;
            }
            // While a revive is under way the player is still down for a moment: that isn't a death to report.
            // A revive that fails leaves them down with nothing under way, and the death is reported then.
            if (dead && !_reportedDead && !Revival.InProgress)
            {
                _reportedDead = true;
                if (s.IsHost) HostMarkDead(s, s.LocalId);
                else
                {
                    var w = WorldSync.Begin(_w, Msg.Died);
                    w.U16((ushort)Mathf.Min(_revivesSeen, ushort.MaxValue));
                    s.SendToHost(w, Channel.Reliable);
                    Plugin.Log.LogInfo($"Died (revives received this run: {_revivesSeen}).");
                }
            }
            else if (!dead && me != null && _reportedDead)
            {
                _reportedDead = false; // revived
            }

            if (s.IsHost) HostCheckWipe(s);
            CheckWin(s);
        }

        // ================= host =================

        public static void HostReceiveDied(NetSession s, ushort from, ref NetReader rd)
        {
            int seen = rd.U16();
            rd.End();
            _revivesTo.TryGetValue(from, out int sent);
            if (seen < sent)
            {
                var name = s.Players.Find(p => p.Id == from)?.Name ?? "A player";
                Plugin.Log.LogInfo($"{name} went down just before the last tape reached them ({seen} of {sent} revives received); that tape brings them back.");
                return;
            }
            HostMarkDead(s, from);
        }

        static void HostMarkDead(NetSession s, ushort id)
        {
            if (!_dead.Add(id)) return;
            var name = s.Players.Find(p => p.Id == id)?.Name ?? "A player";
            Plugin.Log.LogInfo($"{name} died ({_dead.Count}/{s.Players.Count} down).");
            HostBroadcast(s);
        }

        /// <summary>Someone left: they no longer count toward a wipe.</summary>
        public static void OnPlayerLeft(ushort id)
        {
            _dead.Remove(id);
            _revivesTo.Remove(id);
        }

        static void HostBroadcast(NetSession s)
        {
            var w = WorldSync.Begin(_w, Msg.LifeStates);
            w.U16((ushort)_dead.Count);
            foreach (var id in _dead) w.U16(id);
            s.SendToClients(w, Channel.Reliable);
        }

        static void HostCheckWipe(NetSession s)
        {
            bool allDead = s.Players.Count > 0 && s.Players.TrueForAll(p => _dead.Contains(p.Id));
            if (!allDead)
            {
                _allDeadSince = -1f;
                return;
            }
            if (_allDeadSince < 0f) { _allDeadSince = _time; return; }
            if (_time - _allDeadSince < WipeDelaySeconds) return;
            _allDeadSince = -1f;
            // Like single-player: everyone down means a fresh level (new seed unless the host fixed one), in the
            // same scene (the gun range restarts as the gun range).
            Plugin.Log.LogInfo("Everyone died: restarting with a new level.");
            s.StartGame(s.RunScene);
        }

        public static void HostReceiveTape(NetSession s, ushort from, ref NetReader rd)
        {
            Vector3 pos = Codec.Pos(ref rd);
            float height = ReadHeight(ref rd);
            rd.End();
            if (_dead.Contains(from)) return;
            if (!PlayerSync.TryGetPosition(from, out var body) || (body - pos).sqrMagnitude > Protocol.MaxShotOffset * Protocol.MaxShotOffset)
                return;
            HostTapeFound(s, from, pos, height);
        }

        /// <summary>
        /// A living player (host or client) picked up a tape with their body at <paramref name="pos"/>, at crouch
        /// height <paramref name="height"/> (the body's vertical scale: 1 standing, about 0.5 crouched).
        /// </summary>
        public static void OnLocalTapePickup(Vector3 pos, float height)
        {
            var s = NetSession.Current;
            if (s == null || !WorldSync.InRun) return;
            var me = LocalPlayer.Get();
            if (me == null || LocalPlayer.Dead(me)) return;
            if (s.IsHost)
            {
                HostTapeFound(s, s.LocalId, pos, height);
                return;
            }
            var w = WorldSync.Begin(_w, Msg.TapeTaken);
            Codec.Pos(w, pos);
            WriteHeight(w, height);
            s.SendToHost(w, Channel.Reliable);
        }

        /// <summary>
        /// A tape found by anyone is everyone's: every other player gets it to play too (so the team progresses
        /// toward the win together), and every dead player comes back inside the player who found it.
        /// </summary>
        static void HostTapeFound(NetSession s, ushort finder, Vector3 pos, float height)
        {
            _tapesFound++;
            foreach (var p in s.Players)
                if (p.Id != s.LocalId && p.Id != finder)
                    _revivesTo[p.Id] = (_revivesTo.TryGetValue(p.Id, out int n) ? n : 0) + 1;
            // A tape brings back everyone who's down, including those away right now: they rejoin alive.
            if (_leftDown.Count > 0)
                foreach (var key in new List<string>(_leftDown.Keys)) _leftDown[key] = false;
            var finderName = s.Players.Find(p => p.Id == finder)?.Name ?? "A player";
            if (_dead.Count > 0)
            {
                Plugin.Log.LogInfo($"Tape picked up by {finderName}: reviving {_dead.Count} player(s).");
                _dead.Clear();
                _allDeadSince = -1f;
                HostBroadcast(s);
            }
            else Plugin.Log.LogInfo($"Tape picked up by {finderName} (nobody down).");
            var w = WorldSync.Begin(_w, Msg.Revive);
            w.U16(finder);
            Codec.Pos(w, pos);
            WriteHeight(w, height);
            s.SendToClients(w, Channel.Reliable, finder);
            OnTapeFound(s, finder, pos, height);
        }

        static void OnTapeFound(NetSession s, ushort finder, Vector3 pos, float height)
        {
            var me = LocalPlayer.Get();
            if (me == null) return;
            if (finder != s.LocalId) GearSync.UnplayedTapes(me)++;
            if (LocalPlayer.Dead(me))
            {
                // If this revive fails, the death is reported again (the host has just cleared its list).
                _reportedDead = false;
                Revival.ReviveLocal(finder, pos, height);
            }
            else if (finder != s.LocalId && Revival.IsDying(me))
            {
                // Shot and bleeding out: not down yet, so come back the moment we are.
                _pending = true;
                _pendingUntil = _time + PendingReviveSeconds;
                _pendingFinder = finder;
                _pendingPos = pos;
                _pendingHeight = height;
                Plugin.Log.LogInfo("A tape was found while you were bleeding out: you come back once you're down.");
            }
        }

        static void WriteHeight(NetWriter w, float height) => w.U8(Codec.UnitByte(height, 1.5f));
        static float ReadHeight(ref NetReader rd) => Mathf.Clamp(Codec.UnitByte(rd.U8(), 1.5f), 0.1f, 1.5f);

        // ================= client =================

        public static void ClientReceiveStates(ref NetReader rd)
        {
            int n = rd.U16();
            if (n > MaxDeadInMessage) throw new ProtocolException("too many dead players");
            _dead.Clear();
            for (int i = 0; i < n; i++) _dead.Add(rd.U16());
            rd.End();
        }

        public static void ClientReceiveRevive(NetSession s, ref NetReader rd)
        {
            ushort finder = rd.U16();
            Vector3 pos = Codec.Pos(ref rd);
            float height = ReadHeight(ref rd);
            rd.End();
            _revivesSeen++;
            OnTapeFound(s, finder, pos, height);
        }

        // ================= winning =================

        // The game's win: every tape you pick up queues for playing, and once you've heard as many as the run has
        // (11 by default, all of them with the tape limit off) it sets `won`, fades out and loads its win scene.
        // Every tape found by anyone is given to everyone, so the whole team hears them all. The first player to
        // hear the last one wins it for the team; the run ends a few seconds later for everyone (the solo win scene
        // is skipped in a session; everyone goes back to the waiting room).

        const string WinText = "Every tape found and heard: the team won the run!";
        static readonly HarmonyLib.AccessTools.FieldRef<AimScript, bool> Won = HarmonyLib.AccessTools.FieldRefAccess<AimScript, bool>("won");
        static readonly HarmonyLib.AccessTools.FieldRef<AimScript, List<AudioClip>> TotalTapes = HarmonyLib.AccessTools.FieldRefAccess<AimScript, List<AudioClip>>("total_tapes");
        const float WinDelaySeconds = 6f;
        static float _wonSince = -1f;   // host: when the team's win was settled
        static bool _reportedWin;       // client: told the host we've heard every tape
        static int _tapesFound;         // host: tapes picked up by anyone this run

        static void CheckWin(NetSession s)
        {
            var me = LocalPlayer.Get();
            bool heardAll = me != null && Won(me);
            if (s.IsHost)
            {
                if (heardAll) HostTeamWon(s, s.LocalId);
                if (_wonSince >= 0f && _time - _wonSince >= WinDelaySeconds)
                {
                    _wonSince = -1f;
                    s.EndRun(WinText);
                }
            }
            else if (heardAll && !_reportedWin)
            {
                _reportedWin = true;
                s.SendToHost(WorldSync.Begin(_w, Msg.Won), Channel.Reliable);
            }
        }

        static void HostTeamWon(NetSession s, ushort who)
        {
            if (_wonSince >= 0f) return;
            _wonSince = _time;
            var name = s.Players.Find(p => p.Id == who)?.Name ?? "A player";
            Plugin.Log.LogInfo($"{name} has heard every tape ({_tapesFound} found this run): the team won.");
            s.SendToClients(WorldSync.Begin(_w, Msg.Won), Channel.Reliable);
            UI.Notice.Show(WinText);
        }

        public static void HostReceiveWon(NetSession s, ushort from, ref NetReader rd)
        {
            rd.End();
            // Believable only once the team has found as many tapes as a run has (everyone hears every tape found).
            var me = LocalPlayer.Get();
            var total = me != null ? TotalTapes(me) : null;
            int needed = total != null ? total.Count : 0;
            if (needed > 0 && _tapesFound < needed)
            {
                Plugin.Log.LogWarning($"A player reported hearing every tape, but only {_tapesFound} of {needed} have been found; ignored.");
                return;
            }
            HostTeamWon(s, from);
        }

        public static void ClientReceiveWon(ref NetReader rd)
        {
            rd.End();
            Plugin.Log.LogInfo("The team won the run.");
            UI.Notice.Show(WinText);
        }

        /// <summary>
        /// In a session the level never reloads on its own (no reload on death, no solo win scene): only the
        /// host starts runs. This also holds in the waiting room after a run ends, so the room stays open.
        /// </summary>
        [HarmonyLib.HarmonyPatch(typeof(AimScript), nameof(AimScript.UpdateLevelChange))]
        static class NoLevelReload
        {
            static bool Prefix() => NetSession.Current == null;
        }
    }
}
