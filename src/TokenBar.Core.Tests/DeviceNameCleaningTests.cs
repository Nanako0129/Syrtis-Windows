using TokenBar.Core;

namespace TokenBar.Core.Tests;

/// <summary>Device-name cleaning against macOS <c>DevicesModel.cleanedName</c>
/// (DevicesModel.swift :233-262). The macOS self-test reaches it through the
/// name that is sent (SelfTest.swift :22908-22918, :22920-22923) and directly
/// at :22919; here each case is a direct input/output pair. The 128-byte
/// assertion (:22920-22923) belongs to the apply path (4f).</summary>
public class DeviceNameCleaningTests
{
    [Theory] // :22909-22918
    [InlineData("  desk  ", "desk")]
    [InlineData("de\u0007s\tk  ", "desk")]
    [InlineData("‮desk‪‫‬‭⁦⁧⁨⁩", "desk")]
    [InlineData("d؜e‎s‏k", "desk")]
    [InlineData("a b", "a b")]
    [InlineData("Café \U0001F469‍\U0001F4BB", "Café \U0001F469‍\U0001F4BB")]
    [InlineData(" ‮desk\u0007 ", "desk")] // :22919
    public void ControlSeparatorAndBidiCharactersAndSurroundingWhitespaceGoInnerSpacesAndJoinersStay(
        string typed, string expected) =>
        Assert.Equal(expected, DeviceNameCleaning.CleanedName(typed));

    [Theory] // :22915-22917, :22919
    [InlineData("")]
    [InlineData("   \t\n ")]
    [InlineData("‮\u0007")]
    [InlineData("​ ﻿⠀")]
    [InlineData("‮")]
    public void ANameWithNothingVisibleLeftIsNoName(string typed) =>
        Assert.Null(DeviceNameCleaning.CleanedName(typed));
}
