using System.Text.Json;

namespace XKeep.Agent.Shared;

/// <summary>
/// 서비스(Session 0, LocalSystem) ↔ UserHost(사용자 세션 트레이 앱) 간 Named Pipe 메시지.
/// 한 줄에 JSON 하나 (newline-delimited). 양방향:
///  - 서비스 → UserHost: UserHostCommand (상태·알림)
///  - UserHost → 서비스: UserHostReport (화면에 떠 있는 창 목록, 아이의 요청, 공부 시작)
/// UserHost 가 보낸 내용은 신뢰하지 않는다: 창 목록의 pid 는 서비스가 직접 경로·세션을 다시 확인하고,
/// 요청·공부 시작은 부모 승인이 필요하거나 더 엄격해지는 방향이라 위조해도 우회가 되지 않는다.
/// </summary>
public static class Ipc
{
    public const string PipeName = "xkeep-userhost";
}

public sealed record UserHostCommand
{
    /// <summary>"status" | "notify"</summary>
    public required string Type { get; init; }
    public bool Locked { get; init; }
    public string? Reason { get; init; }
    public int UsedMinutes { get; init; }
    public int? RemainingMinutes { get; init; }
    public int StudyMinutes { get; init; }
    /// <summary>학습 모드면 끝나는 시각 "HH:mm"</summary>
    public string? StudyUntil { get; init; }
    /// <summary>내 규칙 요약 (트레이 메뉴 "내 규칙 보기")</summary>
    public string? Rules { get; init; }
    public string? Title { get; init; }
    public string? Text { get; init; }
    /// <summary>notify 에서 "요청하기"로 이어질 대상 (막힌 프로그램 이름)</summary>
    public string? RequestApp { get; init; }

    public string ToJsonLine() => JsonSerializer.Serialize(this, AgentJson.Options);

    public static UserHostCommand? Parse(string line) =>
        JsonSerializer.Deserialize<UserHostCommand>(line, AgentJson.Options);
}

/// <summary>UserHost → 서비스</summary>
public sealed record UserHostReport
{
    /// <summary>"windows" | "request" | "study_start"</summary>
    public required string Type { get; init; }
    /// <summary>windows: 화면에 보이는 최상위 창을 가진 프로세스 id</summary>
    public List<int>? Pids { get; init; }
    /// <summary>request: "site" | "app"</summary>
    public string? Kind { get; init; }
    public string? Value { get; init; }
    public string? Note { get; init; }
    /// <summary>study_start</summary>
    public int Minutes { get; init; }

    public string ToJsonLine() => JsonSerializer.Serialize(this, AgentJson.Options);

    public static UserHostReport? Parse(string line) =>
        JsonSerializer.Deserialize<UserHostReport>(line, AgentJson.Options);
}
