namespace MultiTool.Abstractions;

/// <summary>
/// Numbers written the way people read them. Shared so every module shows sizes the same way.
/// </summary>
public static class Humanize
{
    private static readonly string[] Units = ["B", "KB", "MB", "GB", "TB", "PB"];

    public static string Bytes(long value) => Bytes((double)value);

    public static string Bytes(ulong value) => Bytes((double)value);

    public static string Bytes(double value)
    {
        var negative = value < 0;
        value = Math.Abs(value);

        var unit = 0;
        while (value >= 1024 && unit < Units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        // 1023.6 B would round to "1024 B"; from 1023.5 on it belongs to the next unit.
        if (value >= 1023.5 && unit < Units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        // One decimal below 10, none above.
        var text = unit == 0 ? $"{value:0} B"
                 : value < 10 ? $"{value:0.0} {Units[unit]}"
                 : $"{value:0} {Units[unit]}";

        return negative ? "-" + text : text;
    }

    /// <summary>Duration in the coarsest unit that still says something.</summary>
    public static string Duration(TimeSpan span) => span switch
    {
        { TotalDays: >= 1 } => $"{(int)span.TotalDays} d {span.Hours} h {span.Minutes} min",
        { TotalHours: >= 1 } => $"{(int)span.TotalHours} h {span.Minutes} min",
        { TotalMinutes: >= 1 } => $"{(int)span.TotalMinutes} min {span.Seconds} s",
        _ => $"{span.TotalSeconds:0} s",
    };

    /// <summary>Length of a video or recording: <c>1:04:12</c> or <c>4:12</c>.</summary>
    public static string Clock(TimeSpan span) => span.TotalHours >= 1
        ? $"{(int)span.TotalHours}:{span.Minutes:00}:{span.Seconds:00}"
        : $"{span.Minutes}:{span.Seconds:00}";

    public static string Percent(double fraction) => $"{fraction * 100:0}%";

    /// <summary>"3 days ago", for states whose age matters more than their date.</summary>
    public static string Ago(DateTimeOffset moment)
    {
        var span = DateTimeOffset.Now - moment;

        return span switch
        {
            { TotalSeconds: < 60 } => "just now",
            { TotalMinutes: < 60 } => $"{(int)span.TotalMinutes} min ago",
            { TotalHours: < 24 } => $"{(int)span.TotalHours} h ago",
            { TotalDays: < 2 } => "1 day ago",
            { TotalDays: < 30 } => $"{(int)span.TotalDays} days ago",
            _ => moment.ToString("d"),
        };
    }
}
