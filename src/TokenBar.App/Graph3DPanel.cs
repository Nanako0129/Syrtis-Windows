using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using TokenBar.Core;
using Windows.Foundation;
using Windows.UI;

namespace TokenBar.App;

/// <summary>
/// SwapChainPanel host for the 3D contribution graph. The panel owns the
/// renderer's lifetime against the flyout show/hide cycle, caches data while
/// inactive, and turns pointer input into render-on-demand camera/hover work.
/// </summary>
internal sealed class Graph3DPanel : SwapChainPanel
{
    public static int CreatedCount;
    public static int ReleasedCount;
    public static int DeviceRemovedCount;
    public static int ErrorCount;

    private const string CameraStorageKey = "tokenbar.orbit.v1";

    private Graph3DRenderer? _renderer;
    private GridLayout? _grid;
    private bool _dark;
    private bool _costAuthoritative;
    private bool _hasData;
    private bool _active;
    private bool _creating;
    private string? _dataSignature;

    private uint? _dragPointerId;
    private Point _lastPointer;
    private bool _panDrag;
    private int _dragFrames;
    private long _dragRenderTicks;

    private readonly record struct PointerLocation(
        Point Local,
        Point Root,
        bool LeftPressed,
        bool RightPressed,
        int WheelDelta);

    public Graph3DPanel()
    {
        SizeChanged += (_, _) => OnSizeChanged();
        // ActualWidth/Height are DIPs and may stay unchanged when the flyout
        // crosses monitors. Resize the physical swapchain as soon as WinUI's
        // composition scale changes so rendering and pointer pixels keep the
        // same denominator.
        CompositionScaleChanged += (_, _) => OnSizeChanged();
        PointerPressed += OnPointerPressed;
        PointerMoved += OnPointerMoved;
        PointerReleased += OnPointerReleased;
        PointerCanceled += OnPointerCanceled;
        PointerCaptureLost += OnPointerCaptureLost;
        PointerExited += OnPointerExited;
        PointerWheelChanged += OnPointerWheelChanged;
    }

    public bool IsActive => _active;

    /// <summary>Cache data regardless of visibility. If a renderer exists,
    /// rebuild it and render exactly one updated frame.</summary>
    public void SetData(GridLayout grid, bool dark, bool costAuthoritative)
    {
        var signature = DataSignature(grid, dark, costAuthoritative);
        var changed = signature != _dataSignature;
        _dataSignature = signature;
        _grid = grid;
        _dark = dark;
        _costAuthoritative = costAuthoritative;
        _hasData = true;
        if (!_active)
        {
            return;
        }

        if (_renderer is null)
        {
            TryCreate();
            return;
        }

        if (!changed)
        {
            return;
        }

        try
        {
            // The renderer resets its highlighted instance when geometry is
            // replaced; close the matching popup in the same transaction so a
            // periodic snapshot refresh cannot leave stale hover text behind.
            HoverTip.HideFor(this);
            _renderer.SetData(grid, dark);
            RenderOrRecover("data");
        }
        catch (Exception ex)
        {
            HandleDeviceError("data", ex);
        }
    }

    /// <summary>Create (or re-create) the device once the panel has a real
    /// pixel size and render one frame.</summary>
    public void Activate()
    {
        _active = true;
        TryCreate();
    }

    /// <summary>Release the swapchain/device deterministically while hidden.
    /// Camera motion is persisted before teardown if a drag was in progress.</summary>
    public void Release()
    {
        EndDrag(persist: true);
        HoverTip.HideFor(this);
        _active = false;
        if (_renderer is null)
        {
            return;
        }

        DetachSwapChain();
        _renderer.Dispose();
        _renderer = null;
        Interlocked.Increment(ref ReleasedCount);
        DevLog.Write(
            $"graph3d: released (created={CreatedCount} released={ReleasedCount})");
    }

    public void FitToContent()
    {
        if (_renderer is null)
        {
            if (_active)
            {
                TryCreate();
            }

            return;
        }

        try
        {
            HoverTip.HideFor(this);
            _renderer.ClearHover();
            _renderer.FitToContent();
            RenderOrRecover("fit");
        }
        catch (Exception ex)
        {
            HandleDeviceError("fit", ex);
        }
    }

    public void ResetCamera()
    {
        // Clear even while inactive; the next renderer will auto-fit cached
        // data because no tokenbar.orbit.v1 pose remains.
        AppSettings.Store.Remove(CameraStorageKey);
        if (_renderer is null)
        {
            return;
        }

        try
        {
            HoverTip.HideFor(this);
            _renderer.ClearHover();
            _renderer.ResetCamera();
            RenderOrRecover("reset");
        }
        catch (Exception ex)
        {
            HandleDeviceError("reset", ex);
        }
    }

    public void ZoomFromWheel(int wheelDelta)
    {
        if (_renderer is null)
        {
            return;
        }

        try
        {
            // Both WinUI pointer events and the focusless flyout's low-level
            // wheel hook land here. Clear the old picked cell before moving
            // the camera so neither route can leave stale text/highlight.
            HoverTip.HideFor(this);
            _renderer.ClearHover();
            _renderer.ZoomFromWheel(wheelDelta);
            RenderOrRecover("zoom");
        }
        catch (Exception ex)
        {
            HandleDeviceError("zoom", ex);
        }
    }

    private void OnSizeChanged()
    {
        if (!_active)
        {
            return;
        }

        if (_renderer is null)
        {
            TryCreate();
            return;
        }

        var (w, h) = PixelSize();
        if (w == 0 || h == 0)
        {
            return;
        }

        try
        {
            _renderer.Resize(w, h);
            _renderer.SetCompositionScale(EffectiveScaleX, EffectiveScaleY);
            RenderOrRecover("resize");
        }
        catch (Exception ex)
        {
            HandleDeviceError("resize", ex);
        }
    }

    private void TryCreate()
    {
        if (_renderer is not null)
        {
            return;
        }

        var (w, h) = PixelSize();
        if (w == 0 || h == 0)
        {
            return;
        }

        _creating = true;
        try
        {
            _renderer = new Graph3DRenderer(w, h);
            SetSwapChain(_renderer.SwapChainPointer);
            _renderer.SetCompositionScale(EffectiveScaleX, EffectiveScaleY);
            Interlocked.Increment(ref CreatedCount);
            DevLog.Write($"graph3d: created {w}x{h} scale="
                + $"{EffectiveScaleX:F2},{EffectiveScaleY:F2} (created={CreatedCount})");

            // Always reapply the cache after recreation. Do not signature-gate
            // this: the renderer owns fresh GPU buffers after every release.
            if (_hasData && _grid is not null)
            {
                _renderer.SetData(_grid, _dark);
            }

            RenderOrRecover("initial-render");
        }
        catch (Exception ex)
        {
            // A constructor/shader/API failure is persistent for this build.
            // Re-entering TryCreate from its own catch recurses until stack
            // overflow, so leave the active panel empty and retry on the next
            // activation, size change, or data update instead.
            Interlocked.Increment(ref ErrorCount);
            DevLog.Write($"graph3d: create FAILED (errors={ErrorCount}): {ex.Message}");
            DetachSwapChain();
            _renderer?.Dispose();
            _renderer = null;
        }
        finally
        {
            _creating = false;
        }
    }

    private void RenderOrRecover(string where)
    {
        if (_renderer is null)
        {
            return;
        }

        if (!_renderer.Render())
        {
            HandleDeviceLost(where);
        }
    }

    private (int Width, int Height) PixelSize()
    {
        return ((int)(ActualWidth * EffectiveScaleX),
            (int)(ActualHeight * EffectiveScaleY));
    }

    private float EffectiveScaleX => CompositionScaleX > 0 ? CompositionScaleX : 1f;
    private float EffectiveScaleY => CompositionScaleY > 0 ? CompositionScaleY : 1f;

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (!_active || _renderer is null)
        {
            return;
        }

        if (!TryGetPointerLocation(e, out var point)
            || (!point.LeftPressed && !point.RightPressed))
        {
            return;
        }

        ClearHover();
        _dragPointerId = e.Pointer.PointerId;
        _lastPointer = point.Local;
        _panDrag = point.RightPressed;
        _dragFrames = 0;
        _dragRenderTicks = 0;
        CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_active || _renderer is null)
        {
            return;
        }

        if (!TryGetPointerLocation(e, out var point))
        {
            return;
        }

        if (_dragPointerId is { } dragPointerId
            && e.Pointer.PointerId == dragPointerId)
        {
            var dx = (float)((point.Local.X - _lastPointer.X) * EffectiveScaleX);
            var dy = (float)((point.Local.Y - _lastPointer.Y) * EffectiveScaleY);
            _lastPointer = point.Local;
            try
            {
                if (_panDrag)
                {
                    _renderer.Pan(dx, dy);
                }
                else
                {
                    _renderer.Orbit(dx, dy);
                }

                var renderStart = System.Diagnostics.Stopwatch.GetTimestamp();
                RenderOrRecover("drag");
                _dragRenderTicks += System.Diagnostics.Stopwatch.GetTimestamp() - renderStart;
                _dragFrames++;
            }
            catch (Exception ex)
            {
                HandleDeviceError("drag", ex);
            }

            e.Handled = true;
            return;
        }

        UpdateHover(point);
    }

    private void OnPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_dragPointerId is not { } dragPointerId
            || e.Pointer.PointerId != dragPointerId)
        {
            return;
        }

        var hasPoint = TryGetPointerLocation(e, out var point);
        try
        {
            ReleasePointerCapture(e.Pointer);
        }
        finally
        {
            EndDrag(persist: true);
        }

        if (hasPoint)
        {
            UpdateHover(point);
        }

        e.Handled = true;
    }

    private void OnPointerCanceled(object sender, PointerRoutedEventArgs e) =>
        EndDrag(persist: true);

    private void OnPointerCaptureLost(object sender, PointerRoutedEventArgs e) =>
        EndDrag(persist: true);

    private void OnPointerExited(object sender, PointerRoutedEventArgs e)
    {
        ClearHover();
    }

    private void OnPointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        if (!_active || _renderer is null)
        {
            return;
        }

        if (!TryGetPointerLocation(e, out var point))
        {
            return;
        }

        ZoomFromWheel(point.WheelDelta);
        e.Handled = true;
    }

    /// <summary>
    /// SwapChainPanel's element-relative PointerPoint can include its
    /// composition offset a second time after the panel is reparented inside a
    /// scroller. Read the pointer in XamlRoot space (the same space WinUI uses
    /// for its accelerator tooltip), then subtract the panel's visual origin.
    /// Both values are DIPs; CompositionScale is applied only at the renderer
    /// boundary below.
    /// </summary>
    private bool TryGetPointerLocation(
        PointerRoutedEventArgs e, out PointerLocation location)
    {
        location = default;
        if (XamlRoot?.Content is not UIElement root)
        {
            return false;
        }

        try
        {
            var atRoot = e.GetCurrentPoint(root);
            var origin = TransformToVisual(root).TransformPoint(new Point(0, 0));
            location = new PointerLocation(
                new Point(
                    atRoot.Position.X - origin.X,
                    atRoot.Position.Y - origin.Y),
                atRoot.Position,
                atRoot.Properties.IsLeftButtonPressed,
                atRoot.Properties.IsRightButtonPressed,
                atRoot.Properties.MouseWheelDelta);
            return true;
        }
        catch (InvalidOperationException)
        {
            // The panel can detach while a queued pointer event is delivered.
            return false;
        }
    }

    private void UpdateHover(PointerLocation point)
    {
        if (_renderer is null)
        {
            return;
        }

        try
        {
            var changed = _renderer.UpdateHover(
                (float)(point.Local.X * EffectiveScaleX),
                (float)(point.Local.Y * EffectiveScaleY), out var cell);

            if (cell is null)
            {
                HoverTip.HideFor(this);
            }
            else
            {
                if (changed)
                {
                    HoverTip.ShowAt(this, BuildTooltip(cell), point.Root);
                }
                else if (!HoverTip.MoveAt(this, point.Root))
                {
                    // A light-dismiss or another hover host may have closed
                    // the shared popup while the pointer stayed on this cell.
                    // Reopen it without forcing a GPU highlight change.
                    HoverTip.ShowAt(this, BuildTooltip(cell), point.Root);
                }
            }

            if (changed)
            {
                RenderOrRecover("hover");
            }
        }
        catch (Exception ex)
        {
            HandleDeviceError("hover", ex);
        }
    }

    private void ClearHover()
    {
        HoverTip.HideFor(this);
        if (_renderer is null)
        {
            return;
        }

        try
        {
            if (_renderer.ClearHover())
            {
                RenderOrRecover("hover-clear");
            }
        }
        catch (Exception ex)
        {
            HandleDeviceError("hover-clear", ex);
        }
    }

    private void EndDrag(bool persist)
    {
        var hadDrag = _dragPointerId is not null;
        _dragPointerId = null;
        _panDrag = false;
        try
        {
            ReleasePointerCaptures();
        }
        catch
        {
            // Pointer capture may already have been lost during teardown.
        }

        if (persist && hadDrag)
        {
            _renderer?.PersistCamera();
        }

        if (hadDrag && _dragFrames > 0 && _dragRenderTicks > 0)
        {
            var renderFps = _dragFrames * (double)System.Diagnostics.Stopwatch.Frequency
                / _dragRenderTicks;
            DevLog.Write(
                $"graph3d: drag frames={_dragFrames} renderFps={renderFps:F1}");
        }

        _dragFrames = 0;
        _dragRenderTicks = 0;
    }

    private UIElement BuildTooltip(GridCell cell)
    {
        var panel = new StackPanel { Spacing = 4, MinWidth = 150 };
        panel.Children.Add(TooltipText(Format.MonthDay(cell.Date), 12, bold: true));
        panel.Children.Add(TooltipText(
            "{0} tokens".Localized(Format.ExactTokens(cell.Tokens)), 11, 0.9));
        panel.Children.Add(TooltipText(
            CostSurfaceProjection.CostText(cell.Tokens, cell.Cost, _costAuthoritative), 11, 0.9));
        return panel;
    }

    private static TextBlock TooltipText(
        string text, double size, double opacity = 1, bool bold = false) => new()
    {
        Text = text,
        FontSize = size,
        Opacity = opacity,
        FontWeight = bold
            ? Microsoft.UI.Text.FontWeights.SemiBold
            : Microsoft.UI.Text.FontWeights.Normal,
        Foreground = new SolidColorBrush(Color.FromArgb(255, 240, 240, 245)),
    };

    /// <summary>The Swift reference gates on cols|maxTokens|activeCount|theme.
    /// Keep that contract, plus a compact content fingerprint so two years with
    /// coincidentally equal aggregates cannot retain stale geometry/tooltips.</summary>
    private static string DataSignature(
        GridLayout grid, bool dark, bool costAuthoritative)
    {
        const ulong offset = 14695981039346656037;
        const ulong prime = 1099511628211;
        var fingerprint = offset;
        var activeCount = 0;

        void Mix(ulong value)
        {
            fingerprint ^= value;
            fingerprint *= prime;
        }

        foreach (var cell in grid.Cells)
        {
            Mix((ulong)(uint)cell.Col);
            Mix((ulong)(uint)cell.Row);
            Mix(cell.InYear ? 1UL : 0UL);
            Mix(cell.Active ? 1UL : 0UL);
            if (!cell.InYear)
            {
                continue;
            }

            foreach (var ch in cell.Date)
            {
                Mix(ch);
            }

            Mix(unchecked((ulong)cell.Tokens));
            Mix(unchecked((ulong)BitConverter.DoubleToInt64Bits(cell.Cost)));
            if (cell.Active)
            {
                activeCount++;
            }
        }

        return $"{grid.Cols}|{grid.MaxTokens}|{activeCount}|{dark}|{costAuthoritative}|{fingerprint:X16}";
    }

    /// <summary>Device-removed/reset (TDR, driver update, GPU reset): count it
    /// and recreate on the spot if still active.</summary>
    private void HandleDeviceLost(string where)
    {
        Interlocked.Increment(ref DeviceRemovedCount);
        DevLog.Write($"graph3d: device lost at {where} (removed={DeviceRemovedCount})");
        if (_creating)
        {
            // A freshly-created device that is already removed will fail again
            // immediately. Tear it down and let a later lifecycle event retry,
            // rather than recursively creating devices on the same call stack.
            DetachSwapChain();
            _renderer?.Dispose();
            _renderer = null;
            return;
        }

        Recreate();
    }

    /// <summary>An unexpected render-path throw is logged and recovered like a
    /// lost device so a transient fault cannot wedge the flyout.</summary>
    private void HandleDeviceError(string where, Exception ex)
    {
        Interlocked.Increment(ref ErrorCount);
        DevLog.Write($"graph3d: {where} FAILED (errors={ErrorCount}): {ex.Message}");
        Recreate();
    }

    private void Recreate()
    {
        EndDrag(persist: true);
        HoverTip.HideFor(this);
        DetachSwapChain();
        _renderer?.Dispose();
        _renderer = null;
        if (_active)
        {
            TryCreate();
        }
    }

    private void DetachSwapChain()
    {
        try
        {
            SetSwapChain(nint.Zero);
        }
        catch (Exception ex)
        {
            DevLog.Write($"graph3d: detach failed: {ex.Message}");
        }
    }

    // ── ISwapChainPanelNative interop (the one unverifiable seam) ──────────

    private void SetSwapChain(nint swapChain)
    {
        var native = WinRT.CastExtensions.As<ISwapChainPanelNative>(this);
        Marshal.ThrowExceptionForHR(native.SetSwapChain(swapChain));
    }

    [System.Runtime.InteropServices.ComImport]
    [System.Runtime.InteropServices.Guid("63aad0b8-7c24-40ff-85a8-640d944cc325")]
    [System.Runtime.InteropServices.InterfaceType(
        System.Runtime.InteropServices.ComInterfaceType.InterfaceIsIUnknown)]
    private interface ISwapChainPanelNative
    {
        [System.Runtime.InteropServices.PreserveSig]
        int SetSwapChain(nint swapChain);
    }
}
