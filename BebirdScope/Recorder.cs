using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Media.Core;
using Windows.Media.MediaProperties;
using Windows.Media.Transcoding;
using Windows.Storage;
using Windows.Storage.Streams;

namespace BebirdScope;

/// <summary>
/// Records the frames it is given into an H.264 MP4 using the encoder built into Windows
/// (MediaStreamSource -> MediaTranscoder). Frames are BGRA, top-down, fixed size, pushed live.
/// </summary>
internal sealed class Recorder
{
    struct Item { public byte[] Data; public TimeSpan Ts; }

    readonly object _gate = new object();
    readonly Queue<Item> _queue = new Queue<Item>();
    MediaStreamSource _mss;
    MediaTranscoder _transcoder;
    IRandomAccessStream _out;
    Task _transcode;
    Stopwatch _clock;
    MediaStreamSourceSampleRequest _pendingReq;
    MediaStreamSourceSampleRequestDeferral _pendingDef;
    bool _started, _stopping;
    long _frames;

    public string Path { get; private set; }
    public int Width { get; private set; }
    public int Height { get; private set; }
    public string Error { get; private set; }
    public long Frames => Interlocked.Read(ref _frames);
    public TimeSpan Elapsed => _clock == null ? TimeSpan.Zero : _clock.Elapsed;

    public async Task StartAsync(string path, int width, int height)
    {
        Path = path; Width = width; Height = height;

        File.WriteAllBytes(path, Array.Empty<byte>());
        StorageFile file = await StorageFile.GetFileFromPathAsync(path);
        _out = await file.OpenAsync(FileAccessMode.ReadWrite);

        var vp = VideoEncodingProperties.CreateUncompressed(MediaEncodingSubtypes.Bgra8, (uint)width, (uint)height);
        vp.FrameRate.Numerator = 30; vp.FrameRate.Denominator = 1;
        vp.PixelAspectRatio.Numerator = 1; vp.PixelAspectRatio.Denominator = 1;

        _mss = new MediaStreamSource(new VideoStreamDescriptor(vp)) { BufferTime = TimeSpan.Zero };
        _mss.SampleRequested += OnSampleRequested;
        _mss.Starting += (s, e) => e.Request.SetActualStartPosition(TimeSpan.Zero);

        MediaEncodingProfile profile = MediaEncodingProfile.CreateMp4(VideoEncodingQuality.HD720p);
        profile.Audio = null;
        profile.Video.Width = (uint)width;
        profile.Video.Height = (uint)height;
        profile.Video.Bitrate = 6_000_000;
        profile.Video.FrameRate.Numerator = 30;
        profile.Video.FrameRate.Denominator = 1;

        _transcoder = new MediaTranscoder { HardwareAccelerationEnabled = true };
        PrepareTranscodeResult prep = await _transcoder.PrepareMediaStreamSourceTranscodeAsync(_mss, _out, profile);
        if (!prep.CanTranscode)
            throw new InvalidOperationException("Windows cannot encode this video: " + prep.FailureReason);

        _started = true;
        _transcode = prep.TranscodeAsync().AsTask();
    }

    static MediaStreamSample Make(Item item)
    {
        var s = MediaStreamSample.CreateFromBuffer(item.Data.AsBuffer(), item.Ts);
        s.Duration = TimeSpan.FromMilliseconds(33);
        return s;
    }

    void OnSampleRequested(MediaStreamSource sender, MediaStreamSourceSampleRequestedEventArgs args)
    {
        var req = args.Request;
        lock (_gate)
        {
            if (_queue.Count > 0) { req.Sample = Make(_queue.Dequeue()); return; }
            if (_stopping) { req.Sample = null; return; }      // end of stream
            _pendingReq = req;                                 // no frame yet: answer when the next one arrives
            _pendingDef = req.GetDeferral();
        }
    }

    /// <summary>Hands one BGRA frame (Width x Height x 4 bytes) to the encoder. Cheap; never blocks.</summary>
    public void AddFrame(byte[] bgra)
    {
        MediaStreamSourceSampleRequest req = null;
        MediaStreamSourceSampleRequestDeferral def = null;
        MediaStreamSample sample = null;
        lock (_gate)
        {
            if (!_started || _stopping) return;
            if (_clock == null) _clock = Stopwatch.StartNew();
            var item = new Item { Data = bgra, Ts = _clock.Elapsed };
            Interlocked.Increment(ref _frames);
            if (_pendingDef != null)
            {
                sample = Make(item);
                req = _pendingReq; def = _pendingDef;
                _pendingReq = null; _pendingDef = null;
            }
            else if (_queue.Count < 90) _queue.Enqueue(item);   // encoder is behind: drop frames rather than grow forever
        }
        if (def != null) { req.Sample = sample; def.Complete(); }
    }

    /// <summary>Finishes the file. Returns true when a playable MP4 was written.</summary>
    public async Task<bool> StopAsync()
    {
        MediaStreamSourceSampleRequest req = null;
        MediaStreamSourceSampleRequestDeferral def = null;
        lock (_gate)
        {
            if (!_started || _stopping) return false;
            _stopping = true;
            if (_pendingDef != null) { req = _pendingReq; def = _pendingDef; _pendingReq = null; _pendingDef = null; }
        }
        if (def != null) { req.Sample = null; def.Complete(); }

        bool ok = Frames > 0;
        try
        {
            Task done = await Task.WhenAny(_transcode, Task.Delay(10000));
            if (done != _transcode) { ok = false; Error = "Encoding did not finish in time."; }
            else await _transcode;
        }
        catch (Exception ex) { ok = false; Error = ex.Message; }
        try { _out?.Dispose(); } catch { }
        return ok;
    }
}

