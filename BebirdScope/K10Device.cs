using System.Diagnostics;
using System.Runtime.InteropServices;
using DirectShowLib;

namespace BebirdScope;

internal enum DeviceState
{
    Searching,    // no K10 plugged in
    Starting,     // found, settling / opening / waiting for the first picture
    Live,         // picture is flowing
    NeedsReplug,  // K10 is present but not delivering video: it must be unplugged and plugged in again
    Blocked,      // could not open the camera (e.g. another program is using it)
    Disconnected  // K10 was unplugged while running: the app closes itself (see MainWindow)
}

internal enum SensorStatus { None, Ok, Flat, Lost }

internal sealed class SensorReading
{
    public double AngleDeg;     // roll angle from atan2(Z, Y), degrees (-180..180]
    public SensorStatus Status;
    public int X, Y, Z;         // filtered raw axes (X only filled while diagnostics are on)
    public double Magnitude;    // sqrt(Y^2 + Z^2)
    public long Seq;
}

/// <summary>
/// Owns the camera. One worker thread waits for the K10, lets it settle, builds the DirectShow
/// graph, polls the gyro gently once video is steady, and goes quiet before shutting down.
///
/// Known hardware quirk (measured): after the gyro has been read over the UVC extension unit, the K10
/// will not start video again until it is physically replugged. So when video does not come up, we do
/// not retry in a loop; we tell the user to replug and resume by ourselves when the device returns.
/// </summary>
internal sealed class K10Device : IDisposable
{
    const string K10Id = "vid_1e4e&pid_0110";

    // gyro filtering
    const int FlatThreshold = 2500;        // |(Y,Z)| below this: wand points up/down, roll undefined
    const int MaxPlausible = 28000;        // |(Y,Z)| above this: glitch, ignore sample
    const int SensorLostAfterFails = 30;   // ~1 s of failed reads -> report "lost"
    const int SensorGiveUpAfterFails = 100;// ~3 s -> stop talking to the gyro altogether

    // timing
    const int SettleMs = 2500;             // K10 must be present this long before we open it
    const int PollStartDelayMs = 2000;     // no gyro traffic until video has been flowing this long
    const int PollIntervalMs = 33;         // ~30 Hz
    const int QuietBeforeStopMs = 400;     // no gyro traffic this long before the video stream is stopped
    const int NoFrameFaultMs = 8000;
    const int StallFaultMs = 3000;
    const int MaxAttempts = 3;

    enum Outcome { Ended, NoVideo, Stalled, OpenFailed, Disconnected }

    readonly FrameStore _frames;
    Thread _thread;
    volatile bool _stop;
    volatile DeviceState _state = DeviceState.Searching;
    volatile string _detail = "";
    volatile SensorReading _latest;
    volatile bool _hasSensor;
    volatile string _cameraName = "";
    long _sensorErrors;
    long _glitches;
    long _sessions;

    public volatile bool DiagEnabled;

    public K10Device(FrameStore frames) { _frames = frames; }

    public DeviceState State => _state;
    public string Detail => _detail;
    public SensorReading Latest => _latest;
    public bool HasSensor => _hasSensor;
    public string CameraName => _cameraName;
    public long SensorErrors => Interlocked.Read(ref _sensorErrors);
    public long Glitches => Interlocked.Read(ref _glitches);
    public long Reconnects => Math.Max(0, Interlocked.Read(ref _sessions) - 1);

    public void Start()
    {
        _thread = new Thread(Run) { IsBackground = true, Name = "K10 worker" };
        _thread.SetApartmentState(ApartmentState.MTA);
        _thread.Start();
    }

    public void Dispose()
    {
        _stop = true;
        try { _thread?.Join(4000); } catch { }
    }

    /// <summary>Stop without waiting: used after an unplug, when the worker may be stuck inside a dead camera call.</summary>
    public void Abandon() { _stop = true; }

    // ---------------------------------------------------------------- device lookup

    static bool IsK10(string path) =>
        !string.IsNullOrEmpty(path) && path.IndexOf(K10Id, StringComparison.OrdinalIgnoreCase) >= 0;

    static bool IsK10Present()
    {
        DsDevice[] devs = null;
        try
        {
            devs = DsDevice.GetDevicesOfCat(FilterCategory.VideoInputDevice);
            return devs.Any(d => IsK10(d.DevicePath));
        }
        catch { return false; }
        finally { if (devs != null) foreach (var d in devs) d.Dispose(); }
    }

    static DsDevice FindDevice()
    {
        DsDevice[] devs = DsDevice.GetDevicesOfCat(FilterCategory.VideoInputDevice);
        DsDevice pick = devs.FirstOrDefault(d => IsK10(d.DevicePath));
        foreach (var d in devs)
            if (!ReferenceEquals(d, pick)) d.Dispose();
        return pick;
    }

    // ---------------------------------------------------------------- worker

    void SetState(DeviceState s, string detail)
    {
        if (_state != s || _detail != detail)
            Log.Write("State: " + s + (string.IsNullOrEmpty(detail) ? "" : " (" + detail + ")"));
        _detail = detail;
        _state = s;
    }

    /// <summary>Sleeps up to ms, returns early (false) when the app is closing.</summary>
    bool Wait(int ms)
    {
        var sw = Stopwatch.StartNew();
        while (!_stop && sw.ElapsedMilliseconds < ms) Thread.Sleep(30);
        return !_stop;
    }

    void Run()
    {
        Log.Write("=== Bebird Scope worker started ===");
        while (!_stop)
        {
            try { Cycle(); }
            catch (Exception ex)
            {
                Log.Write("Unexpected worker error: " + ex);
                SetState(DeviceState.Blocked, ex.Message);
                Wait(3000);
            }
        }
        Log.Write("=== worker stopped ===");
    }

    /// <summary>One plug-in lifetime: wait for the K10, settle, run; on failure wait for a replug.</summary>
    void Cycle()
    {
        _hasSensor = false;
        SetState(DeviceState.Searching, "");
        while (!_stop && !IsK10Present()) Wait(500);
        if (_stop) return;

        // The K10 needs a moment after enumeration. Opening it too early is a plausible cause of failed starts.
        SetState(DeviceState.Starting, "Camera found, getting ready…");
        var settle = Stopwatch.StartNew();
        while (!_stop && settle.ElapsedMilliseconds < SettleMs)
        {
            Wait(250);
            if (!IsK10Present()) { Log.Write("K10 vanished while settling"); return; }
        }
        if (_stop) return;

        for (int attempt = 1; attempt <= MaxAttempts && !_stop; attempt++)
        {
            Log.Write("Start attempt " + attempt + "/" + MaxAttempts);
            string detail;
            Outcome o = RunOnce(out detail);
            if (o == Outcome.Disconnected)
            {
                SetState(DeviceState.Disconnected, detail);
                _stop = true;      // this process is done with the camera; the UI closes the app
                return;
            }
            if (_stop || o == Outcome.Ended) return;

            if (!IsK10Present()) { Log.Write("K10 unplugged"); return; }

            if (attempt < MaxAttempts)
            {
                SetState(DeviceState.Starting, "Retrying…");
                if (!Wait(2000)) return;
                continue;
            }

            if (o == Outcome.OpenFailed)
            {
                SetState(DeviceState.Blocked, detail);
                Wait(3000);
                return; // loop around and try again later
            }

            SetState(DeviceState.NeedsReplug, detail);
            Log.Write("Giving up until the K10 is replugged");
            while (!_stop && IsK10Present()) Wait(500);   // wait for the unplug...
            return;                                         // ...then Cycle() waits for it to come back
        }
    }

    // gyro filter state (worker thread only)
    readonly short[] _ys = new short[3];
    readonly short[] _zs = new short[3];
    int _filled, _failStreak;
    double _lastAngle;
    long _seq;
    readonly Stopwatch _clock = Stopwatch.StartNew();
    long _lastLogMs;
    double _minMag = double.MaxValue, _maxMag;
    int _logSamples;

    void ResetFilter()
    {
        _filled = 0; _failStreak = 0; _lastAngle = 0; _minMag = double.MaxValue; _maxMag = 0; _logSamples = 0;
    }

    Outcome RunOnce(out string detail)
    {
        detail = "";
        DsDevice dev = null;
        CaptureSession s = null;
        bool abandon = false;   // true after an unplug: never call Stop/Release on the dead graph, it hangs
        try
        {
            dev = FindDevice();
            if (dev == null) { detail = "The camera is not available."; return Outcome.OpenFailed; }

            string name = dev.Name, path = dev.DevicePath ?? "";
            long baseCount = _frames.FramesReceived;   // counter is cumulative across sessions
            Log.Write("Opening '" + name + "'");
            try { s = new CaptureSession(dev, _frames, true); }
            finally { dev.Dispose(); dev = null; }

            _cameraName = name;
            _hasSensor = s.Sensor != null;
            _latest = null;
            ResetFilter();
            Interlocked.Increment(ref _sessions);
            Log.Write("Graph running: " + s.Width + "x" + s.Height + (s.Sensor != null ? ", gyro attached" : ", no gyro"));
            SetState(DeviceState.Starting, "");

            var sw = Stopwatch.StartNew();
            long lastCount = 0, lastChangeMs = 0, firstFrameMs = -1, nextPoll = 0, nextPresence = 1000;
            int missing = 0;
            bool sensorOn = s.Sensor != null, polledAny = false;

            while (!_stop)
            {
                long now = sw.ElapsedMilliseconds;
                long fc = _frames.FramesReceived - baseCount;
                if (fc != lastCount)
                {
                    lastCount = fc;
                    lastChangeMs = now;
                    if (firstFrameMs < 0) { firstFrameMs = now; Log.Write("First picture after " + now + " ms"); }
                }

                if (now >= nextPresence)
                {
                    nextPresence = now + 500;
                    if (IsK10Present()) missing = 0;
                    else if (++missing >= 2) { detail = "The K10 was unplugged."; abandon = true; return Outcome.Disconnected; }
                }

                if (fc == 0 && now > NoFrameFaultMs) { detail = "The camera is not sending any picture."; return Outcome.NoVideo; }
                if (fc > 0 && now - lastChangeMs > StallFaultMs) { detail = "The picture stopped."; return Outcome.Stalled; }
                if (fc > 0 && _state != DeviceState.Live) SetState(DeviceState.Live, "");

                // gyro traffic only after video has been steady for a while
                if (sensorOn && firstFrameMs >= 0 && now - firstFrameMs >= PollStartDelayMs && now >= nextPoll)
                {
                    polledAny = true;
                    if (!PollSensor(s.Sensor))
                    {
                        sensorOn = false;
                        Log.Write("Gyro polling stopped for this session");
                    }
                    nextPoll = sw.ElapsedMilliseconds + PollIntervalMs;
                }

                Thread.Sleep(10);
            }

            // closing: let the gyro bus go quiet before the video stream is stopped
            if (polledAny) Thread.Sleep(QuietBeforeStopMs);
            return Outcome.Ended;
        }
        catch (Exception ex)
        {
            Log.Write("Open failed: " + ex.GetType().Name + ": " + ex.Message);
            detail = "Could not open the camera: " + ex.Message;
            return Outcome.OpenFailed;
        }
        finally
        {
            try { dev?.Dispose(); } catch { }
            try { if (!abandon) s?.Dispose(); } catch (Exception ex) { Log.Write("Teardown error: " + ex.Message); }
        }
    }

    static short Median3(short a, short b, short c)
    {
        if (a > b) (a, b) = (b, a);
        if (b > c) (b, c) = (c, b);
        if (a > b) (a, b) = (b, a);
        return b;
    }

    void Publish(SensorStatus st, double angle, short x, short y, short z, double mag)
    {
        _latest = new SensorReading
        {
            AngleDeg = angle, Status = st, X = x, Y = y, Z = z, Magnitude = mag, Seq = ++_seq
        };
    }

    /// <summary>Returns false when the gyro has stopped answering and polling should be abandoned.</summary>
    bool PollSensor(K10Sensor sensor)
    {
        short y = 0, z = 0, x = 0;
        bool ok;
        try
        {
            ok = sensor.TryRead16(0x04, out y) && sensor.TryRead16(0x06, out z);
            if (ok && DiagEnabled) sensor.TryRead16(0x02, out x);
        }
        catch (Exception ex)
        {
            ok = false;
            Log.Write("Gyro read exception: " + ex.Message);
        }

        if (!ok)
        {
            Interlocked.Increment(ref _sensorErrors);
            _failStreak++;
            if (_failStreak == SensorLostAfterFails)
            {
                Log.Write("Gyro not answering (hr=0x" + sensor.LastHr.ToString("X8") + ")");
                Publish(SensorStatus.Lost, _lastAngle, 0, 0, 0, 0);
            }
            return _failStreak < SensorGiveUpAfterFails;
        }
        _failStreak = 0;

        // median-of-3 on each axis removes single-sample spikes
        int slot = _filled % 3;
        _ys[slot] = y; _zs[slot] = z;
        _filled++;
        if (_filled < 3) return true;

        short my = Median3(_ys[0], _ys[1], _ys[2]);
        short mz = Median3(_zs[0], _zs[1], _zs[2]);
        double mag = Math.Sqrt((double)my * my + (double)mz * mz);

        long ms = _clock.ElapsedMilliseconds;
        if (mag < _minMag) _minMag = mag;
        if (mag > _maxMag) _maxMag = mag;
        if (_logSamples < 8 || ms - _lastLogMs >= 10000)
        {
            Log.Write(string.Format("gyro y={0} z={1} |yz|={2:F0} angle={3:F1} (|yz| min/max {4:F0}/{5:F0}, errors {6}, glitches {7})",
                my, mz, mag, Math.Atan2(mz, my) * 180.0 / Math.PI, _minMag, _maxMag, SensorErrors, Glitches));
            _lastLogMs = ms;
            _logSamples++;
            _minMag = double.MaxValue; _maxMag = 0;
        }

        if (mag > MaxPlausible)
        {
            Interlocked.Increment(ref _glitches);
            return true; // keep the last good reading
        }

        if (mag < FlatThreshold)
        {
            Publish(SensorStatus.Flat, _lastAngle, x, my, mz, mag);
            return true;
        }

        _lastAngle = Math.Atan2(mz, my) * 180.0 / Math.PI;
        Publish(SensorStatus.Ok, _lastAngle, x, my, mz, mag);
        return true;
    }
}

/// <summary>One running DirectShow graph: camera -> SampleGrabber(RGB24) -> NullRenderer.</summary>
internal sealed class CaptureSession : IDisposable
{
    IFilterGraph2 _graph;
    ICaptureGraphBuilder2 _builder;
    IBaseFilter _cap, _grabF, _nullR;
    ISampleGrabber _grab;
    IMediaControl _ctrl;
    FrameGrabber _cb;

    public K10Sensor Sensor { get; private set; }
    public int Width { get; private set; }
    public int Height { get; private set; }

    static void Check(int hr) { DsError.ThrowExceptionForHR(hr); }

    public CaptureSession(DsDevice dev, FrameStore frames, bool wantSensor, int formatIndex = -1, bool run = true)
    {
        try
        {
            _graph = (IFilterGraph2)new FilterGraph();
            _builder = (ICaptureGraphBuilder2)new CaptureGraphBuilder2();
            Check(_builder.SetFiltergraph(_graph));
            Check(_graph.AddSourceFilterForMoniker(dev.Mon, null, dev.Name, out _cap));

            _grab = (ISampleGrabber)new SampleGrabber();
            _grabF = (IBaseFilter)_grab;
            var want = new AMMediaType
            {
                majorType = MediaType.Video,
                subType = MediaSubType.RGB24,
                formatType = FormatType.VideoInfo
            };
            Check(_grab.SetMediaType(want));
            DsUtils.FreeAMMediaType(want);
            Check(_graph.AddFilter(_grabF, "Grabber"));

            _nullR = (IBaseFilter)new NullRenderer();
            Check(_graph.AddFilter(_nullR, "Null renderer"));

            if (formatIndex >= 0)
            {
                object o;
                int hr0 = _builder.FindInterface(PinCategory.Capture, MediaType.Video, _cap, typeof(IAMStreamConfig).GUID, out o);
                var cfg = o as IAMStreamConfig;
                if (hr0 == 0 && cfg != null)
                {
                    cfg.GetNumberOfCapabilities(out int count, out int size);
                    IntPtr scc = Marshal.AllocCoTaskMem(size);
                    try
                    {
                        AMMediaType fmt;
                        if (formatIndex < count && cfg.GetStreamCaps(formatIndex, out fmt, scc) == 0)
                        {
                            Check(cfg.SetFormat(fmt));
                            DsUtils.FreeAMMediaType(fmt);
                        }
                    }
                    finally { Marshal.FreeCoTaskMem(scc); }
                }
            }

            Check(_builder.RenderStream(PinCategory.Capture, MediaType.Video, _cap, _grabF, _nullR));

            var got = new AMMediaType();
            Check(_grab.GetConnectedMediaType(got));
            try
            {
                if (got.formatType != FormatType.VideoInfo || got.formatPtr == IntPtr.Zero)
                    throw new InvalidOperationException("Unsupported video format");
                var vih = (VideoInfoHeader)Marshal.PtrToStructure(got.formatPtr, typeof(VideoInfoHeader));
                Width = vih.BmiHeader.Width;
                int h = vih.BmiHeader.Height;
                Height = Math.Abs(h);
                frames.Configure(Width, Height);
                _cb = new FrameGrabber(frames, h > 0);
            }
            finally { DsUtils.FreeAMMediaType(got); }

            Check(_grab.SetBufferSamples(false));
            Check(_grab.SetOneShot(false));
            Check(_grab.SetCallback(_cb, 1));

            if (wantSensor)
            {
                var ks = _cap as IKsControl;
                if (ks != null) Sensor = new K10Sensor(ks);
                else Log.Write("Camera filter does not expose IKsControl");
            }

            _ctrl = (IMediaControl)_graph;
            if (run) Check(_ctrl.Run());
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        try { _ctrl?.Stop(); } catch { }
        try { _grab?.SetCallback(null, 1); } catch { }
        try { Sensor?.Dispose(); } catch { }
        Sensor = null;

        try { if (_graph != null && _cap != null) _graph.RemoveFilter(_cap); } catch { }
        try { if (_graph != null && _grabF != null) _graph.RemoveFilter(_grabF); } catch { }
        try { if (_graph != null && _nullR != null) _graph.RemoveFilter(_nullR); } catch { }

        Release(ref _ctrl);
        Release(ref _grab);
        Release(ref _nullR);
        Release(ref _cap);
        Release(ref _builder);
        Release(ref _graph);
        _grabF = null;
        _cb = null;
    }

    static void Release<T>(ref T com) where T : class
    {
        if (com != null)
        {
            try { if (Marshal.IsComObject(com)) Marshal.FinalReleaseComObject(com); } catch { }
            com = null;
        }
    }

    sealed class FrameGrabber : ISampleGrabberCB
    {
        readonly FrameStore _store;
        readonly bool _bottomUp;
        public FrameGrabber(FrameStore store, bool bottomUp) { _store = store; _bottomUp = bottomUp; }

        public int SampleCB(double sampleTime, IMediaSample sample)
        {
            Marshal.ReleaseComObject(sample);
            return 0;
        }

        public int BufferCB(double sampleTime, IntPtr buffer, int bufferLen)
        {
            try { _store.Push(buffer, bufferLen, _bottomUp); } catch { }
            return 0;
        }
    }
}
