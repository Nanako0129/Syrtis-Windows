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
    [InlineData("de\u0007s\tk\u2028\u2029", "desk")]
    [InlineData("\u202Edesk\u202A\u202B\u202C\u202D\u2066\u2067\u2068\u2069", "desk")]
    [InlineData("d\u061Ce\u200Es\u200Fk", "desk")]
    [InlineData("a b", "a b")]
    [InlineData("Caf\u00E9 \U0001F469\u200D\U0001F4BB", "Caf\u00E9 \U0001F469\u200D\U0001F4BB")]
    [InlineData(" \u202Edesk\u0007 ", "desk")] // :22919
    public void ControlSeparatorAndBidiCharactersAndSurroundingWhitespaceGoInnerSpacesAndJoinersStay(
        string typed, string expected) =>
        Assert.Equal(expected, DeviceNameCleaning.CleanedName(typed));

    [Theory] // :22915-22917, :22919
    [InlineData("")]
    [InlineData("   \t\n ")]
    [InlineData("\u202E\u0007")]
    [InlineData("\u200B \uFEFF\u2800")]
    [InlineData("\u202E")]
    public void ANameWithNothingVisibleLeftIsNoName(string typed) =>
        Assert.Null(DeviceNameCleaning.CleanedName(typed));
}
