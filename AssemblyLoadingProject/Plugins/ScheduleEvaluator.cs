using Cronos;

namespace AssemblyLoadingProject.Plugins;

/// <summary>
/// 调度评估器：根据 <see cref="ScheduleConfig"/> 计算某插件下一次应执行的时间。
///
/// 支持四种调度模式：
///  - FixedInterval：从"基准时间"起按固定间隔推演下一次；
///  - Cron：解析 Cron 表达式，取下一个匹配时刻（精确到秒）；
///  - Times：每天在给定的若干精确时刻各执行一次；
///  - IntervalWithinWindow：仅在指定时间窗口内按固定间隔执行，窗口外不触发。
///
/// 说明：
///  - 计算基于 UTC，前端展示时会转换为本地时间。
///  - 对"Times"与"IntervalWithinWindow"模式，需要基于"当天"推演，
///    若所有候选时间都已过去，则顺延到次日。
/// </summary>
public static class ScheduleEvaluator
{
    /// <summary>计算从 <paramref name="from"/> 起的下一次执行时间；无可用时间则返回 null。</summary>
    public static DateTimeOffset? GetNextRunAt(ScheduleConfig schedule, DateTimeOffset from)
    {
        switch (schedule.Mode)
        {
            case ScheduleMode.FixedInterval:
                return from.AddSeconds(Math.Max(schedule.IntervalSeconds, 1));

            case ScheduleMode.Cron:
                return GetNextFromCron(schedule.Cron, from);

            case ScheduleMode.Times:
                return GetNextFromTimes(schedule.Times, from);

            case ScheduleMode.IntervalWithinWindow:
                return GetNextFromWindow(schedule, from);

            default:
                return null;
        }
    }

    /// <summary>校验调度配置是否有效（供前端在保存前给出提示）。</summary>
    public static (bool Ok, string? Message) Validate(ScheduleConfig schedule)
    {
        switch (schedule.Mode)
        {
            case ScheduleMode.Cron:
                if (string.IsNullOrWhiteSpace(schedule.Cron))
                    return (false, "Cron 模式必须填写表达式");
                try
                {
                    _ = CronExpression.Parse(schedule.Cron, CronFormat.IncludeSeconds);
                }
                catch (Exception)
                {
                    return (false, $"Cron 表达式无效：{schedule.Cron}");
                }
                return (true, null);

            case ScheduleMode.Times:
                if (schedule.Times == null || schedule.Times.Count == 0)
                    return (false, "精确时间模式至少需要一个时间点");
                foreach (var t in schedule.Times)
                {
                    if (!TryParseClock(t, out _))
                        return (false, $"时间格式无效：{t}（应为 HH:mm 或 HH:mm:ss）");
                }
                return (true, null);

            case ScheduleMode.IntervalWithinWindow:
                if (!TryParseClock(schedule.WindowStart, out _) || !TryParseClock(schedule.WindowEnd, out _))
                    return (false, "时间窗口起止格式无效（应为 HH:mm）");
                if (schedule.IntervalSeconds <= 0)
                    return (false, "时间段内固定间隔必须大于 0 秒");
                return (true, null);

            default:
                if (schedule.IntervalSeconds <= 0)
                    return (false, "固定间隔必须大于 0 秒");
                return (true, null);
        }
    }

    // ---------- 各模式的下一次时间推演 ----------

    private static DateTimeOffset? GetNextFromCron(string? cron, DateTimeOffset from)
    {
        if (string.IsNullOrWhiteSpace(cron))
            return null;

        try
        {
            var expr = CronExpression.Parse(cron, CronFormat.IncludeSeconds);
            return expr.GetNextOccurrence(from.UtcDateTime, TimeZoneInfo.Utc);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static DateTimeOffset? GetNextFromTimes(List<string> times, DateTimeOffset from)
    {
        var today = from.Date;
        // 只保留能正确解析的时间，并映射为"今天的时刻"
        var candidates = new List<DateTimeOffset>();
        foreach (var t in times)
        {
            if (TryParseClock(t, out var offset))
                candidates.Add(today.Add(offset));
        }
        candidates.Sort();

        // 当天未过去的第一个时刻；否则取次日第一个
        var next = candidates.FirstOrDefault(dt => dt > from);
        if (next != default)
            return next;

        var tomorrowFirst = candidates.FirstOrDefault();
        return tomorrowFirst != default
            ? tomorrowFirst.AddDays(1)
            : null;
    }

    private static DateTimeOffset? GetNextFromWindow(ScheduleConfig schedule, DateTimeOffset from)
    {
        if (!TryParseClock(schedule.WindowStart, out var start) ||
            !TryParseClock(schedule.WindowEnd, out var end))
            return null;

        var today = from.Date;
        var windowStart = today.Add(start);
        var windowEnd = today.Add(end);
        var interval = TimeSpan.FromSeconds(Math.Max(schedule.IntervalSeconds, 1));

        // 当前不在窗口内：若已过结束时间，顺延到次日窗口起点；否则为窗口起点
        if (from < windowStart || from >= windowEnd)
        {
            return from < windowStart ? windowStart : windowStart.AddDays(1);
        }

        // 在窗口内：从窗口起点起按间隔推演，取第一个未过去且仍在窗口内的时刻
        var cursor = windowStart;
        while (cursor <= from)
        {
            cursor = cursor.Add(interval);
        }
        return cursor < windowEnd ? cursor : null; // 窗口内无更多时刻，本日结束
    }

    // ---------- 工具 ----------

    /// <summary>尝试把 "HH:mm" 或 "HH:mm:ss" 解析为当天的时间偏移。</summary>
    private static bool TryParseClock(string? s, out TimeSpan offset)
    {
        offset = default;
        if (string.IsNullOrWhiteSpace(s))
            return false;
        var parts = s.Trim().Split(':');
        if (parts.Length is < 2 or > 3)
            return false;
        if (!int.TryParse(parts[0], out var h) || h is < 0 or > 23) return false;
        if (!int.TryParse(parts[1], out var m) || m is < 0 or > 59) return false;
        int sec = 0;
        if (parts.Length == 3 && (!int.TryParse(parts[2], out sec) || sec is < 0 or > 59))
            return false;
        offset = new TimeSpan(h, m, sec);
        return true;
    }
}
