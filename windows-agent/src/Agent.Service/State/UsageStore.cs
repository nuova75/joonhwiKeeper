using System.Text.Json;
using XKeep.Agent.Shared;

namespace XKeep.Agent.Service.State;

/// <summary>
/// 날짜별 누적 사용 시간(분)과 그중 학습 모드 시간. 재부팅해도 유지되도록 디스크에 저장한다.
/// 표준 사용자는 시스템 시간을 바꿀 수 없어(SeSystemtimePrivilege 없음) 날짜 조작으로 초기화할 수 없다.
/// </summary>
public sealed class UsageStore
{
    private sealed record UsageFile(Dictionary<string, int> Days, Dictionary<string, int>? StudyDays);

    private readonly object _gate = new();
    private readonly Dictionary<string, int> _days;
    private readonly Dictionary<string, int> _study;

    public UsageStore(ILogger<UsageStore> log)
    {
        UsageFile? file = null;
        try
        {
            if (File.Exists(AgentPaths.UsageFile))
                file = JsonSerializer.Deserialize<UsageFile>(File.ReadAllText(AgentPaths.UsageFile), AgentJson.Options);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "사용량 파일 로드 실패 → 새로 시작");
        }
        _days = file?.Days ?? new();
        _study = file?.StudyDays ?? new();
    }

    public int GetStudy(string date)
    {
        lock (_gate) return _study.GetValueOrDefault(date);
    }

    public int Get(string date)
    {
        lock (_gate) return _days.GetValueOrDefault(date);
    }

    /// <param name="study">학습 모드 중 사용이면 true (전체에도 더하고 학습에도 더함)</param>
    public int AddMinutes(string date, int minutes, bool study = false)
    {
        lock (_gate)
        {
            var total = Math.Min(1440, _days.GetValueOrDefault(date) + minutes);
            _days[date] = total;
            if (study) _study[date] = Math.Min(total, _study.GetValueOrDefault(date) + minutes);
            // 최근 14일만 보관
            foreach (var old in _days.Keys.Order().SkipLast(14).ToList()) _days.Remove(old);
            foreach (var old in _study.Keys.Where(k => !_days.ContainsKey(k)).ToList()) _study.Remove(old);
            AgentPaths.WriteAtomic(AgentPaths.UsageFile, JsonSerializer.Serialize(new UsageFile(_days, _study), AgentJson.Options));
            return total;
        }
    }
}
