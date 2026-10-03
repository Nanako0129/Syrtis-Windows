namespace TokenBar.Core;

/// <summary>The live-rate badge's network LED (macOS PopoverView.swift
/// activityLED): steady and dim while idle; while tokens flow, a green light
/// that flickers like a router's activity light, mostly lit with brief
/// pseudo-random off-blinks, more of them at higher rates.</summary>
public static class LiveLed
{
    /// <summary>One flicker slot, macOS's 0.09 s TimelineView period.</summary>
    public const int SlotMs = 90;

    /// <summary>Whether the LED is lit in <paramref name="slot"/> (time / 90 ms)
    /// at <paramref name="tokensPerMin"/>. The off-chance grows from 25% near
    /// idle to 45% at 1M tok/min; the slot is hashed so the blinks look
    /// irregular. Wrapping multiply, as Swift's <c>&amp;*</c>.</summary>
    public static bool Lit(ulong slot, double tokensPerMin)
    {
        var hash = unchecked(slot * 0x9E3779B97F4A7C15UL) >> 33;
        var offChance = 25 + (int)Math.Min(20, tokensPerMin / 50_000);
        return (int)(hash % 100) >= offChance;
    }

    public static bool Active(double tokensPerMin) => tokensPerMin > 0;
}
