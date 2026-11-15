using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace BebirdScope;

public partial class MainWindow : Window
{
    readonly Settings _s;
    readonly FrameStore _frames = new FrameStore();
    readonly K10Device _device;
    readonly DispatcherTimer _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
    ScaleTransform _uiScaleT;
    double _uiScale = 1.0;

    WriteableBitmap _bmp;
    long _lastFrameId;
    bool _loading = true;
    bool _frozen;
    bool _fullscreen;
    WindowState _prevState;
    WindowStyle _prevStyle;

    int _frameW = 640, _frameH = 480;

    // angle pipeline: raw sensor angle -> invert -> target -> smoothed display angle
    double _rawAngle;
    long _lastSeq = -1;
    double _target;
    double _disp;
    TimeSpan _lastRender = TimeSpan.MinValue;

    long _fpsPrev;
    DateTime _fpsPrevTime = DateTime.UtcNow;
    double _fps;

    // disconnect watchdog (runs on the UI thread, independent of the camera worker, which can hang)
    bool _wasLive;
    long _wdCount;
    DateTime _wdChange = DateTime.UtcNow;
    bool _closing;
    Timer _exitTimer;

    // recording
    Recorder _rec;
    bool _recBusy;
    int _recW, _recH;
    bool _recCircle;
    RenderTargetBitmap _recRtb;
    BitmapSource _recFirstFrame;

    // latest capture (thumbnail)
    string _latestPath;

    // slow-picture (USB 2) warning
    readonly Queue<(DateTime t, long c)> _fpsHist = new Queue<(DateTime t, long c)>();
    DateTime _liveSince = DateTime.MinValue;
    double _avgFps;
    bool _warnedThisSession;

    // hidden developer switches
    //   --demo            test pattern + swinging rotation, no camera needed
    //   --demo-slow       same, but only ~12 fps (to see the slow-picture warning)
    //   --demo-autorecord same, records 5 s of video by itself and exits (recording test)
    //   --savedir=PATH    save folder override (demo runs do not write settings)
    readonly bool _demo, _demoSlow, _demoAutoRec;

    static readonly Brush Green = new SolidColorBrush(Color.FromRgb(0x3D, 0xDC, 0x97));
    static readonly Brush Amber = new SolidColorBrush(Color.FromRgb(0xE0, 0xA0, 0x30));
    static readonly Brush Red = new SolidColorBrush(Color.FromRgb(0xE5, 0x57, 0x4F));

    public MainWindow()
    {
        string[] args = Environment.GetCommandLineArgs();
        _demoAutoRec = args.Contains("--demo-autorecord");
        _demoSlow = args.Contains("--demo-slow");
        _demo = args.Contains("--demo") || _demoSlow || _demoAutoRec;

        InitializeComponent();
        _uiScaleT = (ScaleTransform)FindResource("UiScale");

        _s = Settings.Load();
        foreach (string a in args)
            if (a.StartsWith("--savedir=")) _s.SaveFolder = a.Substring("--savedir=".Length).Trim('"');

        Width = Math.Max(MinWidth, Math.Min(_s.Width, SystemParameters.WorkArea.Width));
        Height = Math.Max(MinHeight, Math.Min(_s.Height, SystemParameters.WorkArea.Height));

        BtnLevel.IsChecked = _s.AutoLevel;
        BtnEarL.IsChecked = !_s.Mirror;
        BtnEarR.IsChecked = _s.Mirror;
        BtnCircle.IsChecked = _s.Circle;
        PathBox.Text = _s.EffectiveSaveFolder();
        _loading = false;

        ApplyFrameSize(_frameW, _frameH);
        ApplyTransform();
        RefreshLatest();

        _device = new K10Device(_frames);
        if (_demo) StartDemo(); else _device.Start();

        CompositionTarget.Rendering += OnRender;
        _timer.Tick += (s, e) => UpdateStatus();
        _timer.Start();
        Closing += OnClosing;
        Loaded += (s, e) => LayoutVideo();
    }

    // ------------------------------------------------------------------ demo mode

    void StartDemo()
    {
        const int W = 640, H = 480;
        var buf = new byte[W * H * 3];
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
            {
                int i = (y * W + x) * 3;
                byte b = 120, g = (byte)(y * 255 / (H - 1)), r = (byte)(x * 255 / (W - 1));
                if (x % 40 == 0 || y % 40 == 0) { b = 30; g = 30; r = 30; }
                if (Math.Abs(x - W / 2) < 2 || Math.Abs(y - H / 2) < 2) { b = 255; g = 255; r = 255; }
                if (y < 14) { b = 255; g = 255; r = 255; }          // white bar = top of the picture
                if (x < 14) { b = 40; g = 40; r = 255; }            // red bar = left edge
                buf[i] = b; buf[i + 1] = g; buf[i + 2] = r;
            }
        _frames.Configure(W, H);
        IntPtr p = Marshal.AllocHGlobal(buf.Length);
        Marshal.Copy(buf, 0, p, buf.Length);
        var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(_demoSlow ? 83 : 33) };
        t.Tick += (s, e) => _frames.Push(p, buf.Length, false);
        t.Start();

        if (_demoAutoRec)
        {
            var t1 = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
            t1.Tick += async (s, e) =>
            {
                t1.Stop();
                await StartRecordingAsync();
                var t2 = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
                t2.Tick += async (s2, e2) =>
                {
                    t2.Stop();
                    string path = _rec?.Path;
                    await StopRecordingAsync();
                    long size = (path != null && File.Exists(path)) ? new FileInfo(path).Length : -1;
                    Log.Write("AUTOREC result: path=" + path + " bytes=" + size);
                    Close();
                };
                t2.Start();
            };
            t1.Start();
        }
    }

    // ------------------------------------------------------------------ window chrome

    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        try
        {
            IntPtr h = new WindowInteropHelper(this).Handle;
            int dark = 1;
            DwmSetWindowAttribute(h, 20, ref dark, 4);          // dark title bar
            int caption = 0x00000000;                           // black caption (Windows 11)
            DwmSetWindowAttribute(h, 35, ref caption, 4);
        }
        catch { }
    }

    void OnClosing(object sender, System.ComponentModel.CancelEventArgs e)
    {
        CompositionTarget.Rendering -= OnRender;
        _timer.Stop();
        StopRecordingBlocking();   // always finish an MP4 properly, otherwise it is unplayable
        if (WindowState == WindowState.Normal) { _s.Width = ActualWidth; _s.Height = ActualHeight; }
        if (!_demo) _s.Save();
        if (_closing) _device.Abandon(); else _device.Dispose();
    }

    // ------------------------------------------------------------------ layout: biggest possible circle, UI only in the free corners

    void Layout_Changed(object sender, SizeChangedEventArgs e) => LayoutVideo();

    static double DistanceToRect(Point c, Rect r)
    {
        double dx = Math.Max(Math.Max(r.Left - c.X, 0), c.X - r.Right);
        double dy = Math.Max(Math.Max(r.Top - c.Y, 0), c.Y - r.Bottom);
        return Math.Sqrt(dx * dx + dy * dy);
    }

    void LayoutVideo()
    {
        if (RootGrid == null || VideoBox == null || _uiScaleT == null) return;
        double w = RootGrid.ActualWidth, h = RootGrid.ActualHeight;
        if (w < 50 || h < 50) return;

        // all controls grow with the window so they stay readable on a 4K screen
        double scale = Math.Max(1.0, Math.Min(2.6, Math.Min(w / 1000.0, h / 700.0)));
        if (Math.Abs(scale - _uiScale) > 0.01)
        {
            _uiScale = scale;
            _uiScaleT.ScaleX = scale;
            _uiScaleT.ScaleY = scale;
            var m = new Thickness(14 * scale);
            HostLeft.Margin = m; HostRight.Margin = m; HostBR.Margin = m; HostStatus.Margin = m;
        }

        var hosts = new FrameworkElement[] { HostLeft, HostRight, HostBR, HostStatus };

        if (_s.Circle)
        {
            // largest circle that fits the window without touching any control
            var rects = new List<Rect>();
            foreach (var p in hosts)
            {
                if (p.ActualWidth <= 0 || p.ActualHeight <= 0) continue;
                Point pt = p.TranslatePoint(new Point(0, 0), RootGrid);
                rects.Add(new Rect(pt.X, pt.Y, p.ActualWidth, p.ActualHeight));
            }
            var center = new Point(w / 2, h / 2);
            double d = Math.Min(w, h);
            while (d > 160)
            {
                double r = d / 2 + 6;
                if (!rects.Any(rc => DistanceToRect(center, rc) < r)) break;
                d -= 4;
            }
            VideoBox.Width = d;
            VideoBox.Height = d;
        }
        else
        {
            double side = Math.Max(HostLeft.ActualWidth, Math.Max(HostRight.ActualWidth, HostBR.ActualWidth)) + 32;
            VideoBox.Width = Math.Max(160, w - 2 * side);
            VideoBox.Height = h;
        }
    }

    // ------------------------------------------------------------------ rendering loop

    void OnRender(object sender, EventArgs e)
    {
        var args = (RenderingEventArgs)e;
        if (args.RenderingTime == _lastRender) return;
        double dt = _lastRender == TimeSpan.MinValue ? 1.0 / 60 : (args.RenderingTime - _lastRender).TotalSeconds;
        _lastRender = args.RenderingTime;
        if (dt <= 0 || dt > 0.25) dt = 1.0 / 60;

        bool newFrame = false;
        if (!_frozen)
        {
            if (_frames.Blit(ref _lastFrameId, ref _bmp, out bool sizeChanged))
            {
                newFrame = true;
                if (sizeChanged)
                {
                    VideoImage.Source = _bmp;
                    ApplyFrameSize(_bmp.PixelWidth, _bmp.PixelHeight);
                }
                if (VideoBox.Visibility != Visibility.Visible) VideoBox.Visibility = Visibility.Visible;
            }

            var r = _device.Latest;
            if (r != null && r.Status == SensorStatus.Ok && r.Seq != _lastSeq)
            {
                _lastSeq = r.Seq;
                _rawAngle = r.AngleDeg;
            }
            _target = (_s.AutoLevel && _device.HasSensor && _lastSeq >= 0) ? EffectiveAngle(_rawAngle) : 0.0;
            if (_demo) _target = _s.AutoLevel ? 25 * Math.Sin(args.RenderingTime.TotalSeconds * 0.8) : 0.0;
        }

        // calm for hand tremor, quick for real rotations
        double diff = Normalize(_target - _disp);
        double tau = Math.Abs(diff) > 25 ? 0.04 : 0.09;
        _disp = Normalize(_disp + diff * (1 - Math.Exp(-dt / tau)));

        RotateT.Angle = _disp;

        if (newFrame && _rec != null && _bmp != null) SubmitRecordingFrame();
    }

    double EffectiveAngle(double raw)
    {
        double a = Normalize(raw);
        return _s.Invert ? -a : a;
    }

    static double Normalize(double a)
    {
        while (a > 180.0) a -= 360.0;
        while (a <= -180.0) a += 360.0;
        return a;
    }

    // ------------------------------------------------------------------ view settings

    void ApplyTransform()
    {
        if (_loading) return;
        ScaleT.ScaleX = _s.Mirror ? -1 : 1;
        ScaleT.ScaleY = 1;
    }

    void ApplyFrameSize(int w, int h)
    {
        _frameW = w; _frameH = h;

        if (_s != null && _s.Circle)
        {
            // square stage = the circle's bounding box; the wider picture is centred and cropped by the clip
            double sq = Math.Min(w, h);
            StageGrid.Width = sq;
            StageGrid.Height = sq;
            ClipGrid.Clip = new EllipseGeometry(new Point(sq / 2.0, sq / 2.0), sq / 2.0, sq / 2.0);
        }
        else
        {
            StageGrid.Width = w;
            StageGrid.Height = h;
            ClipGrid.Clip = new RectangleGeometry(new Rect(0, 0, w, h));
        }
        LayoutVideo();
    }

    void SetLevel(bool v) { _s.AutoLevel = v; BtnLevel.IsChecked = v; Toast(v ? "Auto-level on" : "Auto-level off"); }
    void SetMirror(bool v) { _s.Mirror = v; BtnEarL.IsChecked = !v; BtnEarR.IsChecked = v; ApplyTransform(); }
    void SetCircle(bool v) { _s.Circle = v; BtnCircle.IsChecked = v; ApplyFrameSize(_frameW, _frameH); }

    void SetFreeze(bool v)
    {
        _frozen = v;
        BtnFreeze.IsChecked = v;
        Toast(v ? "Paused" : "Live");
    }

    void ToggleFullscreen()
    {
        if (!_fullscreen)
        {
            _prevState = WindowState; _prevStyle = WindowStyle;
            WindowStyle = WindowStyle.None;
            WindowState = WindowState.Maximized;
            _fullscreen = true;
        }
        else
        {
            WindowStyle = _prevStyle;
            WindowState = _prevState;
            _fullscreen = false;
        }
    }

    // ------------------------------------------------------------------ UI events

    void Level_Click(object s, RoutedEventArgs e) => SetLevel(BtnLevel.IsChecked == true);
    void EarL_Click(object s, RoutedEventArgs e) => SetMirror(false);
    void EarR_Click(object s, RoutedEventArgs e) => SetMirror(true);
    void Circle_Click(object s, RoutedEventArgs e) => SetCircle(BtnCircle.IsChecked == true);
    void Freeze_Click(object s, RoutedEventArgs e) => SetFreeze(BtnFreeze.IsChecked == true);
    void Snap_Click(object s, RoutedEventArgs e) => Snapshot();
    void Full_Click(object s, RoutedEventArgs e) => ToggleFullscreen();
    async void Rec_Click(object s, RoutedEventArgs e) => await ToggleRecordAsync();

    void Video_MouseDown(object s, MouseButtonEventArgs e)
    {
        Keyboard.ClearFocus();      // leave the path box
        Focus();
        if (e.ClickCount == 2) ToggleFullscreen();
    }

    protected override async void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        if (e.OriginalSource is TextBox) return;     // typing in the path box must not trigger shortcuts
        switch (e.Key)
        {
            case Key.S: SetLevel(!_s.AutoLevel); break;
            case Key.M: SetMirror(!_s.Mirror); break;
            case Key.C: SetCircle(!_s.Circle); break;
            case Key.P:
            case Key.F: SetFreeze(!_frozen); break;
            case Key.R: e.Handled = true; await ToggleRecordAsync(); return;
            case Key.Space: Snapshot(); break;
            case Key.I:
                _s.Invert = !_s.Invert;
                Toast(_s.Invert ? "Rotation direction reversed" : "Rotation direction normal");
                break;
            case Key.F11: ToggleFullscreen(); break;
            case Key.Escape: if (_fullscreen) ToggleFullscreen(); break;
            case Key.F12:
                _device.DiagEnabled = DiagBox.Visibility != Visibility.Visible;
                DiagBox.Visibility = _device.DiagEnabled ? Visibility.Visible : Visibility.Collapsed;
                break;
            default: return;
        }
        e.Handled = true;
    }

    // ------------------------------------------------------------------ status, slow-picture warning, disconnect handling

    void UpdateStatus()
    {
        var now = DateTime.UtcNow;
        long cnt = _frames.FramesReceived;
        double span = (now - _fpsPrevTime).TotalSeconds;
        if (span >= 0.9) { _fps = (cnt - _fpsPrev) / span; _fpsPrev = cnt; _fpsPrevTime = now; }

        var st = _device.State;
        bool live = _demo || st == DeviceState.Live;

        // 5-second average frame rate -> slow-picture warning
        if (live) { if (_liveSince == DateTime.MinValue) _liveSince = now; }
        else { _liveSince = DateTime.MinValue; _fpsHist.Clear(); }
        _fpsHist.Enqueue((now, cnt));
        while (_fpsHist.Count > 0 && (now - _fpsHist.Peek().t).TotalSeconds > 5.5) _fpsHist.Dequeue();
        double histSpan = _fpsHist.Count > 1 ? (now - _fpsHist.Peek().t).TotalSeconds : 0;
        _avgFps = histSpan > 0 ? (cnt - _fpsHist.Peek().c) / histSpan : 0;
        if (live && !_warnedThisSession && histSpan >= 4.5 && _avgFps < 22 &&
            (now - _liveSince).TotalSeconds > 8 &&
            (_s.Usb2WarnSnoozeUntil == null || DateTime.Now > _s.Usb2WarnSnoozeUntil.Value))
        {
            ShowSlowWarning();
        }

        if (_rec != null)
        {
            RecBadge.Visibility = Visibility.Visible;
            RecText.Text = "REC " + _rec.Elapsed.ToString(@"mm\:ss");
        }
        else RecBadge.Visibility = Visibility.Collapsed;

        if (_demo)
        {
            StatusDot.Fill = Green;
            StatusText.Text = "Demo  ·  " + _fps.ToString("0") + " fps";
            SensorText.Text = "";
            Overlay.Visibility = Visibility.Collapsed;
            return;
        }

        // watchdog: once the picture has been live, silence for 2.5 s means the K10 was unplugged (or the camera hung)
        if (cnt != _wdCount) { _wdCount = cnt; _wdChange = now; }
        if (st == DeviceState.Live) _wasLive = true;
        if (_closing) return;
        if (st == DeviceState.Disconnected || (_wasLive && (now - _wdChange).TotalSeconds > 2.5))
        {
            BeginDisconnectClose();
            return;
        }

        switch (st)
        {
            case DeviceState.Live:
                StatusDot.Fill = _avgFps < 22 && histSpan >= 4.5 ? Amber : Green;
                StatusText.Text = "Live  ·  " + _fps.ToString("0") + " fps";
                Overlay.Visibility = Visibility.Collapsed;
                break;
            case DeviceState.Starting:
                StatusDot.Fill = Amber;
                StatusText.Text = "Starting camera…";
                ShowOverlay("Starting camera…", _device.Detail);
                break;
            case DeviceState.NeedsReplug:
                StatusDot.Fill = Red;
                StatusText.Text = "Needs replug";
                ShowOverlay("Unplug and replug your K10",
                            "The camera is not sending video. Unplug it, wait 3 seconds, then plug it back into the same USB 3 port. The picture will start by itself.");
                break;
            case DeviceState.Blocked:
                StatusDot.Fill = Red;
                StatusText.Text = "Camera unavailable";
                ShowOverlay("Can't open the camera",
                            _device.Detail + "\nIf another program is using the camera, close it. The app keeps trying.");
                break;
            default:
                StatusDot.Fill = Amber;
                StatusText.Text = "No camera";
                ShowOverlay("Plug in your Bebird K10",
                            "Looking for the camera. The picture starts by itself as soon as it appears.");
                break;
        }

        var r = _device.Latest;
        if (st != DeviceState.Live) SensorText.Text = "";
        else if (!_device.HasSensor) SensorText.Text = "no level sensor";
        else if (r == null) SensorText.Text = "level sensor starting…";
        else
        {
            switch (r.Status)
            {
                case SensorStatus.Ok:
                    SensorText.Text = _s.AutoLevel ? "roll " + EffectiveAngle(r.AngleDeg).ToString("+0.0;-0.0;0.0") + "°" : "auto-level off";
                    break;
                case SensorStatus.Flat: SensorText.Text = "wand vertical, holding angle"; break;
                case SensorStatus.Lost: SensorText.Text = "level sensor lost"; break;
                default: SensorText.Text = ""; break;
            }
        }

        if (DiagBox.Visibility == Visibility.Visible) UpdateDiag(r);
    }

    void ShowSlowWarning()
    {
        _warnedThisSession = true;
        WarnBody.Text = "The picture is only running at about " + _avgFps.ToString("0") + " frames per second (normal is 30).\n\n" +
                        "The K10 needs a USB 3 port. Plug it straight into a USB 3 port on the PC (usually blue), " +
                        "and don't use an extension cable or a hub. It still works like this, just not smoothly.";
        WarnSnooze.IsChecked = false;
        WarnCard.Visibility = Visibility.Visible;
        Log.Write("Slow picture warning shown at " + _avgFps.ToString("0.0") + " fps");
    }

    void WarnOk_Click(object s, RoutedEventArgs e)
    {
        if (WarnSnooze.IsChecked == true) _s.Usb2WarnSnoozeUntil = DateTime.Now.AddDays(30);
        WarnCard.Visibility = Visibility.Collapsed;
    }

    /// <summary>
    /// The K10 can't be restarted cleanly in the same process after an unplug (its shutdown hangs inside Windows),
    /// so the app closes itself and the user starts it again. Plugging in first or opening first both work.
    /// </summary>
    void BeginDisconnectClose()
    {
        if (_closing) return;
        _closing = true;
        Log.Write("K10 lost while running: closing the app");
        StatusDot.Fill = Red;
        StatusText.Text = "K10 disconnected";
        SensorText.Text = "";
        ShowOverlay("K10 disconnected", "Plug it back in and open the app again. Closing…");

        var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.5) };
        t.Tick += (s, e) =>
        {
            t.Stop();
            Close();   // finishes a running recording first
            _exitTimer = new Timer(_ => Environment.Exit(0), null, 3000, System.Threading.Timeout.Infinite); // safety net
        };
        t.Start();
    }

    void ShowOverlay(string title, string sub)
    {
        Overlay.Visibility = Visibility.Visible;
        OverlayTitle.Text = title;
        OverlaySub.Text = sub;
        OverlaySub.Visibility = string.IsNullOrEmpty(sub) ? Visibility.Collapsed : Visibility.Visible;
    }

    void UpdateDiag(SensorReading r)
    {
        var sb = new StringBuilder();
        sb.AppendLine("state     " + _device.State + (string.IsNullOrEmpty(_device.Detail) ? "" : "  (" + _device.Detail + ")"));
        sb.AppendLine("camera    " + _device.CameraName + "  " + _frameW + "x" + _frameH + "  " + _fps.ToString("0.0") + " fps (5 s avg " + _avgFps.ToString("0.0") + ")");
        if (r != null)
        {
            sb.AppendLine("sensor    " + r.Status + "  seq " + r.Seq);
            sb.AppendLine("X/Y/Z     " + r.X + " / " + r.Y + " / " + r.Z + "   |YZ| " + r.Magnitude.ToString("0"));
            sb.AppendLine("raw angle " + r.AngleDeg.ToString("0.0") + "°   shown " + _disp.ToString("0.0") + "°   invert " + _s.Invert);
        }
        else sb.AppendLine("sensor    (none)");
        sb.AppendLine("errors " + _device.SensorErrors + "   glitches " + _device.Glitches + "   ui scale " + _uiScale.ToString("0.00"));
        sb.Append("log       " + Log.FilePath);
        DiagText.Text = sb.ToString();
    }

    void Toast(string text)
    {
        ToastText.Text = text;
        ToastBox.Visibility = Visibility.Visible;
        var anim = new DoubleAnimationUsingKeyFrames();
        anim.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(120))));
        anim.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(2400))));
        anim.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(2900))));
        anim.Completed += (s, e) => { if (ToastBox.Opacity < 0.01) ToastBox.Visibility = Visibility.Collapsed; };
        ToastBox.BeginAnimation(OpacityProperty, anim);
    }

    // ------------------------------------------------------------------ rendering the view into a photo / video frame

    /// <summary>Draws the current picture exactly as shown: rotation, mirror and (optionally) the round mask.</summary>
    DrawingVisual BuildView(int w, int h, int outW, int outH, bool circle, bool opaque)
    {
        double r = Math.Min(w, h) / 2.0;
        var dv = new DrawingVisual();
        RenderOptions.SetBitmapScalingMode(dv, BitmapScalingMode.HighQuality);
        using (var dc = dv.RenderOpen())
        {
            if (opaque) dc.DrawRectangle(Brushes.Black, null, new Rect(0, 0, outW, outH));
            dc.PushTransform(new TranslateTransform(-(w - outW) / 2.0, -(h - outH) / 2.0));
            Geometry clip = circle
                ? new EllipseGeometry(new Point(w / 2.0, h / 2.0), r, r)
                : (Geometry)new RectangleGeometry(new Rect(0, 0, w, h));
            dc.PushClip(clip);
            dc.PushTransform(new ScaleTransform(_s.Mirror ? -1 : 1, 1, w / 2.0, h / 2.0));
            dc.PushTransform(new RotateTransform(_disp, w / 2.0, h / 2.0));
            dc.DrawImage(_bmp, new Rect(0, 0, w, h));
            dc.Pop(); dc.Pop(); dc.Pop(); dc.Pop();
        }
        return dv;
    }

    // ------------------------------------------------------------------ save folder + latest capture

    string EnsureSaveFolder()
    {
        string dir = _s.EffectiveSaveFolder();
        try { Directory.CreateDirectory(dir); return dir; }
        catch (Exception ex)
        {
            Log.Write("Save folder unusable: " + ex.Message);
            Toast("Can't use the save folder");
            return null;
        }
    }

    void CommitPath()
    {
        string p = PathBox.Text.Trim().Trim('"');
        if (string.Equals(p, _s.EffectiveSaveFolder(), StringComparison.OrdinalIgnoreCase)) return;
        try
        {
            if (string.IsNullOrEmpty(p)) throw new ArgumentException("empty");
            Directory.CreateDirectory(p);
            _s.SaveFolder = p;
            Toast("Saving to " + p);
            RefreshLatest();
        }
        catch
        {
            Toast("Can't use that folder");
            PathBox.Text = _s.EffectiveSaveFolder();
        }
    }

    void PathBox_KeyDown(object s, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { CommitPath(); Keyboard.ClearFocus(); Focus(); e.Handled = true; }
        else if (e.Key == Key.Escape) { PathBox.Text = _s.EffectiveSaveFolder(); Keyboard.ClearFocus(); Focus(); e.Handled = true; }
    }

    void PathBox_LostFocus(object s, KeyboardFocusChangedEventArgs e) => CommitPath();

    void Browse_Click(object s, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Choose where photos and videos are saved",
            InitialDirectory = Directory.Exists(_s.EffectiveSaveFolder()) ? _s.EffectiveSaveFolder() : null
        };
        if (dlg.ShowDialog(this) == true)
        {
            PathBox.Text = dlg.FolderName;
            CommitPath();
        }
    }

    /// <summary>Finds the newest photo/video of ours in the save folder and shows its thumbnail.</summary>
    void RefreshLatest()
    {
        string newest = null;
        try
        {
            string dir = _s.EffectiveSaveFolder();
            if (Directory.Exists(dir))
                newest = new DirectoryInfo(dir).EnumerateFiles()
                    .Where(f => (f.Name.StartsWith("k10_") || f.Name.StartsWith("bebird_")) &&
                                (f.Extension.Equals(".png", StringComparison.OrdinalIgnoreCase) ||
                                 f.Extension.Equals(".mp4", StringComparison.OrdinalIgnoreCase)))
                    .OrderByDescending(f => f.LastWriteTimeUtc)
                    .Select(f => f.FullName).FirstOrDefault();
        }
        catch { }
        SetLatest(newest, null);
    }

    void SetLatest(string path, BitmapSource preferred)
    {
        _latestPath = path;
        BitmapSource thumb = preferred;
        if (thumb == null && path != null) thumb = ShellThumb.TryGet(path, 208);

        if (thumb != null)
        {
            ThumbBorder.Background = new ImageBrush(thumb) { Stretch = Stretch.UniformToFill };
            ThumbEmpty.Visibility = Visibility.Collapsed;
        }
        else
        {
            ThumbBorder.Background = new SolidColorBrush(Color.FromRgb(0x1B, 0x1D, 0x22));
            ThumbEmpty.Visibility = path == null ? Visibility.Visible : Visibility.Collapsed;
        }
        ThumbPlay.Visibility = (path != null && path.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase))
            ? Visibility.Visible : Visibility.Collapsed;
    }

    void Thumb_Click(object s, MouseButtonEventArgs e)
    {
        try
        {
            if (_latestPath != null && File.Exists(_latestPath))
                Process.Start("explorer.exe", "/select,\"" + _latestPath + "\"");
            else
            {
                string dir = EnsureSaveFolder();
                if (dir != null) Process.Start("explorer.exe", "\"" + dir + "\"");
            }
        }
        catch (Exception ex) { Log.Write("Open Explorer failed: " + ex.Message); }
        e.Handled = true;
    }

    // ------------------------------------------------------------------ photo

    void Snapshot()
    {
        if (_bmp == null) { Toast("No picture yet"); return; }
        string dir = EnsureSaveFolder();
        if (dir == null) return;
        try
        {
            int w = _bmp.PixelWidth, h = _bmp.PixelHeight;
            // with the round scope the photo is the circle's bounding square, otherwise the whole frame
            int outW = _s.Circle ? Math.Min(w, h) : w;
            int outH = _s.Circle ? Math.Min(w, h) : h;

            var rtb = new RenderTargetBitmap(outW, outH, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(BuildView(w, h, outW, outH, _s.Circle, false));

            string file = Path.Combine(dir, "k10_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".png");
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(rtb));
            using (var fs = File.Create(file)) enc.Save(fs);

            rtb.Freeze();
            SetLatest(file, rtb);
            Toast("Saved " + Path.GetFileName(file));
            Log.Write("Photo saved: " + file);
        }
        catch (Exception ex)
        {
            Log.Write("Snapshot failed: " + ex);
            Toast("Could not save the photo");
        }
    }

    // ------------------------------------------------------------------ video

    async Task ToggleRecordAsync()
    {
        if (_recBusy) return;
        if (_rec != null) await StopRecordingAsync();
        else await StartRecordingAsync();
    }

    async Task StartRecordingAsync()
    {
        if (_recBusy || _rec != null) return;
        if (_bmp == null) { Toast("No picture yet"); BtnRec.IsChecked = false; return; }
        string dir = EnsureSaveFolder();
        if (dir == null) { BtnRec.IsChecked = false; return; }

        _recBusy = true;
        try
        {
            int w = _bmp.PixelWidth, h = _bmp.PixelHeight;
            _recCircle = _s.Circle;
            _recW = (_recCircle ? Math.Min(w, h) : w) & ~1;      // H.264 wants even sizes
            _recH = (_recCircle ? Math.Min(w, h) : h) & ~1;
            _recRtb = new RenderTargetBitmap(_recW, _recH, 96, 96, PixelFormats.Pbgra32);
            _recFirstFrame = null;

            string path = Path.Combine(dir, "k10_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".mp4");
            var rec = new Recorder();
            await rec.StartAsync(path, _recW, _recH);
            _rec = rec;
            BtnRec.IsChecked = true;
            BtnRec.Content = "Stop";
            Log.Write("Recording started: " + path + " (" + _recW + "x" + _recH + ")");
        }
        catch (Exception ex)
        {
            Log.Write("Recording could not start: " + ex);
            BtnRec.IsChecked = false;
            Toast("Can't start recording");
        }
        finally { _recBusy = false; }
    }

    async Task StopRecordingAsync()
    {
        var r = _rec;
        if (r == null || _recBusy) return;
        _recBusy = true;
        _rec = null;
        BtnRec.IsChecked = false;
        BtnRec.Content = "Record";
        try
        {
            bool ok = await Task.Run(() => r.StopAsync());
            if (ok && File.Exists(r.Path) && new FileInfo(r.Path).Length > 0)
            {
                SetLatest(r.Path, _recFirstFrame);
                Toast("Saved " + Path.GetFileName(r.Path));
                Log.Write("Recording saved: " + r.Path + " frames=" + r.Frames);
            }
            else
            {
                Log.Write("Recording failed: " + r.Error);
                Toast("Recording failed");
            }
        }
        catch (Exception ex) { Log.Write("Stop recording failed: " + ex); Toast("Recording failed"); }
        finally { _recBusy = false; }
    }

    /// <summary>Used when the window closes: the MP4 must be finished properly.</summary>
    void StopRecordingBlocking()
    {
        var r = _rec;
        if (r == null) return;
        _rec = null;
        try { Task.Run(() => r.StopAsync()).Wait(12000); }
        catch (Exception ex) { Log.Write("Stop recording on close failed: " + ex.Message); }
    }

    void SubmitRecordingFrame()
    {
        try
        {
            _recRtb.Clear();
            _recRtb.Render(BuildView(_bmp.PixelWidth, _bmp.PixelHeight, _recW, _recH, _recCircle, true));
            var buf = new byte[_recW * _recH * 4];
            _recRtb.CopyPixels(buf, _recW * 4, 0);
            if (_recFirstFrame == null)
            {
                var wb = new WriteableBitmap(_recRtb);
                wb.Freeze();
                _recFirstFrame = wb;
            }
            _rec?.AddFrame(buf);
        }
        catch (Exception ex) { Log.Write("Recording frame failed: " + ex.Message); }
    }
}
