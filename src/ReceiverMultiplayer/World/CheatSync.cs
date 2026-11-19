using System;
using HarmonyLib;
using ReceiverMultiplayer.Net;
using ReceiverMultiplayer.Session;
using UnityEngine;

namespace ReceiverMultiplayer.World
{
    /// <summary>
    /// The game's typed cheats in a shared run:
    /// - <c>slomo</c> (time at 10%) is world time, so it's the team's: whoever types it toggles it for everyone
    ///   (the host decides and tells everyone; the time scale is held there every frame).
    /// - <c>iddqd</c> (god mode), <c>idinf</c> (infinite ammo) and <c>idkfa</c> (30 loose rounds) are for the
    ///   player who typed them, as in single-player; what they do (no deaths, shots, rounds on the belt) is
    ///   already shared.
    /// Everyone is told who used which cheat. Like single-player, cheats switch off when a new level loads.
    /// </summary>
    static class CheatSync
    {
        enum Kind : byte { GodMode = 0, Slomo = 1, InfiniteAmmo = 2, Ammo = 3, Max = 3 }

        const float SlomoScale = 0.1f; // the game's own ToggleSlomo value

        /// <summary>Slow motion is on for the team (in this run).</summary>
        public static bool Slomo { get; private set; }

        public static float TimeScale => Slomo ? SlomoScale : 1f;

        static readonly NetWriter _w = new NetWriter(64);
        static bool _slomoRequested;
        static bool _wasInRun;

        public static void Reset()
        {
            Slomo = false;
            _slomoRequested = false;
        }

        /// <summary>
        /// Every frame while a session exists (from the Runner, before anything else): hold the team's time scale
        /// during a run (the menu pause and the game's own slo-mo toggle would change it), and put time back to
        /// normal when a run ends. Outside runs only the menu pause is undone.
        /// </summary>
        public static void HoldTimeScale()
        {
            bool inRun = WorldSync.InRun;
            if (inRun)
            {
                if (Time.timeScale != TimeScale) Time.timeScale = TimeScale;
                Cheats.slomo_mode = Slomo;
            }
            else
            {
                if (_wasInRun && Slomo) { Slomo = false; Time.timeScale = 1f; Cheats.slomo_mode = false; }
                if (Time.timeScale == 0f) Time.timeScale = 1f;
            }
            _wasInRun = inRun;
        }

        // ---------------- local cheat use ----------------

        static void Report(Kind kind, bool on)
        {
            var s = NetSession.Current;
            if (s == null || !WorldSync.InRun) return;
            if (s.IsHost)
            {
                HostApply(s, s.LocalId, kind, on);
                return;
            }
            var w = WorldSync.Begin(_w, Msg.Cheat);
            w.U8((byte)kind);
            w.Bool(on);
            s.SendToHost(w, Channel.Reliable);
        }

        public static void HostReceive(NetSession s, ushort from, ref NetReader rd)
        {
            byte kind = rd.U8();
            bool on = rd.Bool();
            rd.End();
            if (kind > (byte)Kind.Max) throw new ProtocolException("bad cheat");
            HostApply(s, from, (Kind)kind, on);
        }

        static void HostApply(NetSession s, ushort from, Kind kind, bool on)
        {
            if (kind == Kind.Slomo)
            {
                Slomo = !Slomo; // a request to toggle the team's slow motion
                on = Slomo;
                Time.timeScale = TimeScale;
                Cheats.slomo_mode = Slomo;
            }
            var w = WorldSync.Begin(_w, Msg.Cheat);
            w.U16(from);
            w.U8((byte)kind);
            w.Bool(on);
            s.SendToClients(w, Channel.Reliable);
            Announce(s, from, kind, on);
        }

        /// <summary>Host: bring a player who joined mid-run up to date (slow motion only; the rest is personal).</summary>
        public static void HostSendStateTo(NetSession s, ushort id)
        {
            if (!Slomo) return;
            var w = WorldSync.Begin(_w, Msg.Cheat);
            w.U16(s.LocalId);
            w.U8((byte)Kind.Slomo);
            w.Bool(true);
            s.SendToPlayer(id, w, Channel.Reliable);
        }

        public static void ClientReceive(NetSession s, ref NetReader rd)
        {
            ushort from = rd.U16();
            byte kind = rd.U8();
            bool on = rd.Bool();
            rd.End();
            if (kind > (byte)Kind.Max) throw new ProtocolException("bad cheat");
            if ((Kind)kind == Kind.Slomo)
            {
                Slomo = on;
                Time.timeScale = TimeScale;
                Cheats.slomo_mode = on;
            }
            Announce(s, from, (Kind)kind, on);
        }

        static void Announce(NetSession s, ushort from, Kind kind, bool on)
        {
            bool mine = from == s.LocalId;
            string who = mine ? "You" : s.Players.Find(p => p.Id == from)?.Name ?? "A player";
            string text;
            switch (kind)
            {
                case Kind.Slomo: text = $"Slow motion {(on ? "on" : "off")} for everyone ({who})"; break;
                case Kind.GodMode: text = $"{who}: god mode {(on ? "on" : "off")}"; break;
                case Kind.InfiniteAmmo: text = $"{who}: infinite ammo {(on ? "on" : "off")}"; break;
                default: text = $"{who}: refilled loose rounds"; break;
            }
            Plugin.Log.LogInfo("Cheat: " + text);
            if (!mine || kind == Kind.Slomo) UI.Notice.Show(text);
        }

        // ---------------- patches ----------------

        /// <summary>Note which personal cheats a typed code switched, and send slo-mo to the host instead.</summary>
        [HarmonyPatch(typeof(Cheats), "Update")]
        static class UpdatePatch
        {
            static void Prefix(out (bool god, bool inf) __state) => __state = (Cheats.god_mode, Cheats.infinite_ammo);

            static void Postfix((bool god, bool inf) __state)
            {
                if (!WorldSync.InRun) return;
                if (Cheats.god_mode != __state.god) Report(Kind.GodMode, Cheats.god_mode);
                if (Cheats.infinite_ammo != __state.inf) Report(Kind.InfiniteAmmo, Cheats.infinite_ammo);
                if (_slomoRequested)
                {
                    _slomoRequested = false;
                    Report(Kind.Slomo, !Slomo);
                }
                Cheats.slomo_mode = Slomo; // until the host answers
            }
        }

        /// <summary>In a run the time scale is the team's: typing slomo asks the host to toggle it.</summary>
        [HarmonyPatch(typeof(Cheats), nameof(Cheats.ToggleSlomo))]
        static class SlomoPatch
        {
            static bool Prefix()
            {
                if (!WorldSync.InRun) return true;
                _slomoRequested = true;
                return false;
            }
        }

        [HarmonyPatch(typeof(Cheats), nameof(Cheats.GiveBullets))]
        static class AmmoPatch
        {
            static void Postfix()
            {
                if (WorldSync.InRun) Report(Kind.Ammo, true);
            }
        }
    }
}
