namespace VocalPatch.Core.Compat;

/// <summary>
/// Small numeric helpers that exist on modern .NET but not on netstandard2.0,
/// so the same source compiles for every target the toolkit supports.
/// </summary>
public static class Num
{
    public static int Clamp(int value, int min, int max) =>
        value < min ? min : value > max ? max : value;

    public static double Clamp(double value, double min, double max) =>
        value < min ? min : value > max ? max : value;

    public static float Clamp(float value, float min, float max) =>
        value < min ? min : value > max ? max : value;
}

/// <summary>netstandard2.0 replacement for <c>string.Contains(string, StringComparison)</c>.</summary>
public static class StringCompat
{
    public static bool Contains(this string? text, string value, StringComparison comparison)
    {
        if (text == null) return false;
        return text.IndexOf(value, comparison) >= 0;
    }
}
