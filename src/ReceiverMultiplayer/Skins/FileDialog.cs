using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace ReceiverMultiplayer.Skins
{
    /// <summary>
    /// The Windows "Open" dialog, so players pick a skin image from their own folders. It runs on its own
    /// (STA) thread so the game, and the network, keep running while it's open; the chosen path comes back
    /// through <see cref="TakeResult"/>, polled from the main thread.
    /// </summary>
    static class FileDialog
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        sealed class OpenFileName
        {
            public int lStructSize = Marshal.SizeOf(typeof(OpenFileName));
            public IntPtr hwndOwner = IntPtr.Zero;
            public IntPtr hInstance = IntPtr.Zero;
            public string lpstrFilter;
            public string lpstrCustomFilter;
            public int nMaxCustFilter;
            public int nFilterIndex = 1;
            public string lpstrFile;
            public int nMaxFile;
            public string lpstrFileTitle;
            public int nMaxFileTitle;
            public string lpstrInitialDir;
            public string lpstrTitle;
            public int Flags;
            public short nFileOffset;
            public short nFileExtension;
            public string lpstrDefExt;
            public IntPtr lCustData = IntPtr.Zero;
            public IntPtr lpfnHook = IntPtr.Zero;
            public string lpTemplateName;
            public IntPtr pvReserved = IntPtr.Zero;
            public int dwReserved;
            public int FlagsEx;
        }

        [DllImport("comdlg32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern bool GetOpenFileName([In, Out] OpenFileName ofn);

        const int OFN_NOCHANGEDIR = 0x8, OFN_PATHMUSTEXIST = 0x800, OFN_FILEMUSTEXIST = 0x1000, OFN_EXPLORER = 0x80000;

        static readonly object Gate = new object();
        static string _result;
        static bool _done;
        static volatile bool _open;

        public static bool IsOpen => _open;

        /// <summary>Open the dialog (once at a time). Returns false if one is already open or the dialog isn't available.</summary>
        public static bool OpenImage(string title)
        {
            if (_open) return false;
            IntPtr owner = IntPtr.Zero;
            try { owner = System.Diagnostics.Process.GetCurrentProcess().MainWindowHandle; } catch { /* no owner: still works */ }
            _open = true;
            var thread = new Thread(() =>
            {
                string path = null;
                try
                {
                    var ofn = new OpenFileName
                    {
                        hwndOwner = owner,
                        lpstrFilter = "Images (PNG, JPG, WebP, BMP, GIF, TIFF)\0*.png;*.jpg;*.jpeg;*.jfif;*.webp;*.bmp;*.gif;*.tif;*.tiff;*.heic;*.avif\0All files\0*.*\0\0",
                        lpstrFile = new string('\0', 1024),
                        nMaxFile = 1024,
                        lpstrTitle = title,
                        lpstrInitialDir = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
                        Flags = OFN_EXPLORER | OFN_FILEMUSTEXIST | OFN_PATHMUSTEXIST | OFN_NOCHANGEDIR,
                    };
                    if (GetOpenFileName(ofn)) path = ofn.lpstrFile?.TrimEnd('\0');
                }
                catch (Exception e)
                {
                    Plugin.Log.LogWarning($"The file picker couldn't open: {e.Message}");
                }
                lock (Gate) { _result = path; _done = true; }
                _open = false;
            }) { IsBackground = true, Name = "ReceiverMultiplayer file picker" };
            try
            {
                thread.SetApartmentState(ApartmentState.STA);
                thread.Start();
                return true;
            }
            catch (Exception e)
            {
                _open = false;
                Plugin.Log.LogWarning($"The file picker couldn't start: {e.Message}");
                return false;
            }
        }

        /// <summary>Main thread: the picked path once the dialog closed (null if cancelled); false while still open.</summary>
        public static bool TakeResult(out string path)
        {
            lock (Gate)
            {
                path = _result;
                if (!_done) return false;
                _done = false;
                _result = null;
                return true;
            }
        }
    }
}
