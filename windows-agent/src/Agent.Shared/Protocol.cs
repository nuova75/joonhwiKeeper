using System.Text.Json;
using System.Text.Json.Serialization;

namespace XKeep.Agent.Shared;

// ---------- 에이전트 → 서버 (PROTOCOL.md) ----------
// 각 메시지는 "type" 필드를 첫 속성으로 직렬화한다.

public abstract record AgentMessage
{
    [JsonPropertyOrder(-1)]
    public abstract string Type { get; }

    public string ToJson() => JsonSerializer.Serialize(this, GetType(), AgentJson.Options);
}

public sealed record HelloMessage(string AgentVersion, int PolicyVersion) : AgentMessage
{
    public override string Type => "hello";
}

public sealed record UsageMessage(
    string Date, int UsedMinutes, Dictionary<string, int> PerApp, bool Locked, LockReason? LockReason,
    int StudyMinutes, string Mode) : AgentMessage
{
    public override string Type => "usage";
}

public sealed record AppInfo(string Id, string Label);

/// <summary>최근 실행된 프로그램 목록 (부모앱에서 학습 허용 프로그램을 고르는 데 사용)</summary>
public sealed record AppsMessage(IReadOnlyList<AppInfo> Apps) : AgentMessage
{
    public override string Type => "apps";
}

/// <summary>아이의 "학습에 필요해요" 요청. Kind: "site" | "app"</summary>
public sealed record AccessRequestMessage(string Kind, string Value, string Note) : AgentMessage
{
    public override string Type => "access_request";
}

/// <summary>아이가 직접 공부 시작 (10~240분)</summary>
public sealed record StudyStartMessage(int Minutes) : AgentMessage
{
    public override string Type => "study_start";
}

public sealed record ProcessesMessage(IReadOnlyList<string> Names) : AgentMessage
{
    public override string Type => "processes";
}

public sealed record EventMessage(string Kind, string Detail, string At) : AgentMessage
{
    public override string Type => "event";

    public static EventMessage Now(string kind, string detail) =>
        new(kind, detail, DateTimeOffset.Now.ToString("yyyy-MM-ddTHH:mm:sszzz"));
}

public sealed record GoodbyeMessage(string Reason) : AgentMessage
{
    public override string Type => "goodbye";
}

// ---------- 서버 → 에이전트 ----------
/// <summary>요청 처리 결과. Decision: "today" | "always" | "deny" | "invalid"</summary>
public sealed record RequestResult(int? RequestId, string Kind, string Value, string Decision);

public static class ServerMessageParser
{
    /// <summary>"type" 을 먼저 읽고 분기 (속성 순서에 의존하지 않음)</summary>
    public static Policy? TryParsePolicy(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("type", out var type) || type.GetString() != "policy") return null;
        return doc.RootElement.GetProperty("policy").Deserialize<Policy>(AgentJson.Options);
    }

    public static RequestResult? TryParseRequestResult(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("type", out var type) || type.GetString() != "request_result") return null;
        return doc.RootElement.Deserialize<RequestResult>(AgentJson.Options);
    }
}

// ---------- REST ----------
public sealed record PairRequest(string Code, string Platform, string AgentVersion);

public sealed record PairResponse(string DeviceId, string Token, Policy Policy);
