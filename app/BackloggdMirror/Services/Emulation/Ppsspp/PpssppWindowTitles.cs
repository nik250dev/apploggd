using System.Text.RegularExpressions;

namespace BackloggdMirror.Services.Emulation.Ppsspp;

/// <summary>The running game as PPSSPP names it in its window, both values from the game's PARAM.SFO. A null <paramref name="DiscId"/> means a homebrew without one.</summary>
internal sealed record PpssppTitle(string? DiscId, string? Name);

/// <summary>
/// While a game runs PPSSPP titles its main window "PPSSPP v1.20.4 - ULES00026 : DYNASTY WARRIORS",
/// the same "DISC_ID : TITLE" since at least 1.15, and resets it to "PPSSPP v1.20.4" when the game
/// shuts down. Other instances append " (instance: 2)", debug builds " (debug)".
/// </summary>
internal static class PpssppWindowTitles
{
    private static readonly Regex RunningTitle = new(
        @"^PPSSPP (?:Gold )?\S+ - (.+?)(?: \(debug\))?(?: \(instance: \d+\))?$",
        RegexOptions.Compiled);

    // Retail and PSN serials are four letters and five digits; the IDs PPSSPP makes up for homebrew keep the five digits.
    private static readonly Regex DiscIdAndName = new(@"^(\S{4}\d{5}) : (.*)$", RegexOptions.Compiled);

    /// <summary>Null while PPSSPP sits in its menu.</summary>
    internal static PpssppTitle? Parse(string windowTitle)
    {
        var match = RunningTitle.Match(windowTitle);
        if (!match.Success)
            return null;

        string rest = match.Groups[1].Value;
        var withId = DiscIdAndName.Match(rest);
        if (!withId.Success)
            return new PpssppTitle(null, NameOrNull(rest));

        return new PpssppTitle(withId.Groups[1].Value, NameOrNull(withId.Groups[2].Value));
    }

    private static string? NameOrNull(string value)
    {
        string name = value.Trim();
        return name.Length > 0 ? name : null;
    }
}
