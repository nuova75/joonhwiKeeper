using System.Text.Json;
using XKeep.Agent.Shared;
using Xunit;

namespace XKeep.Agent.Tests;

public class LimitEvaluatorTests
{
    private static readonly DateTimeOffset Noon = new(2026, 11, 25, 12, 0, 0, TimeSpan.FromHours(9));
    private static readonly Policy Base = new() { Version = 1, DailyLimitMinutes = 60 };

    [Fact] public void UnderLimit_NotLocked() => Assert.Null(LimitEvaluator.Evaluate(Base, 59, Noon));

    [Fact] public void AtLimit_Locked() => Assert.Equal(LockReason.DailyLimit, LimitEvaluator.Evaluate(Base, 60, Noon));

    [Fact]
    public void ExtraTimeToday_ExtendsLimit()
    {
        var p = Base with { ExtraTime = new ExtraTime("2026-11-25", 30) };
        Assert.Null(LimitEvaluator.Evaluate(p, 80, Noon));
        Assert.Equal(LockReason.DailyLimit, LimitEvaluator.Evaluate(p, 90, Noon));
    }

    [Fact]
    public void ExtraTimeOtherDay_Ignored() =>
        Assert.Equal(LockReason.DailyLimit,
            LimitEvaluator.Evaluate(Base with { ExtraTime = new ExtraTime("2026-11-24", 30) }, 60, Noon));

    [Fact]
    public void Precedence_TamperManualBedtime()
    {
        var p = Base with { ManualLock = true, Bedtime = new Bedtime("11:00", "13:00") };
        Assert.Equal(LockReason.Tamper, LimitEvaluator.Evaluate(p, 0, Noon, tamper: true));
        Assert.Equal(LockReason.Manual, LimitEvaluator.Evaluate(p, 0, Noon));
        Assert.Equal(LockReason.Bedtime, LimitEvaluator.Evaluate(p with { ManualLock = false }, 0, Noon));
    }

    [Theory]
    [InlineData("23:30", true)]
    [InlineData("06:59", true)]
    [InlineData("07:00", false)]
    [InlineData("21:59", false)]
    public void BedtimeAcrossMidnight(string time, bool expected) =>
        Assert.Equal(expected, LimitEvaluator.InBedtime(new Bedtime("22:00", "07:00"), TimeOnly.Parse(time)));

    [Fact]
    public void SharedFixture_PolicyParses()
    {
        var json = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "policy.json"));
        var policy = ServerMessageParser.TryParsePolicy(json);
        Assert.NotNull(policy);
        Assert.Equal(7, policy!.Version);
        Assert.Contains("roblox.exe", policy.BlockedApps);
        Assert.Equal("22:00", policy.Bedtime!.Start);
    }

    [Fact]
    public void UsageMessage_SerializesWithTypeAndWireReason()
    {
        var json = new UsageMessage("2026-11-25", 61, new(), true, LockReason.DailyLimit, 20, "locked").ToJson();
        using var doc = JsonDocument.Parse(json);
        Assert.Equal("usage", doc.RootElement.GetProperty("type").GetString());
        Assert.Equal("daily_limit", doc.RootElement.GetProperty("lockReason").GetString());
        Assert.Equal(61, doc.RootElement.GetProperty("usedMinutes").GetInt32());
        Assert.Equal(20, doc.RootElement.GetProperty("studyMinutes").GetInt32());
        Assert.Equal("locked", doc.RootElement.GetProperty("mode").GetString());
    }

    // ---------- 학습 모드 (2026-11-25 는 수요일) ----------
    private static readonly DateTimeOffset Evening = new(2026, 11, 25, 19, 30, 0, TimeSpan.FromHours(9));
    private static readonly Policy WeekdayStudy = Base with
    {
        Study = new Study { Schedule = [new StudyBlock([1, 2, 3, 4, 5], "19:00", "21:00")] },
    };

    [Fact]
    public void Schedule_ActivatesStudy_AndSuspendsDailyLimit()
    {
        var d = LimitEvaluator.Decide(WeekdayStudy, 999, Evening);
        Assert.True(d.Study);
        Assert.Null(d.Lock);
        Assert.Equal("study", d.Mode);
        Assert.Equal("21:00", LimitEvaluator.StudyEndsAt(WeekdayStudy, Evening)!.Value.ToString("HH:mm"));
    }

    [Fact]
    public void Schedule_OtherDayOrTime_Free()
    {
        Assert.False(LimitEvaluator.IsStudyActive(WeekdayStudy, Evening.AddHours(1.5)));
        Assert.False(LimitEvaluator.IsStudyActive(WeekdayStudy, Evening.AddDays(3))); // 토요일
    }

    [Fact]
    public void BedtimeAndManual_BeatStudy()
    {
        Assert.Equal(LockReason.Bedtime,
            LimitEvaluator.Decide(WeekdayStudy with { Bedtime = new Bedtime("19:00", "07:00") }, 0, Evening).Lock);
        Assert.Equal(LockReason.Manual, LimitEvaluator.Decide(WeekdayStudy with { ManualLock = true }, 0, Evening).Lock);
    }

    [Fact]
    public void Session_ActiveUntilExpiry()
    {
        var p = Base with { StudySession = new StudySession("2026-11-25T04:00:00.000Z", "child") }; // 13:00 KST
        Assert.True(LimitEvaluator.IsStudyActive(p, Noon));
        Assert.False(LimitEvaluator.IsStudyActive(p, Noon.AddHours(1)));
    }

    [Fact]
    public void Grants_OnlyToday()
    {
        var p = Base with
        {
            Study = new Study { Apps = ["chrome.exe"], Sites = ["ebsi.co.kr"] },
            Grants = new Grants("2026-11-25", ["zoom.exe"], ["youtube.com"]),
        };
        Assert.Equal(new[] { "chrome.exe", "zoom.exe" }, LimitEvaluator.StudyApps(p, Noon));
        Assert.Equal(new[] { "ebsi.co.kr", "youtube.com" }, LimitEvaluator.StudySites(p, Noon));
        Assert.Equal(new[] { "chrome.exe" }, LimitEvaluator.StudyApps(p, Noon.AddDays(1)));
    }

    [Fact]
    public void SharedFixture_StudyFieldsParse()
    {
        var json = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "policy.json"));
        var policy = ServerMessageParser.TryParsePolicy(json)!;
        Assert.Contains("band.us", policy.Study.Sites);
        Assert.Equal(2, policy.Study.Schedule.Count);
        Assert.Equal("child", policy.StudySession!.By);
    }
}

public class StudyAppRulesTests
{
    private static readonly StudyAppRules Rules = new(@"C:\Windows", [@"C:\Users", @"C:\ProgramData", @"C:\Windows\Temp"]);
    private static readonly string[] Allowed = ["chrome.exe", @"C:\Users\kid\AppData\Roaming\Zoom\bin\Zoom.exe"];

    [Theory]
    [InlineData(@"C:\Program Files\Google\Chrome\Application\chrome.exe", true)]  // 이름 일치 + 보호된 폴더
    [InlineData(@"C:\Users\kid\Downloads\chrome.exe", false)]                     // 이름 위장 (사용자 폴더)
    [InlineData(@"C:\Users\kid\AppData\Roaming\Zoom\bin\Zoom.exe", true)]         // 전체 경로 허용
    [InlineData(@"C:\Users\kid\AppData\Local\Discord\app-1.0\Discord.exe", false)]
    [InlineData(@"C:\Windows\explorer.exe", true)]                                // 시스템
    [InlineData(@"C:\Windows\Temp\explorer.exe", false)]                          // Windows 아래지만 쓰기 가능
    [InlineData(@"C:\Program Files (x86)\Steam\steam.exe", false)]
    public void IsAllowed(string path, bool expected) => Assert.Equal(expected, Rules.IsAllowed(path, Allowed));

    [Fact]
    public void InventoryId_PathForUserFolders()
    {
        Assert.Equal("steam.exe", Rules.InventoryId(@"C:\Program Files (x86)\Steam\steam.exe"));
        Assert.Equal(@"C:\Users\kid\AppData\Local\Discord\Discord.exe", Rules.InventoryId(@"C:\Users\kid\AppData\Local\Discord\Discord.exe"));
    }
}
