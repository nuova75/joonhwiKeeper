using System.Text.Json;
using XKeep.Agent.Shared;

namespace XKeep.Agent.Service.State;

/// <summary>
/// 서버에서 받은 마지막 정책의 로컬 캐시. 서버가 꺼져 있어도 이 정책으로 계속 제한한다.
/// 변경 시 Changed 이벤트로 워커들이 즉시 재판정하도록 알린다.
/// </summary>
public sealed class PolicyStore
{
    private readonly ILogger<PolicyStore> _log;
    private readonly object _gate = new();
    private Policy? _current;

    public event Action<Policy>? Changed;

    public PolicyStore(ILogger<PolicyStore> log)
    {
        _log = log;
        try
        {
            if (File.Exists(AgentPaths.PolicyFile))
                _current = JsonSerializer.Deserialize<Policy>(File.ReadAllText(AgentPaths.PolicyFile), AgentJson.Options);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "정책 캐시 로드 실패");
        }
    }

    public Policy? Current
    {
        get { lock (_gate) return _current; }
    }

    public void Update(Policy policy)
    {
        lock (_gate)
        {
            if (_current == policy || (_current is not null && PolicyEquals(_current, policy))) return;
            _current = policy;
            AgentPaths.WriteAtomic(AgentPaths.PolicyFile, JsonSerializer.Serialize(policy, AgentJson.Options));
        }
        _log.LogInformation("정책 v{Version} 적용", policy.Version);
        Changed?.Invoke(policy);
    }

    // record 의 List 필드는 참조 비교라 JSON 으로 비교
    private static bool PolicyEquals(Policy a, Policy b) =>
        JsonSerializer.Serialize(a, AgentJson.Options) == JsonSerializer.Serialize(b, AgentJson.Options);
}
