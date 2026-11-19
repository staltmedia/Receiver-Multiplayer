using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using ReceiverMultiplayer.Net;
using UnityEngine;

namespace ReceiverMultiplayer.Session
{
    /// <summary>
    /// Joining player: make this game's non-Workshop mods match the host's, without touching the Mods folder.
    /// Each mod the host has and this game lacks is taken from the cache of mods fetched before, or the Mods
    /// folder if a matching copy sits there unloaded, or else fetched from the host into the cache (checked file
    /// by file, SHA-256). Then the mods the host doesn't have (including other versions of the host's) are left
    /// out, the host's are loaded, and if nothing else differs the player joins. All of it lasts until restart.
    /// </summary>
    static class ModFetch
    {
        enum State { Idle, Fetching, Loading, Done, Failed }

        sealed class Job
        {
            public Addon A;
            public string Source;          // folder to load it from, once there
            public string Origin = "";     // for the window: "downloaded", "already downloaded", "in your Mods folder"
            public bool Asked, Listed;
            public float AskedAt;
            public byte Slot;
            public List<ModFile> Files;
            public string Staging, Target;
            public int NextFile, RecvFile;
            public long NextOffset, RecvOffset, Received;
            public FileStream Out;
            public SHA256 Sha;
        }

        static readonly List<Job> _jobs = new List<Job>();
        static readonly Queue<(int file, long offset, int length)> _inFlight = new Queue<(int, long, int)>();
        static List<Addon> _leaveOut = new List<Addon>();
        static readonly NetWriter _w = new NetWriter(256);
        static State _state;
        static string _status = "";
        static float _lastData, _nextRequest, _loadStarted;
        static long _downloadTotal;

        public static bool Busy => _state == State.Fetching || _state == State.Loading;
        public static bool Failed => _state == State.Failed;
        public static bool Done => _state == State.Done;
        public static string Status => _status;

        /// <summary>Bytes to fetch from the host for <paramref name="missing"/> (what isn't here already).</summary>
        public static long BytesToFetch(IEnumerable<Addon> missing) => missing.Where(a => !a.OnWorkshop && FindHere(a) == null).Sum(a => a.Size);

        /// <summary>A copy of this exact mod already on this machine (fetched before, or in the Mods folder), or null.</summary>
        public static string FindHere(Addon a)
        {
            if (a.OnWorkshop || !LocalMods.IsHash(a.Hash) || !LocalMods.IsSafeName(a.Folder)) return null;
            try
            {
                string cached = LocalMods.CacheDir(a.Folder, a.Hash);
                if (Directory.Exists(cached) && LocalMods.Read(cached).Hash == a.Hash) return cached;
                string own = Path.Combine(ModManager.GetModsfolderPath(), a.Folder);
                if (Directory.Exists(own) && LocalMods.Read(own).Hash == a.Hash) return own;
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"Couldn't look for '{a}' on this machine: {e.Message}");
            }
            return null;
        }

        /// <summary>Start (or retry): fetch <paramref name="missing"/>, then leave out <paramref name="extra"/> and load them.</summary>
        public static void Start(List<Addon> missing, List<Addon> extra)
        {
            if (Busy) return;
            Reset();
            _leaveOut = extra.Where(a => !a.OnWorkshop).ToList();
            foreach (var a in missing.Where(a => !a.OnWorkshop))
            {
                var job = new Job { A = a };
                string here = FindHere(a);
                if (here != null)
                {
                    job.Source = here;
                    job.Origin = LocalMods.InCache(here) ? "already downloaded" : "in your Mods folder";
                }
                else if (!a.Shareable || !LocalMods.IsHash(a.Hash) || !LocalMods.IsSafeName(a.Folder))
                {
                    Fail($"The host can't send '{a}' (it isn't a plain mod folder, or it's too big). Ask the host for it, or ask them to leave it out.");
                    return;
                }
                else _downloadTotal += a.Size;
                _jobs.Add(job);
            }
            _state = State.Fetching;
            _lastData = Time.unscaledTime;
            Plugin.Log.LogInfo($"Matching the host's non-Workshop mods: {_jobs.Count} to load ({_jobs.Count(j => j.Source == null)} to fetch, {LocalMods.FormatSize(_downloadTotal)}), {_leaveOut.Count} to leave out.");
            Tick();
        }

        /// <summary>Every frame (from <see cref="Runner"/>).</summary>
        public static void Tick()
        {
            if (!Busy) return;
            var s = NetSession.Current;
            if (_state == State.Fetching && (s == null || !s.Resolving))
            {
                Fail("The connection to the host closed. Join again to carry on (what's downloaded is kept).");
                return;
            }
            float now = Time.unscaledTime;
            if (_state == State.Loading)
            {
                if (ModImporter.IsImportingMods() && now - _loadStarted < 90f) { _status = "Loading the host's mods…"; return; }
                Finish();
                return;
            }

            var job = _jobs.FirstOrDefault(j => j.Source == null);
            if (job == null)
            {
                Apply();
                return;
            }
            if (!job.Asked)
            {
                job.Asked = true;
                job.AskedAt = now;
                _w.Reset();
                _w.U8((byte)Msg.AddonFiles);
                _w.Str(job.A.Folder, Addons.MaxTextBytes);
                _w.Str(job.A.Hash, LocalMods.HashChars);
                s.SendWhileResolving(_w);
                _status = $"Asking the host for '{job.A}'…";
                return;
            }
            if (!job.Listed)
            {
                if (now - job.AskedAt > 30f) Fail("The host didn't answer. Try again.");
                return;
            }
            if (_inFlight.Count > 0 && now - _lastData > 30f)
            {
                Fail("The download stalled. Try again (what's downloaded so far is kept per finished mod).");
                return;
            }
            while (_inFlight.Count < Protocol.ChunkWindow && now >= _nextRequest && job.NextFile < job.Files.Count)
            {
                var f = job.Files[job.NextFile];
                int length = (int)Math.Min(Protocol.ChunkBytes, f.Size - job.NextOffset);
                _w.Reset();
                _w.U8((byte)Msg.AddonChunk);
                _w.U8(job.Slot);
                _w.U16((ushort)job.NextFile);
                _w.U32((uint)job.NextOffset);
                _w.U32((uint)length);
                s.SendWhileResolving(_w);
                _inFlight.Enqueue((job.NextFile, job.NextOffset, length));
                _nextRequest = Mathf.Max(_nextRequest, now - 0.1f) + 1f / Protocol.ChunkRequestsPerSecond;
                job.NextOffset += length;
                if (job.NextOffset >= f.Size) { job.NextFile = NextNonEmpty(job, job.NextFile + 1); job.NextOffset = 0; }
            }
            long done = _jobs.Where(j => j.Source != null && j.Origin == "downloaded").Sum(j => j.A.Size) + job.Received;
            int index = _jobs.Where(j => j.Origin == "downloaded" || j.Source == null).ToList().IndexOf(job) + 1;
            int count = _jobs.Count(j => j.Origin == "downloaded" || j.Source == null);
            _status = $"Downloading '{job.A}' ({index} of {count}): {LocalMods.FormatSize(done)} of {LocalMods.FormatSize(_downloadTotal)}…";
        }

        static int NextNonEmpty(Job job, int from)
        {
            while (from < job.Files.Count && job.Files[from].Size == 0) from++;
            return from;
        }

        /// <summary>The host's file list for the mod being fetched (or why it can't be had).</summary>
        public static void OnFiles(ref NetReader rd)
        {
            string folder = rd.Str(Addons.MaxTextBytes);
            string hash = rd.Str(LocalMods.HashChars);
            bool ok = rd.Bool();
            if (!ok)
            {
                string reason = Protocol.SanitizeReason(rd.Str(Protocol.MaxReasonBytes));
                rd.End();
                if (CurrentJob(folder, hash) != null) Fail($"The host can't send '{folder}': {reason}.");
                return;
            }
            byte slot = rd.U8();
            int n = rd.U16();
            if (n > LocalMods.MaxFiles) throw new ProtocolException("too many mod files");
            var files = new List<ModFile>(n);
            for (int i = 0; i < n; i++)
            {
                string name = rd.Str(LocalMods.MaxNameChars);
                ulong lo = rd.U32(), hi = rd.U32();
                string sha = rd.Str(LocalMods.HashChars);
                files.Add(new ModFile { Name = name, Size = (long)Math.Min(lo | (hi << 32), long.MaxValue), Sha256 = sha });
            }
            rd.End();

            var job = CurrentJob(folder, hash);
            if (job == null || job.Listed) return; // not what we asked for (stale after a retry)

            // Everything about the list is checked before anything is written.
            string problem = null;
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            long total = 0;
            foreach (var f in files)
            {
                if (!LocalMods.IsSafeName(f.Name) || LocalMods.IsJunk(f.Name)) problem = $"a file has an unusual name";
                else if (LocalMods.IsBlockedFile(f.Name)) problem = $"it contains '{f.Name}', which isn't mod data";
                else if (!LocalMods.IsHash(f.Sha256) || f.Size < 0 || f.Size > LocalMods.MaxFileBytes) problem = "a file's details are wrong";
                else if (!names.Add(f.Name)) problem = "two files have the same name";
                if (problem != null) break;
                total += f.Size;
            }
            if (problem == null && files.Count == 0) problem = "it has no files";
            if (problem == null && (total != job.A.Size || total > LocalMods.MaxModBytes)) problem = "its size doesn't match what the host said";
            if (problem == null && LocalMods.Combine(files) != job.A.Hash) problem = "its files don't match what the host said";
            if (problem != null)
            {
                Fail($"'{job.A}' from the host didn't check out: {problem}. Nothing was kept.");
                return;
            }

            try
            {
                job.Staging = LocalMods.IncomingDir(job.A.Folder, job.A.Hash);
                job.Target = LocalMods.CacheDir(job.A.Folder, job.A.Hash);
                if (!LocalMods.InCache(job.Staging) || !LocalMods.InCache(job.Target)) throw new IOException("bad cache path");
                if (Directory.Exists(job.Staging)) Directory.Delete(job.Staging, true); // an unfinished earlier try
                Directory.CreateDirectory(job.Staging);
                foreach (var f in files.Where(f => f.Size == 0))
                {
                    if (f.Sha256 != EmptySha) throw new IOException($"'{f.Name}' is empty but its checksum isn't");
                    File.WriteAllBytes(Path.Combine(job.Staging, f.Name), new byte[0]);
                }
            }
            catch (Exception e)
            {
                Fail($"Couldn't make room for '{job.A}': {e.Message}");
                return;
            }
            job.Files = files;
            job.Slot = slot;
            job.Listed = true;
            job.NextFile = job.RecvFile = NextNonEmpty(job, 0);
            job.NextOffset = job.RecvOffset = 0;
            _lastData = Time.unscaledTime;
            if (job.RecvFile >= files.Count) Complete(job);
        }

        static readonly string EmptySha = LocalMods.Hex(SHA256.Create().ComputeHash(new byte[0]));

        /// <summary>A piece of the file being fetched. Pieces arrive in the order they were asked for.</summary>
        public static void OnChunk(ref NetReader rd)
        {
            byte slot = rd.U8();
            int file = rd.U16();
            uint offset = rd.U32();
            int count = (int)rd.U32();
            if (count < 0 || count > Protocol.ChunkBytes || count > rd.Remaining) throw new ProtocolException("bad mod piece");
            var job = _state == State.Fetching ? _jobs.FirstOrDefault(j => j.Source == null && j.Listed) : null;
            if (job == null || _inFlight.Count == 0 || slot != job.Slot) return; // stale after a failure or retry
            var want = _inFlight.Dequeue();
            if (want.file != file || want.offset != offset || file != job.RecvFile || offset != job.RecvOffset)
            {
                Fail("The host sent pieces out of order. Try again.");
                return;
            }
            if (count != want.length)
            {
                Fail($"The host couldn't read '{job.Files[file].Name}' of '{job.A}'.");
                return;
            }
            var f = job.Files[file];
            try
            {
                if (job.Out == null)
                {
                    job.Out = new FileStream(Path.Combine(job.Staging, f.Name), FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16);
                    job.Sha = SHA256.Create();
                }
                byte[] data = rd.Bytes(count);
                rd.End();
                job.Out.Write(data, 0, count);
                job.Sha.TransformBlock(data, 0, count, null, 0);
            }
            catch (ProtocolException) { throw; }
            catch (Exception e)
            {
                Fail($"Couldn't save '{job.A}': {e.Message}");
                return;
            }
            _lastData = Time.unscaledTime;
            job.Received += count;
            job.RecvOffset += count;
            if (job.RecvOffset < f.Size) return;

            // A whole file: check it.
            job.Sha.TransformFinalBlock(new byte[0], 0, 0);
            string got = LocalMods.Hex(job.Sha.Hash);
            job.Out.Dispose();
            job.Sha.Dispose();
            job.Out = null;
            job.Sha = null;
            if (got != f.Sha256)
            {
                Fail($"'{f.Name}' of '{job.A}' arrived damaged. Try again.");
                return;
            }
            job.RecvFile = NextNonEmpty(job, file + 1);
            job.RecvOffset = 0;
            if (job.RecvFile >= job.Files.Count) Complete(job);
        }

        static Job CurrentJob(string folder, string hash) =>
            _state == State.Fetching ? _jobs.FirstOrDefault(j => j.Source == null && j.Asked && j.A.Folder == folder && j.A.Hash == hash) : null;

        /// <summary>Every file is in and checked: move the folder into the cache under its hash.</summary>
        static void Complete(Job job)
        {
            try
            {
                if (Directory.Exists(job.Target)) Directory.Delete(job.Target, true); // a damaged earlier copy (the hash didn't match)
                Directory.CreateDirectory(Path.GetDirectoryName(job.Target));
                Directory.Move(job.Staging, job.Target);
            }
            catch (Exception e)
            {
                Fail($"Couldn't store '{job.A}': {e.Message}");
                return;
            }
            var check = LocalMods.Read(job.Target);
            if (check.Hash != job.A.Hash)
            {
                Fail($"'{job.A}' didn't check out after saving. Try again.");
                return;
            }
            job.Source = job.Target;
            job.Origin = "downloaded";
            _inFlight.Clear();
            Plugin.Log.LogInfo($"Fetched '{job.A}' from the host ({LocalMods.FormatSize(job.A.Size)}) into the mod cache.");
        }

        /// <summary>All there: leave out the host-less mods, then load the host's.</summary>
        static void Apply()
        {
            foreach (var a in _leaveOut) Addons.Unload(a);
            var loaded = new HashSet<string>(Addons.Local().Select(a => a.Key));
            int queued = 0;
            foreach (var job in _jobs)
            {
                if (loaded.Contains(job.A.Key)) continue;
                ModImporter.ImportMod(job.Source, true, true);
                queued++;
            }
            Plugin.Log.LogInfo($"Left out {_leaveOut.Count} mod(s) the host doesn't have; loading {queued} of the host's.");
            _state = State.Loading;
            _loadStarted = Time.unscaledTime;
            _status = "Loading the host's mods…";
        }

        static void Finish()
        {
            Addons.RefreshGunList();
            var loaded = new HashSet<string>(Addons.Local().Select(a => a.Key));
            var failed = _jobs.Where(j => !loaded.Contains(j.A.Key)).Select(j => j.A.ToString()).ToList();
            if (failed.Count > 0)
            {
                Fail($"{string.Join(", ", failed)} didn't load (another add-on may use the same files: leave it out and try again).");
                return;
            }
            _state = State.Done;
            _status = _jobs.Count > 0 ? "The host's mods are loaded (until you restart Receiver)." : "Done.";
            Plugin.Log.LogInfo("The host's non-Workshop mods are loaded.");
            if (AddonGate.TryJoin()) _status = "Joining…";
        }

        static void Fail(string message)
        {
            foreach (var j in _jobs)
            {
                try { j.Out?.Dispose(); } catch { }
                try { j.Sha?.Dispose(); } catch { }
                j.Out = null;
                j.Sha = null;
                if (j.Source == null && j.Staging != null && LocalMods.InCache(j.Staging))
                    try { if (Directory.Exists(j.Staging)) Directory.Delete(j.Staging, true); } catch { }
            }
            _inFlight.Clear();
            _state = State.Failed;
            _status = message;
            Plugin.Log.LogWarning("Fetching the host's mods: " + message);
        }

        static void Reset()
        {
            foreach (var j in _jobs)
            {
                try { j.Out?.Dispose(); } catch { }
                try { j.Sha?.Dispose(); } catch { }
            }
            _jobs.Clear();
            _inFlight.Clear();
            _leaveOut = new List<Addon>();
            _downloadTotal = 0;
            _nextRequest = 0f;
            _status = "";
            _state = State.Idle;
        }

        /// <summary>The add-on windows were closed or a new mismatch arrived: forget a finished or failed attempt.</summary>
        public static void Clear()
        {
            if (Busy) Fail("Stopped.");
            Reset();
        }
    }
}
