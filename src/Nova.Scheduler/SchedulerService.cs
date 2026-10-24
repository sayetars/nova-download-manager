using System.Globalization;
using Nova.Domain;
using Nova.Engine;
using Nova.Persistence;

namespace Nova.Scheduler;

/// <summary>
/// Admission control loop. THE ONLY component that starts queued tasks:
/// global concurrency, per-queue limits, time windows, battery policy,
/// priority aging (anti-starvation), queue completion actions.
/// </summary>
public sealed class SchedulerService : IDisposable
{
    private readonly DownloadEngine _engine;
    private readonly ISettingsRepository _settings;
    private readonly IQueueRepository _queues;
    private readonly System.Timers.Timer _tick;
    private readonly Dictionary<string, long> _queuedSince = new();
    private string _lastState = "idle";

    public event Action<string>? SchedulerStateChanged;
    public event Action<string>? QueueCompleted; // queue name

    public SchedulerService(DownloadEngine engine, ISettingsRepository settings, IQueueRepository queues)
    {
        _engine = engine;
        _settings = settings;
        _queues = queues;
        _tick = new System.Timers.Timer(500) { AutoReset = true };
        _tick.Elapsed += (_, _) => Tick();
        _tick.Start();
    }

    public string State => _lastState;

    private int MaxConcurrent => GetInt("scheduler.max_concurrent", 3);
    private bool StartOnBattery => GetBool("scheduler.start_on_battery", true);

    private void Tick()
    {
        try
        {
            var active = _engine.ActiveCount;
            var queued = _engine.GetQueued().ToList();
            var window = InAllowedWindow();
            var batteryOk = StartOnBattery || BatteryStatus.OnAcPower;

            foreach (var q in queued)
                if (!_queuedSince.ContainsKey(q.TaskKey)) _queuedSince[q.TaskKey] = Environment.TickCount64;

            if (!window || !batteryOk)
            {
                SetState(!window ? "paused_outside_window" : "paused_on_battery");
                return;
            }

            var perQueue = _queues.GetAll().ToDictionary(q => q.Name, q => q);

            // IDM parity — per-server connection caps: host → running-task counts.
            var activePerHost = new Dictionary<string, int>(_engine.ActivePerHost(), StringComparer.OrdinalIgnoreCase);

            while (active < MaxConcurrent)
            {
                var next = PickNext(queued, perQueue, active);
                if (next is null) break;
                var cap = perQueue.GetValueOrDefault(next.QueueName)?.MaxConcurrent ?? 0;
                var runningInQueue = _engine.All().Count(t => t.QueueName == next.QueueName && t.State == DownloadState.Downloading);
                if (cap > 0 && runningInQueue >= cap) { queued.Remove(next); continue; }

                // IDM parity — per-server caps + per-queue start/stop schedules.
                var host = PerServerPolicy.HostOf(next.Url);
                if (!PerServerPolicy.MayStart(host, activePerHost, _settings)) { queued.Remove(next); continue; }
                var queueInfo = perQueue.GetValueOrDefault(next.QueueName);
                if (queueInfo is not null && !QueueSchedule.MayRun(queueInfo.ScheduleJson, DateTimeOffset.Now))
                { queued.Remove(next); continue; }

                if (!_engine.TryStartTask(next.TaskKey)) break;
                if (host.Length > 0)
                    activePerHost[host] = activePerHost.GetValueOrDefault(host, 0) + 1;
                _queuedSince.Remove(next.TaskKey);
                active++;
            }

            SetState(active > 0 ? "running" : "idle");

            DetectQueueCompletion();
        }
        catch (Exception ex)
        {
            EngineLog.Warn($"scheduler tick failed: {ex.Message}");
        }
    }

    /// <summary>Priority + starvation aging: every 5 minutes of wait adds one priority notch.</summary>
    private DownloadTask? PickNext(IReadOnlyList<DownloadTask> queued,
        Dictionary<string, DownloadQueueInfo> queues, int activeNow)
    {
        var now = Environment.TickCount64;
        return queued
            .OrderByDescending(t => (int)t.Priority + (int)Math.Min((now - (_queuedSince.GetValueOrDefault(t.TaskKey, now))) / 300_000, 2))
            .ThenBy(t => t.Id)
            .FirstOrDefault();
    }

    /// <summary>Time-window rules (scheduler_rule rows via settings "scheduler.rules" JSON in EngineHost).</summary>
    public bool InAllowedWindow()
    {
        var raw = _settings.Get("scheduler.rules");
        if (string.IsNullOrEmpty(raw)) return true;
        try
        {
            var rules = System.Text.Json.JsonSerializer.Deserialize<List<RuleDto>>(raw) ?? new();
            var anyEnabled = rules.Any(r => r.Enabled);
            if (!anyEnabled) return true;
            return rules.Any(r => RulesProbe.IsRuleActiveAt(r, DateTimeOffset.Now));
        }
        catch { return true; }
    }

    /// <summary>Pure, testable evaluation of a rule at a given time.</summary>
    public static class RulesProbe
    {
        public static bool IsRuleActiveAt(RuleDto r, DateTimeOffset at)
        {
            var dow = ((int)at.DayOfWeek).ToString(CultureInfo.InvariantCulture);
            if (!string.IsNullOrEmpty(r.DaysOfWeek) && !r.DaysOfWeek.Split(',').Contains(dow)) return false;

            if (!TimeSpan.TryParseExact(r.StartTime, @"hh\:mm", CultureInfo.InvariantCulture, out var start) ||
                !TimeSpan.TryParseExact(r.EndTime, @"hh\:mm", CultureInfo.InvariantCulture, out var end))
                return false;

            var t = at.TimeOfDay;
            if (start <= end) return t >= start && t <= end;
            return t >= start || t <= end; // overnight window (e.g. 22:00 → 06:00)
        }
    }

    private void DetectQueueCompletion()
    {
        var tasks = _engine.All();
        foreach (var g in tasks.GroupBy(t => t.QueueName))
        {
            var hasWork = g.Any(t => t.State is DownloadState.Queued or DownloadState.Downloading
                or DownloadState.Analyzing or DownloadState.Verifying
                or DownloadState.Pausing or DownloadState.Retrying or DownloadState.Interrupted);
            if (!hasWork && g.Any(t => t.State == DownloadState.Completed))
                QueueCompleted?.Invoke(g.Key);
        }
    }

    private void SetState(string s)
    {
        if (_lastState == s) return;
        _lastState = s;
        SchedulerStateChanged?.Invoke(s);
    }

    private int GetInt(string key, int dflt) =>
        int.TryParse(_settings.Get(key), out var v) && v > 0 ? v : dflt;

    private bool GetBool(string key, bool dflt) =>
        _settings.Get(key) is { } s ? s == "true" : dflt;

    public void Dispose()
    {
        _tick.Stop();
        _tick.Dispose();
    }

    public sealed class RuleDto
    {
        public long Id { get; set; }
        public string Name { get; set; } = "";
        public string DaysOfWeek { get; set; } = "";
        public string StartTime { get; set; } = "00:00";
        public string EndTime { get; set; } = "23:59";
        public bool Enabled { get; set; } = true;
        public long SpeedCapBytes { get; set; }
    }
}

/// <summary>Battery status abstraction; the Windows implementation lives in Nova.Windows.</summary>
public interface IBatteryStatus
{
    bool OnAcPower { get; }
}

public static class BatteryStatus
{
    public static IBatteryStatus Implementation { get; set; } = new AcPowerDefault();
    public static bool OnAcPower => Implementation.OnAcPower;

    private sealed class AcPowerDefault : IBatteryStatus
    {
        public bool OnAcPower => true;
    }
}
