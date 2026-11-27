namespace XKeep.Agent.Service.State;

/// <summary>
/// SessionControlWorker 가 판정한 현재 모드를 ProcessMonitorWorker 와 공유한다.
/// StudyAllowed 가 null 이 아니면 학습 모드: 그 목록에 없는 사용자 프로그램은 종료 대상.
/// </summary>
public sealed class EnforcementState
{
    private volatile Snapshot _current = new(null, null);

    public sealed record Snapshot(IReadOnlyList<string>? StudyAllowed, string? StudyUntil);

    public Snapshot Current => _current;

    public event Action<Snapshot>? Changed;

    public void Set(Snapshot next)
    {
        var prev = _current;
        _current = next;
        var same = prev.StudyUntil == next.StudyUntil &&
                   (prev.StudyAllowed ?? []).SequenceEqual(next.StudyAllowed ?? [], StringComparer.OrdinalIgnoreCase) &&
                   (prev.StudyAllowed is null) == (next.StudyAllowed is null);
        if (!same) Changed?.Invoke(next);
    }
}
