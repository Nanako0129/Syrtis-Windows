using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using TokenBar.Core;
using TokenBar.Interop;
// TokenBar.Core.Grid collides with the XAML Grid (as in SettingsWindow.cs).
using Grid = Microsoft.UI.Xaml.Controls.Grid;

namespace TokenBar.App;

// Settings → Devices (SA-4f), the macOS DevicesSettingsView (Syrtis f8a1a0ae)
// in WinUI: (A) Share this PC, (B) Scan scope, (C) Linked devices, B and C only
// while the service runs. Every action goes through DevicesModel, which only
// calls TbCore.Remote*. The page is built once and kept across Rebuild(): its
// fields hold what the user is typing, and the model (one per window) holds
// everything else, a pairing wait included. Inputs are kept controls; the
// lists are rebuilt only when what they show changes, so a 2 s poll does not
// take keyboard focus away from a button.
public sealed partial class SettingsWindow
{
    /// <summary>Field width beside a row label (macOS 170 pt).</summary>
    private const double DevicesFieldWidth = 170;

    /// <summary>Wide enough for an IPv4 or a short IPv6 address (macOS 230 pt).</summary>
    private const double DevicesAddressWidth = 230;
    private const double DevicesPasteWidth = 290;

    /// <summary>How often the short status under a linked device is re-read:
    /// its text is in minutes (macOS statusTick, DSV :441).</summary>
    private static readonly TimeSpan DevicesStatusTick = TimeSpan.FromSeconds(30);

    /// <summary>A SAS is compared digit by digit with another screen:
    /// monospaced, one step above the captions around it (DSV :18).</summary>
    private static readonly FontFamily DevicesSasFont = new("Consolas, Cascadia Mono, monospace");

    private DevicesModel? _devicesModel;
    private DevicesPage? _devicesPage;
    private bool _devicesShown;
    private bool _devicesRenderQueued;
    private bool _devicesDialogOpen;
    private DispatcherTimer? _devicesCountdown;
    private DispatcherTimer? _devicesStatusTimer;

    /// <summary>The one model, created with the window's dispatcher (the
    /// clipboard adapter needs it). The short-status provider is null until
    /// SA-4g wires the dashboard overlay: the line stays hidden.</summary>
    private DevicesModel Devices
    {
        get
        {
            if (_devicesModel is null)
            {
                _devicesModel = new DevicesModel(new TbDevicesClient(), new DevicesClipboard(DispatcherQueue));
                _devicesModel.Changed += QueueDevicesRender;
            }

            return _devicesModel;
        }
    }

    /// <summary>The controls of the one page build.</summary>
    private sealed class DevicesPage
    {
        public StackPanel Root { get; } = new() { Spacing = 16, MaxWidth = 380 };
        public TextBlock ServiceState { get; } = Ui.Text("", 12, 0.6);
        public StackPanel NotRunning { get; } = new() { Spacing = 6 };
        public TextBlock StartLine { get; } = Notice("");
        public Button Retry { get; } = SmallButton(DevicesCopy.Retry);
        public StackPanel RunningBody { get; } = new() { Spacing = 6 };
        public ToggleSwitch Sharing { get; } = new() { OnContent = null, OffContent = null };
        public bool SyncingSharing { get; set; }
        public TextBlock NeedsScope { get; } = Notice(DevicesCopy.NeedsScope.Localized());
        public TextBlock SharingError { get; } = Notice("");
        public TextBox Name { get; } = new() { Width = DevicesFieldWidth, FontSize = 12 };
        public ComboBox Host { get; } = new() { MaxWidth = DevicesAddressWidth, MinWidth = 120, FontSize = 12 };
        public bool SyncingHost { get; set; }
        public string HostSignature { get; set; } = "";
        /// <summary>The user picked an address not yet applied: no menu
        /// rebuild until Apply or the next appear.</summary>
        public bool HostPicked { get; set; }
        public string HostValue { get; set; } = "";
        public IReadOnlyList<string> Addresses { get; set; } = [];
        public TextBox Port { get; } = new() { Width = DevicesFieldWidth, FontSize = 12 };
        public Button Apply { get; } = SmallButton(DevicesCopy.Apply);
        public TextBlock Unreachable { get; } = Notice(DevicesCopy.UnreachableLine.Localized());
        public TextBlock Public { get; } = Notice(DevicesCopy.PublicAddress.Localized());
        public TextBlock ListenError { get; } = Notice("");
        public bool Seeded { get; set; }
        public StackPanel Invitation { get; } = new() { Spacing = 6 };
        public string InvitationSignature { get; set; } = "?";
        public TextBlock ExpiresIn { get; } = Ui.Text("", 11, 0.6);
        public TextBlock OfferError { get; } = Notice("");
        public StackPanel Requests { get; } = new() { Spacing = 6 };
        public string RequestsSignature { get; set; } = "?";
        public TextBlock DecideError { get; } = Notice("");
        public StackPanel GrantRows { get; } = new() { Spacing = 6 };
        public string GrantsSignature { get; set; } = "?";
        public TextBlock ManageError { get; } = Notice("");
        public StackPanel Scope { get; } = new() { Spacing = 6 };
        public StackPanel ScopeBody { get; } = new() { Spacing = 6 };
        public object? ScopeShown { get; set; }
        public StackPanel Linked { get; } = new() { Spacing = 6 };
        public StackPanel PeerRows { get; } = new() { Spacing = 6 };
        public string PeersSignature { get; set; } = "?";
        public List<(RemotePeer Peer, StackPanel Line)> StatusLines { get; } = [];
        public StackPanel LinkControls { get; } = new() { Spacing = 6 };
        /// <summary>The paste box and Link button, built once and never
        /// re-parented; only the row's Visibility changes. Observed on 188
        /// (2026-11-23, build 1ac63b98): pressing Link when the render moved
        /// these two controls into a new row ended the app in CoreMessagingXP;
        /// on 203dfee8 (this layout) the same press did not. Buttons that are
        /// only removed after their click (Create, Copy, Cancel invitation)
        /// were pressed on 188 without a crash.</summary>
        public StackPanel LinkRow { get; } = new() { Orientation = Orientation.Horizontal, Spacing = 8 };
        public StackPanel LinkExtra { get; } = new() { Spacing = 6 };
        public DevicesPairing? LinkShown { get; set; }
        public PasswordBox Paste { get; } = new()
        {
            // Masked: the invitation is a secret until it is claimed. A
            // PasswordBox has no spell check or text prediction.
            PasswordRevealMode = PasswordRevealMode.Hidden,
            FontSize = 12,
            // A fixed width: in the horizontal row an auto-sized box grows with
            // the pasted invitation (hundreds of characters) and pushes Link off
            // the page (188, 2026-11-23: a real Pair URL left Link offscreen).
            // Observed on 188 (zh-Hant, 200% scale, default 900-wide window):
            // 290 + 8 spacing + the Link button fit the 380 page. Not measured on
            // a narrower window or larger text scaling.
            Width = DevicesPasteWidth,
            PlaceholderText = DevicesCopy.PastePlaceholder.Localized(),
        };
        public Button Link { get; } = SmallButton(DevicesCopy.Link);
    }

    private StackPanel BuildDevicesPage()
    {
        if (_devicesPage is { } built)
        {
            return built.Root;
        }

        var page = _devicesPage = new DevicesPage();
        var model = Devices;

        // ── (A) Share this PC ───────────────────────────────────────────
        var share = new StackPanel { Spacing = 6 };
        share.Children.Add(Ui.Row(Ui.Text(DevicesCopy.Service.Localized(), 12), page.ServiceState));
        page.NotRunning.Children.Add(page.StartLine);
        page.Retry.Click += async (_, _) => await model.RetryStart();
        page.NotRunning.Children.Add(page.Retry);
        share.Children.Add(page.NotRunning);

        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(page.Sharing, DevicesCopy.ShareToggle.Localized());
        page.Sharing.Toggled += async (_, _) =>
        {
            if (!page.SyncingSharing)
            {
                await model.SetSharing(page.Sharing.IsOn);
            }
        };
        var body = page.RunningBody;
        body.Children.Add(ToggleRow(DevicesCopy.ShareToggle.Localized(), page.Sharing));
        body.Children.Add(Hint(DevicesCopy.ShareOffNote.Localized()));
        body.Children.Add(page.NeedsScope);
        body.Children.Add(page.SharingError);

        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(page.Name, DevicesCopy.DeviceName.Localized());
        body.Children.Add(Ui.Row(RowLabel(DevicesCopy.DeviceName), page.Name));
        body.Children.Add(Hint(DevicesCopy.DeviceNameHint.Localized()));
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(page.Host, DevicesCopy.ListenOn.Localized());
        page.Host.SelectionChanged += (_, _) =>
        {
            if (!page.SyncingHost && page.Host.SelectedItem is ComboBoxItem { Tag: string host })
            {
                page.HostValue = host;
                // Rendered after this handler returns; it never rebuilds the
                // items while HostPicked holds (see RenderHostChoices).
                page.HostPicked = true;
                _ = DispatcherQueue.TryEnqueue(RenderDevices);
            }
        };
        body.Children.Add(Ui.Row(RowLabel(DevicesCopy.ListenOn), page.Host));
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(page.Port, DevicesCopy.Port.Localized());
        page.Port.FontFamily = DevicesSasFont;
        body.Children.Add(Ui.Row(RowLabel(DevicesCopy.Port), page.Port));
        page.Apply.Click += async (_, _) =>
        {
            page.HostPicked = false; // the menu may follow the stored address again
            var host = page.HostValue;
            var port = int.TryParse(page.Port.Text.Trim(), out var parsed) ? parsed : 0;
            var typed = page.Name.Text;
            // The field shows what is sent.
            page.Name.Text = DeviceNameCleaning.CleanedName(typed) ?? "";
            await model.ApplyListen(host, port, typed);
        };
        body.Children.Add(page.Apply);
        body.Children.Add(Hint(DevicesCopy.FirewallNote.Localized()));
        body.Children.Add(page.Unreachable);
        body.Children.Add(page.Public);
        body.Children.Add(page.ListenError);
        body.Children.Add(page.Invitation);
        body.Children.Add(page.OfferError);
        body.Children.Add(page.Requests);
        body.Children.Add(page.DecideError);
        body.Children.Add(page.GrantRows);
        body.Children.Add(page.ManageError);
        share.Children.Add(body);
        page.Root.Children.Add(InfoSection(
            DevicesCopy.ShareSection.Localized(), DevicesCopy.ShareSummary, DevicesCopy.ShareInfoRows, share));

        // ── (B) Scan scope ──────────────────────────────────────────────
        page.Scope.Children.Add(SectionHeader(DevicesCopy.ScopeSection.Localized()));
        page.Scope.Children.Add(Hint(DevicesCopy.ScopeSummary.Localized()));
        page.Scope.Children.Add(Wrapped(Hint(DevicesCopy.ScopeBody.Localized())));
        page.Scope.Children.Add(page.ScopeBody);
        page.Root.Children.Add(page.Scope);

        // ── (C) Linked devices ──────────────────────────────────────────
        var linked = new StackPanel { Spacing = 6 };
        linked.Children.Add(page.PeerRows);
        linked.Children.Add(Caption(DevicesCopy.LinkAnother));
        page.LinkRow.Children.Add(page.Paste);
        page.LinkRow.Children.Add(page.Link);
        page.LinkControls.Children.Add(page.LinkRow);
        page.LinkControls.Children.Add(page.LinkExtra);
        linked.Children.Add(page.LinkControls);
        page.Link.Click += async (_, _) =>
        {
            var text = page.Paste.Password;
            page.Paste.Password = "";
            await model.Link(text);
        };
        page.Paste.PasswordChanged += (_, _) => page.Link.IsEnabled = page.Paste.Password.Length > 0;
        page.Linked.Children.Add(InfoSection(
            DevicesCopy.LinkedSection.Localized(), DevicesCopy.LinkedSummary, DevicesCopy.LinkedInfoRows, linked));
        page.Root.Children.Add(page.Linked);

        RenderDevices();
        return page.Root;
    }

    // ── Appear / disappear ──────────────────────────────────────────────

    /// <summary>Appear = the Devices item is selected while the window is
    /// shown; disappear = another item, or the window hidden. Called from
    /// SelectionChanged, Present and Closing. A rebuild in between changes
    /// nothing here: the model is the window's, and its poll is idempotent.</summary>
    private void SyncDevicesPage()
    {
        var shown = _selectedTag == "devices" && AppWindow.IsVisible;
        if (shown == _devicesShown)
        {
            return;
        }

        _devicesShown = shown;
        var model = Devices;
        if (shown)
        {
            if (_devicesPage is { } page)
            {
                page.Seeded = false;
                page.Addresses = ListenAddress.InterfaceAddresses();
            }

            model.PageAppeared();
            _devicesCountdown ??= NewTimer(TimeSpan.FromSeconds(1), UpdateCountdown);
            _devicesStatusTimer ??= NewTimer(DevicesStatusTick, UpdateShortStatuses);
            _devicesCountdown.Start();
            _devicesStatusTimer.Start();
            RenderDevices();
        }
        else
        {
            _devicesCountdown?.Stop();
            _devicesStatusTimer?.Stop();
            if (_devicesPage is { } page)
            {
                page.Paste.Password = "";
            }

            model.PageDisappeared();
        }
    }

    private DispatcherTimer NewTimer(TimeSpan interval, Action tick)
    {
        var timer = new DispatcherTimer { Interval = interval };
        timer.Tick += (_, _) => tick();
        return timer;
    }

    private void QueueDevicesRender()
    {
        lock (_devicesRenderGate)
        {
            if (_devicesRenderQueued)
            {
                return;
            }

            _devicesRenderQueued = true;
        }

        if (!DispatcherQueue.TryEnqueue(() =>
            {
                lock (_devicesRenderGate) { _devicesRenderQueued = false; }
                RenderDevices();
            }))
        {
            lock (_devicesRenderGate) { _devicesRenderQueued = false; }
        }
    }

    private readonly object _devicesRenderGate = new();

    // ── Render (UI thread) ──────────────────────────────────────────────

    private void RenderDevices()
    {
        if (_devicesPage is not { } page || _devicesModel is not { } model)
        {
            return;
        }

        var status = model.Status;
        var running = model.Running;
        page.ServiceState.Text = (running ? DevicesCopy.ServiceRunning : DevicesCopy.ServiceStopped).Localized();
        page.NotRunning.Visibility = Show(!running);
        page.StartLine.Text = (model.StartFailure ?? DevicesCopy.NotRunning).Localized();
        page.Retry.IsEnabled = !model.Working;
        page.RunningBody.Visibility = Show(running);
        page.Scope.Visibility = Show(running);
        page.Linked.Visibility = Show(running);

        if (running && !page.Seeded && status is not null)
        {
            // Fields are filled from the first running status, once per appear.
            page.HostValue = status.BindHost ?? "";
            page.HostPicked = false;
            page.HostSignature = ""; // rebuild once with the seeded selection
            page.Port.Text = status.BindPort?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "";
            page.Name.Text = status.NodeLabel ?? "";
            page.Seeded = true;
        }

        page.SyncingSharing = true;
        try
        {
            page.Sharing.IsOn = model.SharingOn;
            page.Sharing.IsEnabled = model.CanToggleSharing;
        }
        finally
        {
            page.SyncingSharing = false;
        }

        page.NeedsScope.Visibility = Show(!model.ScopeApproved && !model.SharingOn);
        SetNotice(page.SharingError, model.SharingError);

        var offered = ListenAddress.Choices(page.Addresses, [status?.BindHost ?? "", page.HostValue]);
        RenderHostChoices(page, offered, status?.BindHost ?? "");
        page.Apply.IsEnabled = offered.Contains(page.HostValue);
        page.Unreachable.Visibility = Show(model.Unreachable);
        page.Public.Visibility = Show(ListenAddress.MayBePublic(page.HostValue));
        SetNotice(page.ListenError, model.ListenError);

        RenderInvitation(page, model);
        SetNotice(page.OfferError, model.OfferError);
        RenderRequests(page, model);
        SetNotice(page.DecideError, model.DecideError);
        RenderGrants(page, model);
        SetNotice(page.ManageError, model.ManageError);
        RenderScope(page, model);
        RenderPeers(page, model);
        RenderLink(page, model);
    }

    /// <summary>Rebuilds the menu when the offered list or the stored
    /// address changes, but never while the drop-down is open and never
    /// between a pick and the next Apply or appear (HostPicked); a change
    /// skipped then is picked up by a later render. Observed on 188
    /// (2026-11-23, build 69585e59): picking a LAN address while "Choose an
    /// address" showed, which rebuilt the items in the render queued by that
    /// pick, ended the app with E_UNEXPECTED in Microsoft.UI.Xaml; the same
    /// pick on 1ac63b98, which skips that rebuild, did not.</summary>
    private static void RenderHostChoices(DevicesPage page, IReadOnlyList<string> offered, string stored)
    {
        var current = page.HostValue;
        var signature = string.Join('\n', offered) + "\n|" + stored;
        if (signature == page.HostSignature || page.Host.IsDropDownOpen || page.HostPicked)
        {
            return;
        }

        page.HostSignature = signature;
        page.SyncingHost = true;
        try
        {
            page.Host.Items.Clear();
            ComboBoxItem? selected = null;
            if (!offered.Contains(current))
            {
                // A selection the list cannot offer (nothing set yet, or a
                // stored value that is not an address) shows as "choose".
                selected = new ComboBoxItem { Content = DevicesCopy.ChooseAddress.Localized(), Tag = current };
                page.Host.Items.Add(selected);
            }

            foreach (var address in offered)
            {
                var item = new ComboBoxItem
                {
                    Content = address == ListenAddress.LocalOnly ? DevicesCopy.LocalOnlyLabel.Localized() : address,
                    Tag = address,
                };
                page.Host.Items.Add(item);
                if (address == current)
                {
                    selected = item;
                }
            }

            page.Host.SelectedItem = selected;
        }
        finally
        {
            page.SyncingHost = false;
        }
    }

    private void RenderInvitation(DevicesPage page, DevicesModel model)
    {
        var offer = model.Offer;
        var signature = offer is null ? $"none|{model.SharingOn}" : $"{offer.OfferId}|{model.Copied}";
        if (signature != page.InvitationSignature)
        {
            page.InvitationSignature = signature;
            page.Invitation.Children.Clear();
            if (offer is not null)
            {
                page.Invitation.Children.Add(Hint(DevicesCopy.InvitationReady.Localized()));
                page.Invitation.Children.Add(page.ExpiresIn);
                var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
                var copy = SmallButton(DevicesCopy.Copy);
                copy.Click += (_, _) =>
                {
                    try
                    {
                        model.CopyOffer();
                    }
                    catch (Exception ex)
                    {
                        // The type only: never the Pair URL.
                        DevLog.Write($"devices: copy failed {ex.GetType().Name}");
                    }
                };
                var cancel = SmallButton(DevicesCopy.CancelInvitation);
                cancel.Click += (_, _) => model.ClearOffer();
                buttons.Children.Add(copy);
                buttons.Children.Add(cancel);
                if (model.Copied)
                {
                    var copied = Ui.Text(DevicesCopy.CopiedLine.Localized(), 10, 0.6);
                    copied.VerticalAlignment = VerticalAlignment.Center;
                    buttons.Children.Add(copied);
                }

                page.Invitation.Children.Add(buttons);
            }
            else
            {
                var create = SmallButton(DevicesCopy.CreateInvitation);
                create.IsEnabled = model.SharingOn;
                create.Click += async (_, _) => await model.CreateOffer();
                page.Invitation.Children.Add(create);
            }
        }

        UpdateCountdown();
    }

    private void UpdateCountdown()
    {
        if (_devicesPage is { } page && _devicesModel?.Offer is { } offer)
        {
            page.ExpiresIn.Text = DevicesCopy.ExpiresIn.Localized(_devicesModel.RemainingForOffer(offer));
        }
    }

    private void RenderRequests(DevicesPage page, DevicesModel model)
    {
        var pending = model.Pending;
        var signature = string.Join('\n', pending.Select(p => $"{p.PendingId}:{model.IsDeciding(p.PendingId)}"));
        if (signature == page.RequestsSignature)
        {
            return;
        }

        page.RequestsSignature = signature;
        page.Requests.Children.Clear();
        if (pending.Count == 0)
        {
            return;
        }

        page.Requests.Children.Add(Caption(DevicesCopy.Requests));
        // Each row's buttons send that row's own record (its PendingId, the
        // one shown with this SAS), never an index (R5).
        foreach (var request in pending)
        {
            var row = request;
            var sas = Ui.Text(row.SasCode, 13, bold: true);
            sas.FontFamily = DevicesSasFont;
            sas.IsTextSelectionEnabled = true;
            sas.VerticalAlignment = VerticalAlignment.Center;
            var reject = SmallButton(DevicesCopy.Reject);
            var approve = SmallButton(DevicesCopy.Approve);
            var deciding = model.IsDeciding(row.PendingId);
            reject.IsEnabled = approve.IsEnabled = !deciding;
            reject.Click += async (_, _) => await model.Decide(row, approve: false);
            approve.Click += async (_, _) => await model.Decide(row, approve: true);
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            buttons.Children.Add(reject);
            buttons.Children.Add(approve);
            var card = new StackPanel { Spacing = 6 };
            card.Children.Add(Ui.Row(sas, buttons));
            card.Children.Add(Wrapped(Hint(DevicesCopy.CompareCode.Localized())));
            card.Children.Add(Wrapped(Hint(DevicesCopy.ApproveEffect.Localized())));
            page.Requests.Children.Add(DevicesCard(card));
        }
    }

    private void RenderGrants(DevicesPage page, DevicesModel model)
    {
        var active = model.Grants.Where(g => g.State == "active").ToList();
        var signature = string.Join('\n', active.Select(g => g.GrantId));
        if (signature == page.GrantsSignature)
        {
            return;
        }

        page.GrantsSignature = signature;
        page.GrantRows.Children.Clear();
        if (active.Count == 0)
        {
            return;
        }

        page.GrantRows.Children.Add(Caption(DevicesCopy.GrantsHeader));
        foreach (var grant in active)
        {
            var target = grant;
            // DR-3b: a grant lasts 100 years; the fingerprint only, no date.
            var line = Ui.Text(target.PeerFingerprint, 12);
            line.IsTextSelectionEnabled = true;
            line.VerticalAlignment = VerticalAlignment.Center;
            var revoke = SmallButton(DevicesCopy.Revoke);
            revoke.Click += async (_, _) =>
            {
                if (await ConfirmDevices(
                    DevicesCopy.RevokeTitle.Localized(target.PeerFingerprint),
                    DevicesCopy.RevokeMessage.Localized(), DevicesCopy.Revoke.Localized()))
                {
                    await model.RevokeGrant(target);
                }
            };
            page.GrantRows.Children.Add(Ui.Row(line, revoke));
        }
    }

    private void RenderScope(DevicesPage page, DevicesModel model)
    {
        var disclosure = model.Disclosure;
        var shown = (disclosure, model.ScopeApproved, model.ScopeNotice);
        if (Equals(page.ScopeShown, shown))
        {
            return;
        }

        page.ScopeShown = shown;
        var body = page.ScopeBody;
        body.Children.Clear();
        if (disclosure is not null)
        {
            AddPathCard(body, DevicesCopy.HomeFolder, [disclosure.Home]);
            AddPathCard(body, DevicesCopy.ConfigFolder, [disclosure.ConfigDir]);
            AddPathCard(body, DevicesCopy.DataFolder, DevicesModel.DataFolderPaths(disclosure));
            AddPathCard(body, DevicesCopy.AddedFolders, DevicesModel.Flatten(disclosure.ExtraScanPaths));
            AddPathCard(body, DevicesCopy.ExcludedFolders, DevicesModel.Flatten(disclosure.ExcludedScanPaths));
            AddPathCard(body, DevicesCopy.OpenCodeDatabases, disclosure.OpencodeDbPaths);
            AddPathCard(body, DevicesCopy.Clients, [string.Join(", ", disclosure.Clients)]);
            if (disclosure.EnvRootsSet)
            {
                body.Children.Add(Notice(DevicesCopy.EnvRoots.Localized(DevicesModel.VariableNames(disclosure.EnvRoots))));
            }
        }

        body.Children.Add(model.ScopeApproved
            ? Hint(DevicesCopy.ScopeApprovedLine.Localized())
            : Notice(DevicesCopy.NeedsApproval.Localized()));
        var approve = SmallButton(DevicesCopy.ApproveScope);
        approve.IsEnabled = disclosure is not null && !model.ScopeApproved;
        // The digest of the very disclosure these cards were drawn from,
        // captured with them: never read again for the click (R4).
        var shownDigest = disclosure?.OwnerInputsSha256;
        approve.Click += async (_, _) =>
        {
            if (shownDigest is not null)
            {
                await model.ApproveScope(shownDigest);
            }
        };
        body.Children.Add(approve);
        if (model.ScopeNotice is { } notice)
        {
            body.Children.Add(Notice(notice.Localized()));
        }
    }

    /// <summary>A labelled list in a card; an empty list is left out (DSV :298-318).</summary>
    private static void AddPathCard(StackPanel into, string label, IReadOnlyList<string> items)
    {
        if (items.Count == 0 || (items.Count == 1 && items[0].Length == 0))
        {
            return;
        }

        var card = new StackPanel { Spacing = 2 };
        card.Children.Add(Ui.Text(label.Localized(), 12));
        foreach (var item in items)
        {
            var path = Ui.Text(item, 11, 0.6);
            path.TextWrapping = TextWrapping.Wrap;
            path.TextTrimming = TextTrimming.None;
            path.IsTextSelectionEnabled = true;
            card.Children.Add(path);
        }

        into.Children.Add(DevicesCard(card));
    }

    private void RenderPeers(DevicesPage page, DevicesModel model)
    {
        var peers = model.Peers;
        var signature = string.Join('\n', peers.Select(p =>
            $"{p.ProducerStatic}|{model.NameLine(p)}|{p.Host}|{p.Port}|{p.State}"));
        if (signature == page.PeersSignature)
        {
            UpdateShortStatuses();
            return;
        }

        page.PeersSignature = signature;
        page.PeerRows.Children.Clear();
        page.StatusLines.Clear();
        if (peers.Count == 0)
        {
            page.PeerRows.Children.Add(Hint(DevicesCopy.NoLinked.Localized()));
        }

        foreach (var peer in peers)
        {
            var target = peer;
            var rows = new StackPanel { Spacing = 2 };
            // The name its owner gave it, only as data, on its own line.
            if (model.NameLine(target) is { } name)
            {
                rows.Children.Add(Ui.Text(name, 12, bold: true));
            }

            var address = Ui.Text(DevicesModel.Address(target), 12);
            address.IsTextSelectionEnabled = true;
            address.VerticalAlignment = VerticalAlignment.Center;
            var trailing = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            var state = Ui.Text(DevicesCopy.State(target.State).Localized(), 10, 0.6);
            state.VerticalAlignment = VerticalAlignment.Center;
            var remove = SmallButton(DevicesCopy.Remove);
            remove.Click += async (_, _) =>
            {
                if (await ConfirmDevices(
                    DevicesCopy.RemoveTitle.Localized(DevicesModel.Address(target)),
                    DevicesCopy.RemoveMessage.Localized(), DevicesCopy.Remove.Localized()))
                {
                    await model.RemovePeer(target);
                }
            };
            trailing.Children.Add(state);
            trailing.Children.Add(remove);
            rows.Children.Add(Ui.Row(address, trailing));
            var statusLine = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            rows.Children.Add(statusLine);
            page.StatusLines.Add((target, statusLine));
            var fingerprint = Hint(target.Fingerprint);
            fingerprint.IsTextSelectionEnabled = true;
            rows.Children.Add(fingerprint);
            page.PeerRows.Children.Add(rows);
        }

        UpdateShortStatuses();
    }

    /// <summary>The third line under each linked device, from what the
    /// dashboard last learned; hidden without a result (no state is made up).</summary>
    private void UpdateShortStatuses()
    {
        if (_devicesPage is not { } page || _devicesModel is not { } model)
        {
            return;
        }

        foreach (var (peer, line) in page.StatusLines)
        {
            line.Children.Clear();
            if (model.ShortStatus(peer) is not { } status)
            {
                line.Visibility = Visibility.Collapsed;
                continue;
            }

            line.Visibility = Visibility.Visible;
            line.Children.Add(Ui.Disc(ShortDotColor(status.Dot), 7));
            line.Children.Add(Ui.Text(status.Text, 10, 0.6));
        }
    }

    /// <summary>Dot colors (4e1 ShortDot): green connected, the pace orange
    /// for trouble, grey otherwise. ponytail: a filled grey dot for both
    /// "not included" and "updating"; SA-4g draws the dashboard's own dots.</summary>
    private static string ShortDotColor(DeviceOverlayCopy.ShortDot dot) => dot switch
    {
        DeviceOverlayCopy.ShortDot.Green => "#34C759",
        DeviceOverlayCopy.ShortDot.Orange => DashboardView.PaceOrange,
        _ => "#8E8E93",
    };

    private void RenderLink(DevicesPage page, DevicesModel model)
    {
        var pairing = model.Pairing;
        if (Equals(page.LinkShown, pairing))
        {
            return;
        }

        var wasBusy = page.LinkShown is { Kind: PairingKind.Previewing or PairingKind.Claiming };
        page.LinkShown = pairing;
        var into = page.LinkExtra;
        into.Children.Clear();
        page.LinkRow.Visibility = Visibility.Collapsed; // shown again by the default branch
        switch (pairing.Kind)
        {
            case PairingKind.Waiting when pairing.Claim is { } claim:
                var code = Ui.Text(DevicesCopy.YourCode.Localized(claim.Sas), 13, bold: true);
                code.FontFamily = DevicesSasFont;
                code.IsTextSelectionEnabled = true;
                into.Children.Add(code);
                into.Children.Add(Wrapped(Hint(DevicesCopy.WaitingApproval.Localized())));
                var cancelWait = SmallButton(DevicesCopy.Cancel);
                cancelWait.Click += (_, _) => model.CancelWait();
                into.Children.Add(cancelWait);
                break;
            case PairingKind.Confirming when pairing.Destination is { } destination:
                into.Children.Add(Ui.Text(DevicesCopy.LinkDestination.Localized(), 12));
                // In full: a remote string, never a key, never cut.
                var where = Ui.Text(DevicesModel.Address(destination.Host, destination.Port), 12);
                where.FontFamily = DevicesSasFont;
                where.TextWrapping = TextWrapping.Wrap;
                where.TextTrimming = TextTrimming.None;
                where.IsTextSelectionEnabled = true;
                into.Children.Add(where);
                into.Children.Add(Wrapped(Hint(DevicesCopy.LinkDestinationHint.Localized())));
                var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
                var cancel = SmallButton(DevicesCopy.Cancel);
                cancel.Click += (_, _) => model.CancelLink();
                var confirm = SmallButton(DevicesCopy.Link);
                confirm.Click += async (_, _) => await model.ConfirmLink();
                buttons.Children.Add(cancel);
                buttons.Children.Add(confirm);
                into.Children.Add(buttons);
                break;
            case PairingKind.Previewing or PairingKind.Claiming:
                into.Children.Add(new ProgressRing { IsActive = true, Width = 16, Height = 16, HorizontalAlignment = HorizontalAlignment.Left });
                break;
            default:
                page.LinkRow.Visibility = Visibility.Visible;
                page.Link.IsEnabled = page.Paste.Password.Length > 0;
                if (wasBusy)
                {
                    _ = page.Paste.Focus(FocusState.Programmatic);
                }
                if (pairing.Kind == PairingKind.Linked)
                {
                    into.Children.Add(Hint(DevicesCopy.LinkedLine.Localized()));
                }
                else if (pairing is { Kind: PairingKind.Failed, Failure: { } failure })
                {
                    into.Children.Add(Notice(failure.Localized()));
                }

                break;
        }
    }

    /// <summary>The revoke and remove confirmations: a destructive primary
    /// button and Cancel. One dialog at a time: a second ContentDialog open
    /// on the same XamlRoot throws (UpdateDialog's note), so a click while one
    /// is open is ignored; a throw from ShowAsync counts as Cancel.</summary>
    private async Task<bool> ConfirmDevices(string title, string message, string destructive)
    {
        if (_devicesDialogOpen || Content.XamlRoot is not { } root)
        {
            return false;
        }

        _devicesDialogOpen = true;
        try
        {
            var dialog = new ContentDialog
            {
                XamlRoot = root,
                Title = title,
                Content = Wrapped(Ui.Text(message, 13)),
                PrimaryButtonText = destructive,
                CloseButtonText = DevicesCopy.Cancel.Localized(),
                DefaultButton = ContentDialogButton.Close,
            };
            // Destructive: red text on the default button face (the maintainer's
            // choice, 2026-11-23). The critical fill as the background with
            // white text read as disabled on 188 (pale pink, grey-looking text).
            var style = new Style(typeof(Button));
            style.Setters.Add(new Setter(Control.ForegroundProperty,
                Application.Current.Resources["SystemFillColorCriticalBrush"]));
            dialog.PrimaryButtonStyle = style;
            return await dialog.ShowAsync() == ContentDialogResult.Primary;
        }
        catch (Exception ex)
        {
            DevLog.Write($"devices: dialog failed {ex.GetType().Name}");
            return false;
        }
        finally
        {
            _devicesDialogOpen = false;
        }
    }

    // ── Pieces ──────────────────────────────────────────────────────────

    private static Visibility Show(bool visible) => visible ? Visibility.Visible : Visibility.Collapsed;

    private static Button SmallButton(string key) => new()
    {
        Content = key.Localized(),
        FontSize = 12,
        Padding = new Thickness(8, 3, 8, 4),
    };

    private static TextBlock RowLabel(string key)
    {
        var label = Ui.Text(key.Localized(), 12);
        label.VerticalAlignment = VerticalAlignment.Center;
        return label;
    }

    /// <summary>A sub-heading inside a section (DSV :417-421).</summary>
    private static TextBlock Caption(string key) => Ui.Text(key.Localized(), 10, bold: true);

    /// <summary>A state notice: the orange line the Accounts page uses (DSV :424-429).</summary>
    private static TextBlock Notice(string text) => Wrapped(WarningLine(text));

    private static void SetNotice(TextBlock line, string? key)
    {
        line.Text = key?.Localized() ?? "";
        line.Visibility = Show(key is not null);
    }

    private static TextBlock Wrapped(TextBlock text)
    {
        text.TextWrapping = TextWrapping.Wrap;
        text.TextTrimming = TextTrimming.None;
        return text;
    }

    /// <summary>The macOS glass card in the Fluent card colors.</summary>
    private static Border DevicesCard(UIElement child) => new()
    {
        Child = child,
        Padding = new Thickness(10, 7, 10, 7),
        CornerRadius = new CornerRadius(8),
        BorderThickness = new Thickness(1),
        Background = (Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"],
        BorderBrush = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
    };
}
