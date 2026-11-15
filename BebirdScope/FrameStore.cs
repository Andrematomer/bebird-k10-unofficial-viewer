using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace BebirdScope;

/// <summary>
/// Hand-off point between the DirectShow streaming thread (writer) and the UI thread (reader).
/// Stores the newest frame as top-down BGR24.
/// </summary>
internal sealed class FrameStore
{
    readonly object _gate = new object();
    byte[] _buf = Array.Empty<byte>();
    int _w, _h;
    long _id;
    long _received;

    public long FramesReceived => Interlocked.Read(ref _received);

    public void Configure(int width, int height)
    {
        lock (_gate)
        {
            _w = width;
            _h = height;
            _buf = new byte[width * 3 * height];
            _id++;
        }
    }

    /// <summary>Called on the streaming thread with a DirectShow RGB24 DIB (4-byte aligned rows).</summary>
    public void Push(IntPtr src, int len, bool bottomUp)
    {
        lock (_gate)
        {
            if (_w == 0 || _h == 0) return;
            int srcStride = (_w * 3 + 3) & ~3;
            int dstStride = _w * 3;
            if (len < srcStride * _h) return;
            for (int y = 0; y < _h; y++)
            {
                int sy = bottomUp ? _h - 1 - y : y;
                Marshal.Copy(src + sy * srcStride, _buf, y * dstStride, dstStride);
            }
            _id++;
        }
        Interlocked.Increment(ref _received);
    }

    /// <summary>UI thread only. Copies the newest frame into the bitmap (creating it when the size changes).</summary>
    public bool Blit(ref long lastId, ref WriteableBitmap bmp, out bool sizeChanged)
    {
        sizeChanged = false;
        lock (_gate)
        {
            if (_id == lastId || _w == 0 || _h == 0) return false;
            if (bmp == null || bmp.PixelWidth != _w || bmp.PixelHeight != _h)
            {
                bmp = new WriteableBitmap(_w, _h, 96, 96, PixelFormats.Bgr24, null);
                sizeChanged = true;
            }
            bmp.WritePixels(new Int32Rect(0, 0, _w, _h), _buf, _w * 3, 0);
            lastId = _id;
            return true;
        }
    }
}
