using Microsoft.UI.Dispatching;
using TokenBar.Core;

namespace TokenBar.App;

/// <summary>
/// RunCat-style tray animation (macOS TrayAnimator, itself a port of the
/// Tauri animation.rs): cat or parrot frames spin with the live token rate,
/// scaled by the Settings pace (<see cref="TrayAnimationSpeed"/>: idle 2 fps
/// below 50K tok/min, 40 fps at 3M after scaling). Runs only while the tray is in
/// Hidden ("icon only") mode with an animation style; every other state
/// stops the timer cold so the resident process stays quiet.
/// </summary>
internal sealed class TrayAnimator : IDisposable
{
    private readonly DispatcherQueueTimer _timer;
    private readonly Func<double?> _rate;
    private readonly Action<System.Drawing.Icon> _apply;
    // Frames are pre-baked into HICON-backed Icons once per style/theme:
    // per-frame GetHicon churn measured ~23% of a core at 2 fps; swapping
    // cached icons brings the loop back to noise.
    private string _style = "";
    private bool _dark;
    private readonly Dictionary<string, List<System.Drawing.Icon>> _frames = [];
    private string _active = "";
    private int _index;
    private bool _sandLoaded;
    private int? _sandLevelShown;

    public TrayAnimator(
        DispatcherQueue dispatcher, Func<double?> rate,
        Action<System.Drawing.Icon> apply)
    {
        _rate = rate;
        _apply = apply;
        _timer = dispatcher.CreateTimer();
        _timer.Tick += (_, _) => Tick();
    }

    public bool Running => _timer.IsRunning;

    public void Dispose()
    {
        _timer.Stop();
        foreach (var icon in _frames.Values.SelectMany(f => f))
        {
            var handle = icon.Handle; // the raw HICON Icon.FromHandle wraps
            icon.Dispose();
            _ = DestroyIcon(handle);
        }

        _frames.Clear();
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool DestroyIcon(nint hIcon);

    /// <summary>Show the style's frames; animate=false parks on frame 0
    /// (the tokenbar.tray.animate toggle).</summary>
    public void Start(string style, bool dark, bool animate)
    {
        _style = style;
        _dark = dark;
        var key = KeyFor(style, dark);
        if (key != _active)
        {
            _active = key;
            _index = 0;
        }

        var frames = FramesFor(key);
        if (frames.Count == 0)
        {
            _timer.Stop();
            return;
        }

        _apply(frames[_index % frames.Count]);
        if (!animate)
        {
            _timer.Stop();
            return;
        }

        _timer.Interval = IntervalFor(_rate(), style);
        if (!_timer.IsRunning)
        {
            _timer.Start();
        }
    }

    public void Stop() => _timer.Stop();

    private void Tick()
    {
        // Sand changes frame set, not speed, when usage crosses a level.
        var key = KeyFor(_style, _dark);
        if (key != _active)
        {
            _active = key;
            _index = 0;
        }

        var frames = FramesFor(_active);
        if (frames.Count == 0)
        {
            _timer.Stop();
            return;
        }

        _index = (_index + 1) % frames.Count;
        _apply(frames[_index]);
        // Retune to the current rate every frame (macOS animationLoop), so a
        // pace change in Settings applies on the next frame.
        _timer.Interval = IntervalFor(_rate(), _style);
    }

    /// <summary>The sand level for the live rate under the current pace,
    /// with hysteresis against the level shown now (macOS
    /// <c>frameStyle</c>). Also what TrayService watches so a still
    /// (animation off) sand icon follows usage.</summary>
    public int SandLevel()
    {
        var scaled = AnimationPaces.Current(AppSettings.Store).Scaled(_rate() ?? 0);
        return (_sandLevelShown = TokenBar.Core.SandShoal.Level(scaled, _sandLevelShown)).Value;
    }

    private string KeyFor(string style, bool dark)
    {
        if (style != TokenBar.Core.SandShoal.Style)
        {
            return $"{style}|{(dark ? "dark" : "light")}";
        }

        foreach (var set in TokenBar.Core.SandShoal.SetsToLoad(style, _sandLoaded))
        {
            _ = FramesFor(set);
        }

        _sandLoaded = true;
        return TokenBar.Core.SandShoal.FrameKey(SandLevel(), dark);
    }

    // Sand plays at one fixed rate whatever the usage (macOS sandLayerSpeed).
    private static TimeSpan IntervalFor(double? rate, string style) =>
        style == TokenBar.Core.SandShoal.Style
            ? TokenBar.Core.SandShoal.FrameInterval
            : IntervalFor(rate);

    private static TimeSpan IntervalFor(double? rate) =>
        TimeSpan.FromMilliseconds(TrayAnimationSpeed.IntervalMilliseconds(
            rate, AnimationPaces.Current(AppSettings.Store)));

    /// <summary>Frames land as 32x32 letterboxed HICONs (parrot is 48x36),
    /// composed once and cached for the process lifetime.</summary>
    private List<System.Drawing.Icon> FramesFor(string key)
    {
        if (_frames.TryGetValue(key, out var cached))
        {
            return cached;
        }

        var parts = key.Split('|');
        var dir = Path.Combine(
            AppContext.BaseDirectory, "Assets",
            TokenBar.Core.SandShoal.AssetDirectory(parts[0], parts[1] != "light")
                ?? $"anim-{(parts[0] == "parrot" ? "parrot" : "cat2")}{(parts[1] == "light" ? "-light" : "")}");
        var composed = new List<System.Drawing.Icon>();
        try
        {
            foreach (var file in Directory.GetFiles(dir, "frame-*.png").OrderBy(f => f))
            {
                using var raw = new System.Drawing.Bitmap(file);
                using var canvas = new System.Drawing.Bitmap(32, 32);
                using (var g = System.Drawing.Graphics.FromImage(canvas))
                {
                    g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                    var scale = Math.Min(32.0 / raw.Width, 32.0 / raw.Height);
                    var w = (float)(raw.Width * scale);
                    var h = (float)(raw.Height * scale);
                    g.DrawImage(raw, (32 - w) / 2, (32 - h) / 2, w, h);
                }

                composed.Add(System.Drawing.Icon.FromHandle(canvas.GetHicon()));
            }
        }
        catch (Exception ex)
        {
            DevLog.Write($"tray frames load failed ({key}): {ex.Message}");
        }

        _frames[key] = composed;
        return composed;
    }
}
