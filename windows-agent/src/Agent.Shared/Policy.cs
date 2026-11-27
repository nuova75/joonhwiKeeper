using System.Text.Json;
using System.Text.Json.Serialization;

namespace XKeep.Agent.Shared;

/// <summary>shared/protocol/PROTOCOL.md 의 Policy 와 필드명 1:1 대응 (JSON camelCase)</summary>
public sealed record Policy
{
    public int Version { get; init; }
    public string Timezone { get; init; } = "Asia/Seoul";
    public int? DailyLimitMinutes { get; init; }
    public Bedtime? Bedtime { get; init; }
    public List<string> BlockedApps { get; init; } = [];
    public List<string> AlwaysAllowed { get; init; } = [];
    public bool ManualLock { get; init; }
    public ExtraTime? ExtraTime { get; init; }
    public Study Study { get; init; } = new();
    public StudySession? StudySession { get; init; }
    public Grants? Grants { get; init; }
}

/// <summary>학습 모드: 이 시간엔 Apps/Sites 에 있는 것만 사용 가능</summary>
public sealed record Study
{
    /// <summary>exe 이름(chrome.exe) 또는 전체 경로. 이름만 쓰면 사용자 쓰기 가능 폴더 밖의 실행 파일에만 일치</summary>
    public List<string> Apps { get; init; } = [];
    /// <summary>도메인 (하위 도메인 포함, Chrome URL 필터 형식)</summary>
    public List<string> Sites { get; init; } = [];
    public List<StudyBlock> Schedule { get; init; } = [];
}

/// <summary>Days: ISO 요일 1=월 … 7=일, Start &lt; End</summary>
public sealed record StudyBlock(List<int> Days, string Start, string End);

/// <summary>Until: ISO-8601(오프셋 포함), By: "parent" | "child"</summary>
public sealed record StudySession(string Until, string By);

/// <summary>요청 승인으로 오늘만 허용된 앱·사이트</summary>
public sealed record Grants(string Date, List<string> Apps, List<string> Sites);

public sealed record Bedtime(string Start, string End);

public sealed record ExtraTime(string Date, int Minutes);

[JsonConverter(typeof(LockReasonConverter))]
public enum LockReason { Tamper, Manual, Bedtime, DailyLimit }

public static class LockReasonExtensions
{
    public static string ToWire(this LockReason r) => r switch
    {
        LockReason.Tamper => "tamper",
        LockReason.Manual => "manual",
        LockReason.Bedtime => "bedtime",
        LockReason.DailyLimit => "daily_limit",
        _ => throw new ArgumentOutOfRangeException(nameof(r)),
    };

    public static string ToKorean(this LockReason r) => r switch
    {
        LockReason.Tamper => "보호 설정이 변경되어 잠겼어요. 부모님께 알려주세요.",
        LockReason.Manual => "부모님이 잠갔어요.",
        LockReason.Bedtime => "잘 시간이에요.",
        LockReason.DailyLimit => "오늘 사용 시간을 다 썼어요.",
        _ => "",
    };
}

internal sealed class LockReasonConverter : JsonConverter<LockReason>
{
    public override LockReason Read(ref Utf8JsonReader reader, Type t, JsonSerializerOptions o) => reader.GetString() switch
    {
        "tamper" => LockReason.Tamper,
        "manual" => LockReason.Manual,
        "bedtime" => LockReason.Bedtime,
        "daily_limit" => LockReason.DailyLimit,
        var s => throw new JsonException($"unknown lock reason {s}"),
    };

    public override void Write(Utf8JsonWriter writer, LockReason value, JsonSerializerOptions o) =>
        writer.WriteStringValue(value.ToWire());
}

public static class AgentJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        WriteIndented = false,
    };
}
