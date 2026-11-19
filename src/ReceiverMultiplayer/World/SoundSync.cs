using System.Collections.Generic;
using HarmonyLib;
using ReceiverMultiplayer.Net;
using ReceiverMultiplayer.Session;
using UnityEngine;
using Object = UnityEngine.Object;
using Random = UnityEngine.Random;

namespace ReceiverMultiplayer.World
{
    /// <summary>
    /// Other players' gun handling, magazine and body sounds (slide, hammer, safety, cylinder, magazine in and
    /// out, loading rounds, holstering, footsteps...). The game plays every one of them through three tiny
    /// methods (pick a random clip from a group, PlayOneShot it); they're replaced here by identical code that
    /// also reports which clip played, and everyone else plays that clip at the player. Gunshots are left to
    /// the shot replay, which plays them with a gunshot's range.
    /// </summary>
    static class SoundSync
    {
        enum Source : byte { Gun = 0, Magazine = 1, Body = 2 }

        const int MaxClipNameBytes = 96;
        static readonly NetWriter _w = new NetWriter(160);
        static readonly Dictionary<string, AudioClip> _clips = new Dictionary<string, AudioClip>();
        static readonly HashSet<AudioClip> _gunshots = new HashSet<AudioClip>();
        static float _nextClipScan = -100f;

        public static void Reset() => _gunshots.Clear();

        // ---------------- local: play as the game would, then report ----------------

        static void Report(AudioClip clip, float volume, Source source)
        {
            var s = NetSession.Current;
            if (s == null || clip == null || !WorldSync.InRun) return;
            if (source == Source.Gun && IsGunshot(clip)) return;
            var w = WorldSync.Begin(_w, Msg.Sound);
            w.Str(clip.name, MaxClipNameBytes);
            w.U8(Codec.UnitByte(volume, 2.55f));
            w.U8((byte)source);
            if (s.IsHost) Relay(s, s.LocalId, w);
            else s.SendToHost(w, Channel.Reliable);
            SyncStats.Count("sounds sent");
        }

        static bool IsGunshot(AudioClip clip)
        {
            if (_gunshots.Count == 0)
                for (int g = 0; g < GunCache.Count; g++)
                {
                    var shots = GunCache.Get(g)?.sound_gunshot_smallroom;
                    if (shots != null) foreach (var c in shots) if (c != null) _gunshots.Add(c);
                }
            return _gunshots.Contains(clip);
        }

        static bool Mine(Component c)
        {
            for (var x = c.transform; x != null; x = x.parent)
                if (x.name == "PlayerInventory") return true;
            return false;
        }

        [HarmonyPatch(typeof(GunScript), nameof(GunScript.PlaySound))]
        static class GunSounds
        {
            static bool Prefix(GunScript __instance, AudioClip[] group, float volume)
            {
                if (!WorldSync.InRun) return true;
                if (group == null || group.Length == 0) return false;
                var clip = group[Random.Range(0, group.Length)];
                var src = __instance.GetComponent<AudioSource>();
                if (src != null) src.PlayOneShot(clip, volume * Preferences.sound_volume);
                Report(clip, volume, Source.Gun);
                return false;
            }
        }

        [HarmonyPatch(typeof(mag_script), nameof(mag_script.PlaySoundFromGroup))]
        static class MagazineSounds
        {
            static bool Prefix(mag_script __instance, List<AudioClip> group, float volume)
            {
                if (!WorldSync.InRun) return true;
                if (group == null || group.Count == 0) return false;
                var clip = group[Random.Range(0, group.Count)];
                var src = __instance.GetComponent<AudioSource>();
                if (src != null) src.PlayOneShot(clip, volume * Preferences.sound_volume);
                if (Mine(__instance)) Report(clip, volume, Source.Magazine); // world magazines bounce on every machine anyway
                return false;
            }
        }

        [HarmonyPatch(typeof(AimScript), nameof(AimScript.PlaySoundFromGroup))]
        static class BodySounds
        {
            static bool Prefix(AimScript __instance, List<AudioClip> group, float volume)
            {
                if (!WorldSync.InRun || !__instance.main_client_control) return true;
                if (group == null || group.Count == 0) return true; // the game's own behaviour (it would throw)
                var clip = group[Random.Range(0, group.Count)];
                var src = __instance.GetComponent<AudioSource>();
                if (src != null) src.PlayOneShot(clip, volume * Preferences.sound_volume);
                Report(clip, volume, Source.Body);
                return false;
            }
        }

        // ---------------- network ----------------

        static void Relay(NetSession s, ushort owner, NetWriter src)
        {
            var r = WorldSync.Begin(new NetWriter(src.Length + 4), Msg.Sound);
            r.U16(owner);
            r.Raw(src.Buffer, 3, src.Length - 3);
            s.SendToClients(r, Channel.Reliable, owner);
        }

        public static void HostReceive(NetSession s, ushort from, ref NetReader rd)
        {
            var copy = new NetWriter(rd.Remaining + 8);
            rd.CopyRestTo(copy);
            string clip = rd.Str(MaxClipNameBytes);
            float volume = Codec.UnitByte(rd.U8(), 2.55f);
            byte source = rd.U8();
            rd.End();
            if (source > (byte)Source.Body) throw new ProtocolException("bad sound source");
            Play(from, clip, volume, (Source)source);
            var r = WorldSync.Begin(new NetWriter(copy.Length + 8), Msg.Sound);
            r.U16(from);
            r.Raw(copy.Buffer, 0, copy.Length);
            s.SendToClients(r, Channel.Reliable, from);
        }

        public static void ClientReceive(NetSession s, ref NetReader rd)
        {
            ushort owner = rd.U16();
            string clip = rd.Str(MaxClipNameBytes);
            float volume = Codec.UnitByte(rd.U8(), 2.55f);
            byte source = rd.U8();
            rd.End();
            if (source > (byte)Source.Body) throw new ProtocolException("bad sound source");
            if (owner == s.LocalId) return;
            Play(owner, clip, volume, (Source)source);
        }

        static void Play(ushort owner, string clipName, float volume, Source source)
        {
            if (LocalPlayer.Get() == null) return;
            var clip = FindClip(clipName);
            if (clip == null) { SyncStats.Note("sound: unknown clip", $"a teammate's sound '{clipName}' isn't loaded here"); return; }
            Vector3 pos;
            bool found = source == Source.Body
                ? PlayerSync.TryGetBodyPosition(owner, out pos)
                : GearSync.TryGetHandPosition(owner, out pos) || PlayerSync.TryGetBodyPosition(owner, out pos);
            if (!found) return;
            // Handling sounds are quiet and close: they fade out over a few tens of metres.
            OneShotAudio.Play(clip, pos, volume, 1.5f, 35f);
            SyncStats.Count("sounds played");
        }

        static AudioClip FindClip(string name)
        {
            if (_clips.TryGetValue(name, out var clip) && clip != null) return clip;
            if (Time.unscaledTime < _nextClipScan) return null;
            _nextClipScan = Time.unscaledTime + 5f;
            _clips.Clear();
            foreach (var c in Resources.FindObjectsOfTypeAll<AudioClip>())
                if (c != null && !_clips.ContainsKey(c.name)) _clips[c.name] = c;
            return _clips.TryGetValue(name, out clip) ? clip : null;
        }
    }
}
