namespace ClaudeCodeIndicator;

/// <summary>The three states Claude Code reports through hooks.</summary>
public enum IndicatorState { Done, Working, Waiting }

public static class IndicatorStates
{
    /// <summary>
    /// Display label and color for a state. The label, lower-cased, is also the
    /// <c>value=</c> sent to the ESP, so don't rename these.
    /// </summary>
    public static (string Label, string Hex) Describe(IndicatorState s) => s switch
    {
        IndicatorState.Working => ("Working", "D37355"),
        IndicatorState.Waiting => ("Waiting", "E53935"),
        _                      => ("Done",    "43A047"),
    };

    /// <summary>The state's color as RGB components, for drawing tray icons.</summary>
    public static (byte R, byte G, byte B) Rgb(IndicatorState s)
    {
        string hex = Describe(s).Hex;
        return (System.Convert.ToByte(hex[..2], 16),
                System.Convert.ToByte(hex[2..4], 16),
                System.Convert.ToByte(hex[4..], 16));
    }
}
