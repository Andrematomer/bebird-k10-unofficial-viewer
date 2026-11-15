using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using DirectShowLib;

namespace BebirdScope;

/// <summary>
/// Headless reliability experiment:  BebirdScope.exe --selftest mode=none|poll start=0 interval=18 trials=5 secs=6 out=path
/// Opens the K10, streams for N seconds (optionally polling the sensor), tears down, repeats.
/// </summary>
internal static class SelfTest
{
    public static void Run(string[] args)
    {
        var o = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var a in args.Skip(1))
        {
            int i = a.IndexOf('=');
            if (i > 0) o[a.Substring(0, i)] = a.Substring(i + 1);
        }
        string mode = o.TryGetValue("mode", out var m) ? m : "none";
        int start = o.TryGetValue("start", out var st) ? int.Parse(st) : 0;
        int interval = o.TryGetValue("interval", out var iv) ? int.Parse(iv) : 18;
        int trials = o.TryGetValue("trials", out var tr) ? int.Parse(tr) : 5;
        int secs = o.TryGetValue("secs", out var se) ? int.Parse(se) : 6;
        int gap = o.TryGetValue("gap", out var gp) ? int.Parse(gp) : 1500;
        string outPath = o.TryGetValue("out", out var op) ? op : Path.Combine(Log.Dir, "selftest.txt");
        int fmt = o.TryGetValue("fmt", out var fm) ? int.Parse(fm) : -1;
        int stopBefore = o.TryGetValue("stopbefore", out var sb4) ? int.Parse(sb4) : 0;

        void Emit(string line)
        {
            Log.Write("[selftest] " + line);
            File.AppendAllText(outPath, line + Environment.NewLine);
        }

        Emit($"--- mode={mode} fmt={fmt} start={start}ms stopbefore={stopBefore}ms interval={interval}ms trials={trials} secs={secs} gap={gap}ms ---");
        if (mode == "formats") { ListFormats(Emit); Emit("--- done ---"); return; }
        if (mode == "ladder") { Ladder(Emit); Emit("--- done ---"); return; }

        for (int t = 1; t <= trials; t++)
        {
            var frames = new FrameStore();
            CaptureSession s = null;
            DsDevice dev = null;
            var total = Stopwatch.StartNew();
            try
            {
                var devs = DsDevice.GetDevicesOfCat(FilterCategory.VideoInputDevice);
                dev = devs.FirstOrDefault(d => d.DevicePath != null &&
                    d.DevicePath.IndexOf("vid_1e4e&pid_0110", StringComparison.OrdinalIgnoreCase) >= 0);
                foreach (var d in devs) if (!ReferenceEquals(d, dev)) d.Dispose();
                if (dev == null) { Emit($"trial {t}: NO DEVICE"); break; }

                s = new CaptureSession(dev, frames, mode == "poll", fmt);
                dev.Dispose(); dev = null;
                long openMs = total.ElapsedMilliseconds;

                var per = new List<long>();
                long last = 0, okReads = 0, badReads = 0, nextSec = 1000;
                var run = Stopwatch.StartNew();
                while (run.ElapsedMilliseconds < secs * 1000L)
                {
                    if (mode == "poll" && s.Sensor != null && run.ElapsedMilliseconds >= start &&
                        run.ElapsedMilliseconds < secs * 1000L - stopBefore)
                    {
                        bool ok = s.Sensor.TryRead16(0x04, out _) && s.Sensor.TryRead16(0x06, out _);
                        if (ok) okReads++; else badReads++;
                        Thread.Sleep(interval);
                    }
                    else Thread.Sleep(10);

                    if (run.ElapsedMilliseconds >= nextSec)
                    {
                        long c = frames.FramesReceived;
                        per.Add(c - last); last = c; nextSec += 1000;
                    }
                }

                var td = Stopwatch.StartNew();
                s.Dispose(); s = null;
                long teardownMs = td.ElapsedMilliseconds;

                string result = per.Sum() == 0 ? "NOFRAMES" : (per.Skip(1).Any(x => x < 3) ? "STALL" : "OK");
                Emit($"trial {t}: {result,-8} fps/sec=[{string.Join(",", per)}] open={openMs}ms teardown={teardownMs}ms sensorOk={okReads} sensorBad={badReads}");
            }
            catch (Exception ex)
            {
                Emit($"trial {t}: EXCEPTION {ex.GetType().Name}: {ex.Message}");
            }
            finally
            {
                try { dev?.Dispose(); } catch { }
                try { s?.Dispose(); } catch { }
            }
            Thread.Sleep(gap);
        }
        Emit("--- done ---");
    }

    static DsDevice FindK10()
    {
        var devs = DsDevice.GetDevicesOfCat(FilterCategory.VideoInputDevice);
        var dev = devs.FirstOrDefault(d => d.DevicePath != null &&
            d.DevicePath.IndexOf("vid_1e4e&pid_0110", StringComparison.OrdinalIgnoreCase) >= 0);
        foreach (var d in devs) if (!ReferenceEquals(d, dev)) d.Dispose();
        return dev;
    }

    /// <summary>One step: open the camera (optionally without streaming), run body for ms, close. Returns frame count.</summary>
    static long Step(Action<string> emit, string name, bool wantSensor, bool run, int ms, Action<CaptureSession, Stopwatch> body)
    {
        var frames = new FrameStore();
        DsDevice dev = null;
        CaptureSession s = null;
        try
        {
            dev = FindK10();
            if (dev == null) { emit($"{name}: NO DEVICE"); return -1; }
            s = new CaptureSession(dev, frames, wantSensor, -1, run);
            dev.Dispose(); dev = null;
            var sw = Stopwatch.StartNew();
            body?.Invoke(s, sw);
            while (sw.ElapsedMilliseconds < ms) Thread.Sleep(20);
            long fr = frames.FramesReceived;
            s.Dispose(); s = null;
            emit($"{name}: frames={fr}");
            return fr;
        }
        catch (Exception ex) { emit($"{name}: EXCEPTION {ex.Message}"); return -2; }
        finally { try { dev?.Dispose(); } catch { } try { s?.Dispose(); } catch { } }
    }

    static void Pair(CaptureSession s) { s.Sensor?.TryRead16(0x04, out _); s.Sensor?.TryRead16(0x06, out _); }

    static void Ladder(Action<string> emit)
    {
        int check = 0;
        bool Check()
        {
            check++;
            long fr = Step(emit, $"CHECK {check} (video only, 3 s)", false, true, 3000, null);
            Thread.Sleep(1500);
            if (fr < 40) { emit($"*** WEDGED at check {check}: the step right before it is the trigger ***"); return false; }
            return true;
        }

        if (!Check()) return;

        Step(emit, "STEP A: gyro reads, NO video streaming (50 pairs)", true, false, 1500, (s, sw) =>
        { for (int i = 0; i < 50; i++) { Pair(s); Thread.Sleep(20); } });
        Thread.Sleep(1500);
        if (!Check()) return;

        Step(emit, "STEP B: streaming + ONE gyro read pair at 1.5 s", true, true, 3000, (s, sw) =>
        { Thread.Sleep(1500); Pair(s); });
        Thread.Sleep(1500);
        if (!Check()) return;

        Step(emit, "STEP C: streaming + 1 s burst of reads (about 30 pairs)", true, true, 4500, (s, sw) =>
        { Thread.Sleep(1500); var b = Stopwatch.StartNew(); while (b.ElapsedMilliseconds < 1000) { Pair(s); Thread.Sleep(33); } });
        Thread.Sleep(1500);
        if (!Check()) return;

        Step(emit, "STEP D: streaming + continuous reads for 5 s", true, true, 5000, (s, sw) =>
        { while (sw.ElapsedMilliseconds < 4800) { Pair(s); Thread.Sleep(33); } });
        Thread.Sleep(1500);
        Check();
    }

    static void ListFormats(Action<string> emit)
    {
        var devs = DsDevice.GetDevicesOfCat(FilterCategory.VideoInputDevice);
        var dev = devs.FirstOrDefault(d => d.DevicePath != null &&
            d.DevicePath.IndexOf("vid_1e4e&pid_0110", StringComparison.OrdinalIgnoreCase) >= 0);
        foreach (var d in devs) if (!ReferenceEquals(d, dev)) d.Dispose();
        if (dev == null) { emit("formats: NO DEVICE"); return; }

        var graph = (IFilterGraph2)new FilterGraph();
        var builder = (ICaptureGraphBuilder2)new CaptureGraphBuilder2();
        builder.SetFiltergraph(graph);
        IBaseFilter cap;
        DsError.ThrowExceptionForHR(graph.AddSourceFilterForMoniker(dev.Mon, null, dev.Name, out cap));
        dev.Dispose();

        object o;
        int hr = builder.FindInterface(PinCategory.Capture, MediaType.Video, cap, typeof(IAMStreamConfig).GUID, out o);
        var cfg = o as IAMStreamConfig;
        if (hr != 0 || cfg == null) emit("no IAMStreamConfig, hr=0x" + hr.ToString("X8"));
        else
        {
            cfg.GetNumberOfCapabilities(out int count, out int size);
            emit($"formats: {count} (caps size {size})");
            IntPtr scc = Marshal.AllocCoTaskMem(size);
            for (int i = 0; i < count; i++)
            {
                AMMediaType mt;
                if (cfg.GetStreamCaps(i, out mt, scc) != 0) continue;
                string desc = Encoding.ASCII.GetString(mt.subType.ToByteArray(), 0, 4);
                if (mt.formatType == FormatType.VideoInfo && mt.formatPtr != IntPtr.Zero)
                {
                    var vih = (VideoInfoHeader)Marshal.PtrToStructure(mt.formatPtr, typeof(VideoInfoHeader));
                    double fps = vih.AvgTimePerFrame > 0 ? 1e7 / vih.AvgTimePerFrame : 0;
                    desc += $" {vih.BmiHeader.Width}x{vih.BmiHeader.Height} @{fps:0.#}fps";
                }
                emit($"  [{i}] {desc}");
                DsUtils.FreeAMMediaType(mt);
            }
            Marshal.FreeCoTaskMem(scc);
        }
        try { graph.RemoveFilter(cap); } catch { }
        try { Marshal.FinalReleaseComObject(cap); } catch { }
        try { Marshal.FinalReleaseComObject(builder); } catch { }
        try { Marshal.FinalReleaseComObject(graph); } catch { }
    }
}
