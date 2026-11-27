using Microsoft.Extensions.Options;
using XKeep.Agent.Service.Enforcement;
using XKeep.Agent.Service.Ipc;
using XKeep.Agent.Service.Native;
using XKeep.Agent.Service.Net;
using XKeep.Agent.Service.State;
using XKeep.Agent.Shared;

namespace XKeep.Agent.Service.Workers;

/// <summary>
/// 일일 사용 시간 추적 + 학습 모드 + 시간 초과/취침/원격 잠금 집행.
///
/// 매 틱(기본 60초)마다:
///  1. 콘솔 세션에 로그인한 사용자가 있고 잠금 해제 상태면 사용 시간 +1분 (학습 모드면 학습 시간에도 +1분)
///  2. LimitEvaluator 로 판정 (하루 한도는 자유 시간 = 전체 - 학습 기준)
///     학습 모드면 EnforcementState(→ ProcessMonitorWorker 의 허용 목록 차단)와 BrowserPolicy(사이트 허용 목록) 적용
///  3. 잠금이면 3단계 집행:
///     a) UserHost(사용자 세션)에 status{locked} 전송 → 전체 화면 잠금 창 + LockWorkStation()
///     b) UserHost 가 없으면(자녀가 강제 종료 등) CreateProcessAsUser 로 다시 실행
///     c) 유예 시간(기본 2분)이 지나도 세션이 여전히 잠금 해제 상태면 WTSDisconnectSession
///        → 로그인 화면으로 강제 전환. 다시 로그인해도 다음 틱에 또 끊긴다.
///  4. 서버에 사용량 보고
///
/// 정책 변경(원격 잠금/해제, 시간 추가) 시 틱을 기다리지 않고 즉시 재판정한다.
/// </summary>
public sealed class SessionControlWorker(
    PolicyStore policies,
    UsageStore usage,
    ServerConnection server,
    UserHostPipeServer userHost,
    UserSessionLauncher launcher,
    EnforcementState enforcement,
    BrowserPolicy browser,
    IOptions<AgentOptions> options,
    ILogger<SessionControlWorker> log) : BackgroundService
{
    private readonly SemaphoreSlim _wake = new(0, 1);
    private DateTimeOffset _lastCountedUtc = DateTimeOffset.UtcNow;
    private DateTimeOffset? _lockIssuedUtc;
    private LockReason? _lastReason;
    private bool _lastStudy;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        policies.Changed += _ => WakeNow();
        userHost.Reported += OnUserHostReport;
        server.RequestResultReceived += r => _ = userHost.BroadcastAsync(ResultNotice(r));
        var tick = TimeSpan.FromSeconds(Math.Max(10, options.Value.SessionTickSeconds));

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await TickAsync();
            }
            catch (Exception ex)
            {
                log.LogError(ex, "세션 제어 틱 오류");
            }

            try
            {
                await _wake.WaitAsync(tick, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private void WakeNow()
    {
        if (_wake.CurrentCount == 0)
        {
            try { _wake.Release(); } catch (SemaphoreFullException) { }
        }
    }

    private async Task TickAsync()
    {
        var policy = policies.Current;
        if (policy is null) return; // 페어링 전

        var nowUtc = DateTimeOffset.UtcNow;
        var localNow = LimitEvaluator.LocalNow(policy, nowUtc);
        var today = LimitEvaluator.DateKey(localNow);
        var session = WtsApi.GetConsoleSession();
        var activeUnlocked = session is { HasUser: true, IsActive: true, IsLocked: false };

        // 1) 사용 시간 누적: 실제 경과 분만큼 (정책 변경으로 틱이 앞당겨져도 중복 집계 안 됨)
        //    학습 여부는 사용량과 무관하게 정해지므로 먼저 판정해서 학습 시간에도 더한다
        var studyNow = LimitEvaluator.Decide(policy, 0, localNow).Study;
        var elapsedMinutes = (int)((nowUtc - _lastCountedUtc).TotalMinutes);
        if (elapsedMinutes >= 1)
        {
            _lastCountedUtc = _lastCountedUtc.AddMinutes(elapsedMinutes);
            // 서비스 정지/절전 등으로 공백이 생긴 경우 최대 2분만 인정
            if (activeUnlocked) usage.AddMinutes(today, Math.Min(elapsedMinutes, 2), study: studyNow);
        }
        var used = usage.Get(today);
        var studyMinutes = Math.Min(used, usage.GetStudy(today));
        var free = Math.Max(0, used - studyMinutes);

        // 2) 판정
        var decision = LimitEvaluator.Decide(policy, free, localNow);
        var reason = decision.Lock;
        var remaining = LimitEvaluator.RemainingMinutes(policy, free, localNow);
        var studyUntil = decision.Study ? LimitEvaluator.StudyEndsAt(policy, localNow)?.ToString("HH:mm") : null;

        if (reason != _lastReason)
        {
            server.Enqueue(reason is { } r
                ? EventMessage.Now("lock_started", r.ToWire())
                : EventMessage.Now("lock_ended", ""));
            _lastReason = reason;
        }
        if (decision.Study != _lastStudy)
        {
            server.Enqueue(decision.Study
                ? EventMessage.Now("study_started", studyUntil is null ? "" : $"~{studyUntil}")
                : EventMessage.Now("study_ended", ""));
            _lastStudy = decision.Study;
        }

        // 학습 모드 집행: 프로그램 허용 목록(ProcessMonitorWorker) + 사이트 허용 목록(Chrome/Edge 정책)
        enforcement.Set(new EnforcementState.Snapshot(
            decision.Study ? LimitEvaluator.StudyApps(policy, localNow) : null, studyUntil));
        browser.Apply(decision.Study ? LimitEvaluator.StudySites(policy, localNow) : null);

        // 3) 집행
        if (session is { HasUser: true } s)
        {
            if (!userHost.HasClients) launcher.EnsureUserHost(s.SessionId);

            await userHost.BroadcastAsync(new UserHostCommand
            {
                Type = "status",
                Locked = reason is not null,
                Reason = reason?.ToKorean(),
                UsedMinutes = free,
                RemainingMinutes = remaining,
                StudyMinutes = studyMinutes,
                StudyUntil = studyUntil,
                Rules = RulesText(policy),
            });

            if (reason is null)
            {
                _lockIssuedUtc = null;
            }
            else if (activeUnlocked)
            {
                _lockIssuedUtc ??= nowUtc;
                var grace = TimeSpan.FromSeconds(options.Value.LockGraceSeconds);
                if (nowUtc - _lockIssuedUtc >= grace)
                {
                    log.LogWarning("잠금 유예 초과 → 세션 {Session} 연결 끊기", s.SessionId);
                    if (WtsApi.Disconnect(s.SessionId))
                        server.Enqueue(EventMessage.Now("session_disconnected", reason.Value.ToWire()));
                    _lockIssuedUtc = nowUtc; // 다시 로그인하면 유예 시간 후 또 끊김
                }
            }
        }

        // 4) 보고
        server.Enqueue(new UsageMessage(
            today, used, new Dictionary<string, int>(), reason is not null, reason, studyMinutes, decision.Mode));
    }

    /// <summary>UserHost 에서 온 아이의 요청·공부 시작 → 서버로 전달 (값 검증은 서버가 다시 함)</summary>
    private void OnUserHostReport(UserHostReport r, int clientSession)
    {
        switch (r.Type)
        {
            case "request" when r.Kind is "site" or "app" && !string.IsNullOrWhiteSpace(r.Value) && r.Value.Length <= 500:
                var note = (r.Note ?? "").Trim();
                if (note.Length > 200) note = note[..200];
                server.Enqueue(new AccessRequestMessage(r.Kind, r.Value.Trim(), note));
                break;
            case "study_start":
                server.Enqueue(new StudyStartMessage(Math.Clamp(r.Minutes, 10, 240)));
                break;
        }
    }

    private static UserHostCommand ResultNotice(RequestResult r)
    {
        var what = r.Kind == "site" ? "사이트" : "프로그램";
        return new UserHostCommand
        {
            Type = "notify",
            Title = $"{what} 요청: {r.Value}",
            Text = r.Decision switch
            {
                "today" => "오늘은 쓸 수 있어요.",
                "always" => "이제 학습 시간에 항상 쓸 수 있어요.",
                "deny" => "부모님이 허락하지 않았어요.",
                _ => "주소 형식을 알아볼 수 없어요. 예: ebsi.co.kr",
            },
        };
    }

    /// <summary>"내 규칙 보기" 에 표시할 요약 (아이에게 규칙을 투명하게 공개)</summary>
    private static string RulesText(Policy p)
    {
        string[] days = ["월", "화", "수", "목", "금", "토", "일"];
        var lines = new List<string>
        {
            "하루 자유 시간: " + (p.DailyLimitMinutes is { } l ? $"{l}분" : "제한 없음"),
        };
        if (p.Bedtime is { } b) lines.Add($"잠자는 시간: {b.Start} ~ {b.End}");
        if (p.Study.Schedule.Count == 0) lines.Add("학습 시간표: 없음");
        foreach (var blk in p.Study.Schedule)
            lines.Add($"학습: {string.Concat(blk.Days.Order().Select(d => days[d - 1]))} {blk.Start} ~ {blk.End}");
        lines.Add("학습 사이트: " + (p.Study.Sites.Count > 0 ? string.Join(", ", p.Study.Sites) : "없음"));
        lines.Add("학습 프로그램: " + (p.Study.Apps.Count > 0 ? string.Join(", ", p.Study.Apps.Select(a => Path.GetFileName(a))) : "없음"));
        return string.Join("\n", lines);
    }
}
