using System;
using System.IO;
using BepInEx;
using UnityEngine;
using Object = UnityEngine.Object;

namespace ReceiverMultiplayer.Skins
{
    /// <summary>
    /// Your own skin: any image you pick (PNG or JPG read by the game; WebP, BMP, GIF, TIFF and others by Windows), squeezed to a 256×256 JPEG (a few tens of KB) so it's quick to
    /// share, and kept in the plugin's config folder so it's still applied next launch.
    /// </summary>
    static class SkinStore
    {
        public const int Size = 256;
        const int Quality = 85;
        const long MaxSourceBytes = 40L * 1024 * 1024;
        public const int MaxSourceSide = 8192;

        public static Texture2D Texture { get; private set; }
        public static byte[] Encoded { get; private set; }
        /// <summary>Bumps on every change (apply or remove), so displays and the network can notice.</summary>
        public static int Version { get; private set; }
        public static string Status { get; private set; } = "";

        static string FilePath => Path.Combine(Path.Combine(Paths.ConfigPath, "ReceiverMultiplayer"), "skin.jpg");

        public static void Load()
        {
            try
            {
                if (!File.Exists(FilePath)) return;
                var bytes = File.ReadAllBytes(FilePath);
                if (!SkinImage.Validate(bytes, out _)) return;
                var tex = Decode(bytes);
                if (tex == null) return;
                Set(tex, bytes);
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"Couldn't load your saved skin: {e.Message}");
            }
        }

        /// <summary>Main thread, every frame: pick up the file the dialog returned.</summary>
        public static void Tick()
        {
            if (!FileDialog.TakeResult(out string path) || string.IsNullOrEmpty(path)) return;
            Apply(path);
        }

        public static void Choose()
        {
            if (FileDialog.OpenImage("Choose a skin image")) Status = "Choose an image in the window that opened.";
        }

        public static void Apply(string path)
        {
            try
            {
                var info = new FileInfo(path);
                if (!info.Exists) { Status = "That file doesn't exist."; return; }
                if (info.Length > MaxSourceBytes) { Status = "That image is too large (40 MB max)."; return; }
                var file = File.ReadAllBytes(path);
                // A small file can claim an enormous picture; decoding that would take all the memory there is.
                if (SkinImage.HeaderSize(file, out int fw, out int fh, out _) && (fw <= 0 || fh <= 0 || fw > MaxSourceSide || fh > MaxSourceSide))
                {
                    Status = $"That image is too big ({fw}×{fh}; {MaxSourceSide} pixels a side at most).";
                    return;
                }
                var source = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                string decoder = "the game";
                if (!source.LoadImage(file))
                {
                    // The game reads only PNG and JPG (and not every PNG): let Windows read anything else it can.
                    Object.Destroy(source);
                    if (!WicImage.TryDecode(path, Size, out var rgba, out int w, out int h, out string why))
                    {
                        Status = "Couldn't read that image: " + why;
                        Plugin.Log.LogWarning($"Skin from '{Path.GetFileName(path)}' couldn't be read: {why}");
                        return;
                    }
                    source = new Texture2D(Size, Size, TextureFormat.RGBA32, false);
                    source.LoadRawTextureData(rgba);
                    source.Apply(false);
                    decoder = $"Windows ({w}×{h})";
                }
                var tex = Shrink(source);
                Object.Destroy(source);
                var bytes = tex.EncodeToJPG(Quality);
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
                File.WriteAllBytes(FilePath, bytes);
                Set(tex, bytes);
                Status = "Skin applied.";
                Plugin.Log.LogInfo($"Skin applied ({bytes.Length / 1024} KB, read by {decoder}).");
            }
            catch (Exception e)
            {
                Status = "Couldn't use that image.";
                Plugin.Log.LogWarning($"Skin from '{Path.GetFileName(path)}' failed: {e.Message}");
            }
        }

        public static void Remove()
        {
            try { if (File.Exists(FilePath)) File.Delete(FilePath); }
            catch (Exception e) { Plugin.Log.LogWarning($"Couldn't delete the saved skin: {e.Message}"); }
            if (Texture != null) Object.Destroy(Texture);
            Texture = null;
            Encoded = null;
            Version++;
            Status = "Skin removed.";
            SkinSync.OnLocalChanged();
        }

        static void Set(Texture2D tex, byte[] bytes)
        {
            if (Texture != null && Texture != tex) Object.Destroy(Texture);
            Texture = tex;
            Encoded = bytes;
            Version++;
            SkinSync.OnLocalChanged();
        }

        /// <summary>Scale any image to 256×256 on the GPU (bilinear) and read it back.</summary>
        static Texture2D Shrink(Texture2D source)
        {
            var rt = RenderTexture.GetTemporary(Size, Size, 0, RenderTextureFormat.ARGB32);
            var previous = RenderTexture.active;
            try
            {
                source.filterMode = FilterMode.Bilinear;
                Graphics.Blit(source, rt);
                RenderTexture.active = rt;
                var tex = new Texture2D(Size, Size, TextureFormat.RGB24, true);
                tex.ReadPixels(new Rect(0, 0, Size, Size), 0, 0);
                tex.Apply(true);
                tex.wrapMode = TextureWrapMode.Repeat;
                tex.name = "MP_Skin";
                return tex;
            }
            finally
            {
                RenderTexture.active = previous;
                RenderTexture.ReleaseTemporary(rt);
            }
        }

        /// <summary>Decode an already-validated image into a texture for a pill.</summary>
        public static Texture2D Decode(byte[] bytes)
        {
            var tex = new Texture2D(2, 2, TextureFormat.RGB24, true) { name = "MP_Skin", wrapMode = TextureWrapMode.Repeat };
            if (tex.LoadImage(bytes, false)) return tex;
            Object.Destroy(tex);
            return null;
        }
    }

    /// <summary>
    /// Header checks done before any image from the network (or disk) reaches the decoder: only PNG or JPEG,
    /// at most <see cref="MaxBytes"/>, and no larger than 512×512 (so a small file can't claim a huge image).
    /// </summary>
    static class SkinImage
    {
        public const int MaxBytes = 128 * 1024;
        const int MaxSide = 512;

        public static bool Validate(byte[] b, out string why)
        {
            why = null;
            if (b == null || b.Length < 32) { why = "empty"; return false; }
            if (b.Length > MaxBytes) { why = "too large"; return false; }
            if (!HeaderSize(b, out int w, out int h, out why)) return false;
            if (w <= 0 || h <= 0 || w > MaxSide || h > MaxSide) { why = $"{w}×{h} is too big"; return false; }
            return true;
        }

        /// <summary>A PNG's or JPEG's size from its header, without decoding it. False: neither (or a broken JPEG).</summary>
        public static bool HeaderSize(byte[] b, out int w, out int h, out string why)
        {
            why = null;
            w = h = 0;
            if (b == null || b.Length < 32) { why = "empty"; return false; }
            if (b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47)
            {
                // PNG: IHDR is the first chunk; width and height are big-endian at 16 and 20.
                w = (b[16] << 24) | (b[17] << 16) | (b[18] << 8) | b[19];
                h = (b[20] << 24) | (b[21] << 16) | (b[22] << 8) | b[23];
                return true;
            }
            if (b[0] == 0xFF && b[1] == 0xD8)
            {
                if (JpegSize(b, out w, out h)) return true;
                why = "bad JPEG";
                return false;
            }
            why = "not a PNG or JPEG";
            return false;
        }

        static bool JpegSize(byte[] b, out int w, out int h)
        {
            w = h = 0;
            int i = 2;
            while (i + 9 < b.Length)
            {
                if (b[i] != 0xFF) return false;
                byte marker = b[i + 1];
                if (marker == 0xFF) { i++; continue; }
                if (marker == 0xD8 || (marker >= 0xD0 && marker <= 0xD7) || marker == 0x01) { i += 2; continue; }
                int len = (b[i + 2] << 8) | b[i + 3];
                if (len < 2) return false;
                bool sof = marker >= 0xC0 && marker <= 0xCF && marker != 0xC4 && marker != 0xC8 && marker != 0xCC;
                if (sof)
                {
                    h = (b[i + 5] << 8) | b[i + 6];
                    w = (b[i + 7] << 8) | b[i + 8];
                    return true;
                }
                i += 2 + len;
            }
            return false;
        }
    }
}
