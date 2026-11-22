namespace TokenBar.Core;

/// <summary>The pure parts of macOS <c>OverviewMerge</c> (OverviewView.swift
/// :9-42): what Overview shows above and in its combined cards. The merge
/// state itself (pages, colors) belongs to the overlay model, SA-4e2.</summary>
public static class OverviewMerge
{
    public enum ModelsCardState { Card, Loading, Failed }

    /// <summary>The lines the overlay lens shows above a combined page, in its
    /// order (:30-33).</summary>
    public static IReadOnlyList<string> NoteLines(bool saturated, bool keptLast)
    {
        List<string> lines = [];
        if (saturated) { lines.Add(DeviceOverlayCopy.Saturated.Localized()); }
        if (keptLast) { lines.Add(DeviceOverlayCopy.KeptLast.Localized()); }
        return lines;
    }

    /// <summary>What the models card shows: the report (an empty one says so
    /// itself), a failure when the request failed with nothing to show, else
    /// loading (:39-41).</summary>
    public static ModelsCardState ModelsCard(bool hasReport, bool failed) =>
        hasReport ? ModelsCardState.Card : failed ? ModelsCardState.Failed : ModelsCardState.Loading;
}
