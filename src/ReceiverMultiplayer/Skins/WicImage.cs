using System;
using System.Runtime.InteropServices;

namespace ReceiverMultiplayer.Skins
{
    /// <summary>
    /// Reads images the game's own decoder can't (it knows only PNG and JPG, and not every PNG): WebP, BMP, GIF, TIFF,
    /// 16-bit PNGs, and HEIC or AVIF where Windows has those codecs. It uses the Windows Imaging Component, the
    /// decoder File Explorer and Photos use, called through its COM interfaces by vtable slot (no extra libraries).
    /// The format comes from the file's contents, so a WebP renamed to .png reads too. The image is scaled to a
    /// square on the way and returned as RGBA, bottom row first, as a Unity texture wants it.
    /// </summary>
    static class WicImage
    {
        static readonly Guid FactoryClass = new Guid("cacaf262-9370-4615-a13b-9f5539da4c0a");     // CLSID_WICImagingFactory
        static readonly Guid FactoryInterface = new Guid("ec5ec8a9-c395-4314-9c77-54d7a935ff70"); // IID_IWICImagingFactory
        static Guid _rgba = new Guid("f5c7ad2d-6a8d-43dd-a7a8-a29935261ae9");                    // GUID_WICPixelFormat32bppRGBA

        const uint ApartmentThreaded = 2, InprocServer = 1, GenericRead = 0x80000000;
        const int ChangedMode = unchecked((int)0x80010106);          // RPC_E_CHANGED_MODE: COM is already set up otherwise
        const int ComponentNotFound = unchecked((int)0x88982F50);    // WINCODEC_ERR_COMPONENTNOTFOUND: no codec for it
        const int UnknownImageFormat = unchecked((int)0x88982F07);   // WINCODEC_ERR_UNKNOWNIMAGEFORMAT
        const int Fant = 3;                                          // WICBitmapInterpolationModeFant

        [DllImport("ole32.dll")] static extern int CoInitializeEx(IntPtr reserved, uint coInit);
        [DllImport("ole32.dll")] static extern void CoUninitialize();
        [DllImport("ole32.dll")] static extern int CoCreateInstance(ref Guid clsid, IntPtr outer, uint context, ref Guid iid, out IntPtr obj);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate uint ReleaseFn(IntPtr self);
        [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Unicode)]
        delegate int CreateDecoderFromFilenameFn(IntPtr self, [MarshalAs(UnmanagedType.LPWStr)] string path, IntPtr vendor, uint access, int options, out IntPtr decoder);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int CreateFn(IntPtr self, out IntPtr obj);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int GetFrameFn(IntPtr self, uint index, out IntPtr frame);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int GetSizeFn(IntPtr self, out uint width, out uint height);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int ScalerInitFn(IntPtr self, IntPtr source, uint width, uint height, int mode);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        delegate int ConverterInitFn(IntPtr self, IntPtr source, ref Guid format, int dither, IntPtr palette, double alphaThreshold, int paletteType);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int CopyPixelsFn(IntPtr self, IntPtr rect, uint stride, uint size, [Out] byte[] buffer);

        // vtable slots (wincodec.h): IUnknown 0-2; factory 3 CreateDecoderFromFilename, 10 CreateFormatConverter,
        // 11 CreateBitmapScaler; decoder 13 GetFrame; any bitmap source 3 GetSize, 7 CopyPixels; converter and
        // scaler 8 Initialize.
        static T Slot<T>(IntPtr obj, int slot) where T : class
        {
            IntPtr table = Marshal.ReadIntPtr(obj);
            IntPtr fn = Marshal.ReadIntPtr(table, slot * IntPtr.Size);
            return Marshal.GetDelegateForFunctionPointer(fn, typeof(T)) as T;
        }

        static void Release(ref IntPtr obj)
        {
            if (obj == IntPtr.Zero) return;
            Slot<ReleaseFn>(obj, 2)(obj);
            obj = IntPtr.Zero;
        }

        /// <param name="size">Width and height of the square result.</param>
        /// <param name="rgba">size × size × 4 bytes, bottom row first.</param>
        /// <param name="why">Why it couldn't be read, in words for the player.</param>
        public static bool TryDecode(string path, int size, out byte[] rgba, out int width, out int height, out string why)
        {
            rgba = null;
            width = height = 0;
            why = null;
            if (Environment.OSVersion.Platform != PlatformID.Win32NT) { why = "only PNG and JPG images work on this system."; return false; }
            int init = CoInitializeEx(IntPtr.Zero, ApartmentThreaded);
            IntPtr factory = IntPtr.Zero, decoder = IntPtr.Zero, frame = IntPtr.Zero, scaler = IntPtr.Zero, converter = IntPtr.Zero;
            try
            {
                var clsid = FactoryClass;
                var iid = FactoryInterface;
                int hr = CoCreateInstance(ref clsid, IntPtr.Zero, InprocServer, ref iid, out factory);
                if (hr < 0) { why = $"Windows' image decoder isn't available (0x{hr:X8})."; return false; }

                hr = Slot<CreateDecoderFromFilenameFn>(factory, 3)(factory, path, IntPtr.Zero, GenericRead, 0, out decoder);
                if (hr == ComponentNotFound || hr == UnknownImageFormat)
                {
                    why = "it isn't an image this PC can read. If it's a WebP, Microsoft's free \"WebP Image Extensions\" (Microsoft Store) let Windows read it; or save it as PNG or JPG.";
                    return false;
                }
                if (hr < 0) { why = $"Windows couldn't read it (0x{hr:X8})."; return false; }
                hr = Slot<GetFrameFn>(decoder, 13)(decoder, 0, out frame);
                if (hr < 0) { why = $"it has no picture in it (0x{hr:X8})."; return false; }
                hr = Slot<GetSizeFn>(frame, 3)(frame, out uint w, out uint h);
                if (hr < 0 || w == 0 || h == 0) { why = "it has no size."; return false; }
                if (w > SkinStore.MaxSourceSide || h > SkinStore.MaxSourceSide)
                {
                    why = $"it's too big ({w}×{h}; {SkinStore.MaxSourceSide} pixels a side at most).";
                    return false;
                }
                width = (int)w;
                height = (int)h;

                hr = Slot<CreateFn>(factory, 11)(factory, out scaler);
                if (hr >= 0) hr = Slot<ScalerInitFn>(scaler, 8)(scaler, frame, (uint)size, (uint)size, Fant);
                if (hr < 0) { why = $"it couldn't be scaled (0x{hr:X8})."; return false; }
                hr = Slot<CreateFn>(factory, 10)(factory, out converter);
                if (hr >= 0) hr = Slot<ConverterInitFn>(converter, 8)(converter, scaler, ref _rgba, 0, IntPtr.Zero, 0.0, 0);
                if (hr < 0) { why = $"its colours couldn't be converted (0x{hr:X8})."; return false; }

                int stride = size * 4;
                var top = new byte[stride * size];
                hr = Slot<CopyPixelsFn>(converter, 7)(converter, IntPtr.Zero, (uint)stride, (uint)top.Length, top);
                if (hr < 0) { why = $"its pixels couldn't be read (0x{hr:X8})."; return false; }
                rgba = new byte[top.Length];
                for (int y = 0; y < size; y++) Buffer.BlockCopy(top, y * stride, rgba, (size - 1 - y) * stride, stride);
                return true;
            }
            catch (Exception e)
            {
                why = "Windows' image decoder failed: " + e.Message;
                return false;
            }
            finally
            {
                Release(ref converter);
                Release(ref scaler);
                Release(ref frame);
                Release(ref decoder);
                Release(ref factory);
                if (init >= 0) CoUninitialize(); // balance our own CoInitializeEx only (RPC_E_CHANGED_MODE: COM was set up otherwise)
            }
        }
    }
}
