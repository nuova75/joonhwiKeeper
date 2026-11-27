namespace XKeep.Agent.Shared;

/// <summary>판정 결과: 잠금이 우선, 아니면 학습 모드 여부</summary>
public sealed record Decision(LockReason? Lock, bool Study)
{
    public string Mode => Lock is not null ? "locked" : Study ? "study" : "free";
}

/// <summary>
/// 잠금·학습 판정 (순수 함수). 판정 순서는 세 플랫폼 공통: tamper → manual → bedtime → study → daily_limit.
///  - 학습 모드 중에는 하루 한도로 잠그지 않는다 (공부는 자유 시간을 쓰지 않음)
///  - freeMinutes = 오늘 전체 사용 - 학습 모드 사용
/// Android 의 com.xkeep.agent.policy.LimitEvaluator 와 동일한 규칙을 유지할 것.
/// </summary>
public static class LimitEvaluator
{
    public static Decision Decide(Policy policy, int freeMinutes, DateTimeOffset localNow, bool tamper = false)
    {
        if (tamper) return new(LockReason.Tamper, false);
        if (policy.ManualLock) return new(LockReason.Manual, false);
        if (policy.Bedtime is { } b && InBedtime(b, TimeOnly.FromDateTime(localNow.DateTime))) return new(LockReason.Bedtime, false);
        if (IsStudyActive(policy, localNow)) return new(null, true);
        var limit = EffectiveLimit(policy, localNow);
        if (limit is null) return new(null, false);
        return new(freeMinutes >= limit ? LockReason.DailyLimit : null, false);
    }

    public static LockReason? Evaluate(Policy policy, int usedMinutes, DateTimeOffset localNow, bool tamper = false) =>
        Decide(policy, usedMinutes, localNow, tamper).Lock;

    public static bool IsStudyActive(Policy policy, DateTimeOffset localNow) => StudyEndsAt(policy, localNow) is not null;

    /// <summary>현재 학습 모드가 끝나는 시각(정책 시간대). 학습 모드가 아니면 null. 세션과 시간표가 겹치면 늦은 쪽</summary>
    public static DateTimeOffset? StudyEndsAt(Policy policy, DateTimeOffset localNow)
    {
        DateTimeOffset? session = null;
        if (policy.StudySession is { } s && DateTimeOffset.TryParse(s.Until, out var until) && until > localNow)
            session = until.ToOffset(localNow.Offset);

        var t = TimeOnly.FromDateTime(localNow.DateTime);
        var day = localNow.DayOfWeek == DayOfWeek.Sunday ? 7 : (int)localNow.DayOfWeek; // ISO 1=월
        DateTimeOffset? block = null;
        foreach (var b in policy.Study.Schedule)
        {
            if (!b.Days.Contains(day)) continue;
            var start = TimeOnly.Parse(b.Start);
            var end = TimeOnly.Parse(b.End);
            if (t < start || t >= end) continue;
            var endAt = new DateTimeOffset(localNow.Date + end.ToTimeSpan(), localNow.Offset);
            if (block is null || endAt > block) block = endAt;
        }
        if (session is null) return block;
        if (block is null) return session;
        return session > block ? session : block;
    }

    /// <summary>학습 모드 허용 앱 = 학습 앱 + 오늘 승인된 앱</summary>
    public static IReadOnlyList<string> StudyApps(Policy policy, DateTimeOffset localNow) =>
        policy.Study.Apps.Concat(TodayGrants(policy, localNow)?.Apps ?? []).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    public static IReadOnlyList<string> StudySites(Policy policy, DateTimeOffset localNow) =>
        policy.Study.Sites.Concat(TodayGrants(policy, localNow)?.Sites ?? []).Distinct().ToList();

    private static Grants? TodayGrants(Policy policy, DateTimeOffset localNow) =>
        policy.Grants is { } g && g.Date == DateKey(localNow) ? g : null;

    /// <summary>오늘 한도 = 기본 한도 + 오늘 날짜의 추가 시간. null = 무제한</summary>
    public static int? EffectiveLimit(Policy policy, DateTimeOffset localNow)
    {
        if (policy.DailyLimitMinutes is not { } baseLimit) return null;
        var today = DateOnly.FromDateTime(localNow.DateTime).ToString("yyyy-MM-dd");
        var extra = policy.ExtraTime is { } e && e.Date == today ? e.Minutes : 0;
        return baseLimit + extra;
    }

    public static int? RemainingMinutes(Policy policy, int usedMinutes, DateTimeOffset localNow) =>
        EffectiveLimit(policy, localNow) is { } l ? Math.Max(0, l - usedMinutes) : null;

    /// <summary>자정을 넘기는 구간(22:00~07:00) 처리. start == end 는 "없음"</summary>
    public static bool InBedtime(Bedtime bedtime, TimeOnly t)
    {
        var start = TimeOnly.Parse(bedtime.Start);
        var end = TimeOnly.Parse(bedtime.End);
        if (start == end) return false;
        return start < end ? t >= start && t < end : t >= start || t < end;
    }

    /// <summary>정책 시간대 기준 현재 시각. .NET 8 은 Windows 에서도 IANA ID(Asia/Seoul)를 지원</summary>
    public static DateTimeOffset LocalNow(Policy policy, DateTimeOffset? utcNow = null)
    {
        var now = utcNow ?? DateTimeOffset.UtcNow;
        try
        {
            return TimeZoneInfo.ConvertTime(now, TimeZoneInfo.FindSystemTimeZoneById(policy.Timezone));
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return now.ToLocalTime();
        }
    }

    public static string DateKey(DateTimeOffset localNow) => localNow.ToString("yyyy-MM-dd");
}
