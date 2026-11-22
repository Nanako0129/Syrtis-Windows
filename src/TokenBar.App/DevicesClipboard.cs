using Microsoft.UI.Dispatching;
using Windows.ApplicationModel.DataTransfer;

namespace TokenBar.App;

/// <summary>The real clipboard for <see cref="DevicesModel"/> (the macOS
/// concealed/transient pasteboard). The request's flags map one to one onto
/// <see cref="ClipboardContentOptions"/>: not in Win+V history, not synced to
/// other devices. The sequence number is user32
/// <c>GetClipboardSequenceNumber</c>, so "clear only if unchanged" never reads
/// the secret back. Whether Windows honours both options is checked by hand on
/// 188 (SA-4f report), not by a test.</summary>
internal sealed class DevicesClipboard(DispatcherQueue ui) : IDevicesClipboard
{
    public uint SequenceNumber => GetClipboardSequenceNumber();

    /// <summary>UI thread: the clipboard belongs to it.</summary>
    public uint Write(ClipboardWriteRequest request)
    {
        var package = new DataPackage();
        package.SetText(request.Text);
        Clipboard.SetContentWithOptions(package, new ClipboardContentOptions
        {
            IsAllowedInHistory = request.AllowHistory,
            IsRoamable = request.Roamable,
        });
        return GetClipboardSequenceNumber();
    }

    /// <summary>From any thread (the invitation can expire inside the poll
    /// loop). ponytail: the model compared the sequence number before this
    /// hop; a copy made in the few milliseconds before the UI thread runs it
    /// is cleared too. Re-check here if that ever matters.</summary>
    public void Clear()
    {
        if (ui.HasThreadAccess)
        {
            ClearNow();
        }
        else
        {
            _ = ui.TryEnqueue(ClearNow);
        }
    }

    private static void ClearNow()
    {
        try
        {
            Clipboard.Clear();
        }
        catch (Exception)
        {
            // Another process holding the clipboard open: nothing to clear safely.
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetClipboardSequenceNumber();
}
