using TokenBar.Core;

namespace TokenBar.App;

/// <summary>Which device-aware page an overlay holds (macOS
/// <c>DeviceOverlayKind</c>, DashboardModel.swift :2914).</summary>
public enum DeviceOverlayKind
{
    Graph,
    Models,
    Hourly,
    Agents,
}

/// <summary>The pure request decisions of the dashboard device overlay (macOS
/// DeviceOverlayState.kinds(for:) and clientsArgument, DashboardModel.swift
/// :3337-3358). Lives in App because it takes the lens (<see cref="AppView"/>).
/// The stateful overlay model is SA-4e2.</summary>
public static class DeviceOverlayRequests
{
    /// <summary>Overview and Stats share the same two keys (graph and models,
    /// no clients), so moving between them asks the producers once.</summary>
    public static IReadOnlyList<DeviceOverlayKind> KindsFor(AppView view) => view switch
    {
        AppView.Daily or AppView.Monthly => [DeviceOverlayKind.Graph],
        AppView.Overview or AppView.Stats => [DeviceOverlayKind.Graph, DeviceOverlayKind.Models],
        AppView.Models => [DeviceOverlayKind.Models],
        AppView.Hourly => [DeviceOverlayKind.Hourly],
        AppView.Agents => [DeviceOverlayKind.Agents],
        AppView.Quota => [],
        _ => throw new ArgumentOutOfRangeException(nameof(view)),
    };

    /// <summary>Hourly's and Agents' <c>clients</c> argument. A single-client
    /// tab sends its slice. Overview sends null while nothing is hidden, else
    /// the registry's ids minus the hidden ones (not this PC's present list: a
    /// client only a remote device used must still be asked for).</summary>
    public static IReadOnlyList<string>? ClientsArgument(
        string? singleClient, IReadOnlySet<string> hidden)
    {
        if (singleClient is not null) { return ClientRegistry.TabSlice(singleClient); }
        var hiddenAll = ClientRegistry.HiddenTabClients(hidden);
        if (hiddenAll.Count == 0) { return null; }
        return [.. ClientRegistry.AllIds.Where(id => !hiddenAll.Contains(id))];
    }
}
