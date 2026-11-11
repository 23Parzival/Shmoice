using System.Globalization;
using Shmoice.Models;

namespace Shmoice.Services;

/// <summary>Decides when schedules are due. Checked every few seconds by the main window.</summary>
public class Scheduler
{
    /// <summary>A time slot missed by more than this (e.g. the PC was asleep at 9:00) is skipped, not run late.</summary>
    public static readonly TimeSpan CatchUpWindow = TimeSpan.FromMinutes(10);

    /// <summary>"Every N minutes" counts from when Shmoice started, so starting the app doesn't fire everything at once.</summary>
    private readonly DateTime _started = DateTime.Now;

    public bool IsDue(Schedule s, DateTime now)
    {
        if (!s.Enabled || s.CommandId == null) return false;
        if (s.Kind == ScheduleKind.Every)
            return now >= EveryBase(s) + TimeSpan.FromMinutes(s.IntervalMinutes);

        if (!TryParseTime(s.Time, out var time) || !s.RunsOn(now.DayOfWeek)) return false;
        var slot = now.Date + time;
        return slot <= now && now - slot <= CatchUpWindow && (s.LastRun == null || s.LastRun < slot);
    }

    public DateTime? NextRun(Schedule s, DateTime now)
    {
        if (!s.Enabled || s.CommandId == null) return null;
        if (s.Kind == ScheduleKind.Every) return EveryBase(s) + TimeSpan.FromMinutes(s.IntervalMinutes);

        if (!TryParseTime(s.Time, out var time) || s.Days == 0) return null;
        for (var i = 0; i < 8; i++)
        {
            var slot = now.Date.AddDays(i) + time;
            if (s.RunsOn(slot.DayOfWeek) && slot > now) return slot;
        }
        return null;
    }

    /// <summary>"Next: today 9:00", "Next: Mon 9:00", or why it won't run.</summary>
    public string Describe(Schedule s, IEnumerable<VoiceCommand> commands, DateTime now)
    {
        if (s.CommandId == null || commands.All(c => c.Id != s.CommandId)) return "Pick a command";
        if (s.Kind == ScheduleKind.AtTime && !TryParseTime(s.Time, out _)) return "Enter a time like 09:00";
        if (s.Kind == ScheduleKind.AtTime && s.Days == 0) return "Pick at least one day";
        if (!s.Enabled) return "Off";
        if (NextRun(s, now) is not { } next) return "";
        var day = next.Date == now.Date ? "today" : next.Date == now.Date.AddDays(1) ? "tomorrow" : next.ToString("ddd", CultureInfo.CurrentCulture);
        return $"Next: {day} {next:HH:mm}";
    }

    private DateTime EveryBase(Schedule s) => s.LastRun is { } last && last > _started ? last : _started;

    /// <summary>Accepts "9:00", "09:00", "21:30", "9".</summary>
    public static bool TryParseTime(string text, out TimeSpan time)
    {
        time = default;
        var t = text.Trim();
        if (int.TryParse(t, out var hour) && hour is >= 0 and < 24) { time = TimeSpan.FromHours(hour); return true; }
        return TimeSpan.TryParseExact(t, ["h\\:mm", "hh\\:mm"], CultureInfo.InvariantCulture, out time) && time < TimeSpan.FromDays(1);
    }
}
