using System.Runtime.InteropServices;

namespace ExplorerHelper.Services;

/// <summary>
/// Media metadata Explorer already knows about a file — resolution, length, frame rate,
/// bit rate — read straight from the Windows Shell property store (the same values shown on
/// the Details tab of a file's Properties dialog). Everything is best-effort: any missing
/// property or unreadable file yields <c>null</c> for that field rather than throwing, so the
/// details panel simply omits the rows it can't fill (issue #20).
/// </summary>
public static class ShellPropertyService
{
    /// <summary>The subset of shell properties the preview details panel surfaces.</summary>
    public sealed class MediaProperties
    {
        /// <summary>Pixel dimensions (width, height) for images and video, if known.</summary>
        public (uint Width, uint Height)? Dimensions { get; init; }

        /// <summary>Playing length of audio/video, if known.</summary>
        public TimeSpan? Duration { get; init; }

        /// <summary>Video frame rate in frames per second, if known.</summary>
        public double? FrameRate { get; init; }

        /// <summary>Total bit rate in bits per second, if known.</summary>
        public ulong? Bitrate { get; init; }
    }

    // Windows property keys (fmtid + pid). See the System.* property definitions in
    // propkey.h / the Windows property system documentation.
    private static readonly Guid VideoFmtId = new("64440491-4C8B-11D1-8B70-080036B11A03");
    private static readonly Guid MediaFmtId = new("64440490-4C8B-11D1-8B70-080036B11A03");
    private static readonly Guid ImageFmtId = new("6444048F-4C8B-11D1-8B70-080036B11A03");

    private static readonly PropertyKey VideoFrameWidth = new(VideoFmtId, 3);   // System.Video.FrameWidth
    private static readonly PropertyKey VideoFrameHeight = new(VideoFmtId, 4);  // System.Video.FrameHeight
    private static readonly PropertyKey VideoFrameRate = new(VideoFmtId, 6);    // System.Video.FrameRate (frames/1000s)
    private static readonly PropertyKey VideoTotalBitrate = new(VideoFmtId, 43);// System.Video.TotalBitrate (bps)
    private static readonly PropertyKey MediaDuration = new(MediaFmtId, 3);     // System.Media.Duration (100ns units)
    private static readonly PropertyKey AudioBitrate = new(MediaFmtId, 4);      // System.Audio.EncodingBitrate (bps)
    private static readonly PropertyKey ImageWidth = new(ImageFmtId, 3);        // System.Image.HorizontalSize
    private static readonly PropertyKey ImageHeight = new(ImageFmtId, 4);       // System.Image.VerticalSize

    /// <summary>
    /// Reads the media properties for a file. Safe to call on a background thread; returns an
    /// all-null <see cref="MediaProperties"/> if the file has no such metadata or can't be read.
    /// </summary>
    public static MediaProperties Read(string path)
    {
        IPropertyStore? store = null;
        try
        {
            var iid = typeof(IPropertyStore).GUID;
            SHGetPropertyStoreFromParsingName(path, IntPtr.Zero, GETPROPERTYSTOREFLAGS.Default, ref iid, out store);

            // Prefer real video/image pixel sizes; images use their own keys.
            var width = ReadUInt(store, VideoFrameWidth) ?? ReadUInt(store, ImageWidth);
            var height = ReadUInt(store, VideoFrameHeight) ?? ReadUInt(store, ImageHeight);
            (uint, uint)? dimensions = width is { } w && height is { } h ? (w, h) : null;

            // System.Media.Duration is in 100-nanosecond units.
            var durationTicks = ReadUInt64(store, MediaDuration);
            TimeSpan? duration = durationTicks is { } ticks and > 0 ? TimeSpan.FromTicks((long)ticks) : null;

            // System.Video.FrameRate is stored as frames per 1000 seconds.
            var frameRateMilli = ReadUInt(store, VideoFrameRate);
            double? frameRate = frameRateMilli is { } fr and > 0 ? fr / 1000.0 : null;

            var bitrate = ReadUInt64(store, VideoTotalBitrate) ?? ReadUInt64(store, AudioBitrate);
            if (bitrate is 0) bitrate = null;

            return new MediaProperties
            {
                Dimensions = dimensions,
                Duration = duration,
                FrameRate = frameRate,
                Bitrate = bitrate,
            };
        }
        catch
        {
            return new MediaProperties();
        }
        finally
        {
            if (store is not null)
                Marshal.ReleaseComObject(store);
        }
    }

    private static uint? ReadUInt(IPropertyStore store, PropertyKey key)
    {
        var pv = default(PropVariant);
        try
        {
            if (store.GetValue(ref key, out pv) != 0 || pv.vt == 0 /* VT_EMPTY */)
                return null;
            return PropVariantToUInt32(ref pv, out var value) == 0 ? value : null;
        }
        catch
        {
            return null;
        }
        finally
        {
            PropVariantClear(ref pv);
        }
    }

    private static ulong? ReadUInt64(IPropertyStore store, PropertyKey key)
    {
        var pv = default(PropVariant);
        try
        {
            if (store.GetValue(ref key, out pv) != 0 || pv.vt == 0)
                return null;
            return PropVariantToUInt64(ref pv, out var value) == 0 ? value : null;
        }
        catch
        {
            return null;
        }
        finally
        {
            PropVariantClear(ref pv);
        }
    }

    // --- Interop -----------------------------------------------------------------------

    [StructLayout(LayoutKind.Sequential)]
    private struct PropertyKey(Guid fmtid, uint pid)
    {
        public Guid FmtId = fmtid;
        public uint Pid = pid;
    }

    // PROPVARIANT: an 8-byte header followed by a union. Using two IntPtrs for the union keeps
    // the struct the right size on both x86 (16 bytes) and x64 (24 bytes); we never read the
    // union directly — the PropVariantToXxx helpers coerce it for us.
    [StructLayout(LayoutKind.Sequential)]
    private struct PropVariant
    {
        public ushort vt;
        public ushort wReserved1;
        public ushort wReserved2;
        public ushort wReserved3;
        public IntPtr p;
        public IntPtr p2;
    }

    [ComImport]
    [Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        [PreserveSig] int GetCount(out uint cProps);
        [PreserveSig] int GetAt(uint iProp, out PropertyKey pkey);
        [PreserveSig] int GetValue(ref PropertyKey key, out PropVariant pv);
        [PreserveSig] int SetValue(ref PropertyKey key, ref PropVariant pv);
        [PreserveSig] int Commit();
    }

    private enum GETPROPERTYSTOREFLAGS
    {
        Default = 0,
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    private static extern void SHGetPropertyStoreFromParsingName(
        [MarshalAs(UnmanagedType.LPWStr)] string pszPath,
        IntPtr pbc,
        GETPROPERTYSTOREFLAGS flags,
        ref Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out IPropertyStore propertyStore);

    [DllImport("propsys.dll", PreserveSig = true)]
    private static extern int PropVariantToUInt32(ref PropVariant propvar, out uint ret);

    [DllImport("propsys.dll", PreserveSig = true)]
    private static extern int PropVariantToUInt64(ref PropVariant propvar, out ulong ret);

    [DllImport("ole32.dll")]
    private static extern int PropVariantClear(ref PropVariant pvar);
}
