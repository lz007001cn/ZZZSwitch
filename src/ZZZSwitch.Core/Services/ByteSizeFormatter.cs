namespace ZZZSwitch.Core.Services;

public static class ByteSizeFormatter
{
    private static readonly string[] Units = ["B", "KiB", "MiB", "GiB", "TiB"];

    public static string Format(long bytes)
    {
        var value = (double)Math.Max(0, bytes);
        var unit = 0;
        while (value >= 1024 && unit < Units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0
            ? $"{value:N0} {Units[unit]}"
            : $"{value:0.##} {Units[unit]}";
    }
}
