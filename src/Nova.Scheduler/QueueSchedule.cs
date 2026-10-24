using System.Globalization;
using System.Text.Json;
using Nova.Domain;

namespace Nova.Scheduler;

/// <summary>
/// IDM parity — per-queue start/stop scheduling. A queue's <c>ScheduleJson</c> holds
/// QueueScheduleRule entries: "run" windows (queue may ONLY run inside them) and
/// "pause" windows (queue must NOT run inside them). Empty/invalid JSON = always allowed.
/// Evaluation order: any matching "pause" window denies; else if any "run" rules exist,
/// at least one must match; else allowed.
/// </summary>
public static class QueueSchedule
{
    public static List<QueueScheduleRule> Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new();
        try { return JsonSerializer.Deserialize<List<QueueScheduleRule>>(json) ?? new(); }
        catch { return new(); }
    }

    public static string Serialize(List<QueueScheduleRule> rules) => JsonSerializer.Serialize(rules);

    /// <summary>Pure, testable: may a queue run at this moment?</summary>
    public static bool MayRun(IReadOnlyList<QueueScheduleRule> rules, DateTimeOffset at)
    {
        var activePause = false;
        var hasRunRules = false;
        var activeRun = false;
        foreach (var r in rules)
        {
            if (!r.Enabled) continue;
            var inside = IsInside(r, at);
            if (r.Action == "pause")
            {
                if (inside) activePause = true;
            }
            else
            {
                hasRunRules = true;
                if (inside) activeRun = true;
            }
        }
        if (activePause) return false;
        if (hasRunRules) return activeRun;
        return true;
    }

    public static bool MayRun(string? json, DateTimeOffset at) => MayRun(Parse(json), at);

    /// <summary>Day-of-week + time-window test; supports overnight windows (22:00 → 06:00).</summary>
    private static bool IsInside(QueueScheduleRule r, DateTimeOffset at)
    {
        var dow = ((int)at.DayOfWeek).ToString(CultureInfo.InvariantCulture);
        if (!string.IsNullOrEmpty(r.DaysOfWeek) && !r.DaysOfWeek.Split(',').Select(x => x.Trim()).Contains(dow))
            return false;

        if (!TimeSpan.TryParseExact(r.StartTime, @"hh\:mm", CultureInfo.InvariantCulture, out var start) ||
            !TimeSpan.TryParseExact(r.EndTime, @"hh\:mm", CultureInfo.InvariantCulture, out var end))
            return false;

        var t = at.TimeOfDay;
        if (start <= end) return t >= start && t <= end;
        return t >= start || t <= end; // overnight
    }
}
