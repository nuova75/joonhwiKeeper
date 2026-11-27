using System.Collections.Concurrent;
using System.Diagnostics;
using System.Management;
using Microsoft.Extensions.Options;
using XKeep.Agent.Service.Ipc;
using XKeep.Agent.Service.Native;
using XKeep.Agent.Service.Net;
using XKeep.Agent.Service.State;
using XKeep.Agent.Shared;

namespace XKeep.Agent.Service.Workers;

/// <summary>
/// 프로그램 차단. 두 가지 규칙:
///
/// 1. 차단 목록(blockedApps, 항상 적용): 실행 파일 이름이 일치하면 종료.
/// 2. 학습 모드(허용 목록 방식): 학습 허용 목록에 없는 "사용자 프로그램"은 종료.
///    사용자 프로그램 판단은 세 경로로 한다.
///    a) UserHost 가 3초마다 보내는 "화면에 창이 보이는 프로세스" 목록
///       → 서비스가 pid 의 실제 경로·세션을 직접 확인 (UserHost 보고 내용 자체는 신뢰하지 않음)
///    b) 사용자 폴더(C:\Users, ProgramData, Temp)나 WindowsApps(스토어 앱)에서 실행된 프로세스
///       → 창이 없어도 종료 (Discord·스토어 게임 등은 여기서 바로 잡힘, 창 목록 보고가 끊겨도 동작)
///    c) WMI 프로세스 시작 이벤트 + 5초 주기 스캔으로 b) 를 즉시 적용
///    Windows 폴더의 시스템 프로그램(탐색기, 설정 등)과 xKeep 자신은 항상 허용.
///
/// 학습 모드에서는 UserHost 가 꺼져 있으면(아이가 작업 관리자로 종료) 5초 안에 다시 띄우고,
/// 10분 안에 3번 이상 반복되면 부모에게 tamper_suspected 를 보고한다.
/// </summary>
public sealed class ProcessMonitorWorker(
    PolicyStore policies,
    EnforcementState enforcement,
    AppInventory inventory,
    ServerConnection server,
    UserHostPipeServer userHost,
    UserSessionLauncher launcher,
    IOptions<AgentOptions> options,
    ILogger<ProcessMonitorWorker> log) : BackgroundService
{
    /// <summary>부모가 실수로 등록해도 절대 종료하지 않을 시스템 프로세스</summary>
    private static readonly HashSet<string> Protected = new(StringComparer.OrdinalIgnoreCase)
    {
        "system", "smss.exe", "csrss.exe", "wininit.exe", "winlogon.exe", "services.exe", "lsass.exe",
        "svchost.exe", "explorer.exe", "dwm.exe", "fontdrvhost.exe", "sihost.exe", "ctfmon.exe",
        "logonui.exe", "taskmgr.exe", "conhost.exe", UserSessionLauncher.UserHostExe,
        Path.GetFileName(Environment.ProcessPath ?? "XKeep.Agent.Service.exe"),
    };

    private static readonly string OwnDir = AppContext.BaseDirectory;

    private readonly StudyAppRules _rules = StudyAppRules.ForThisMachine();
    private volatile HashSet<string> _blocked = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, DateTime> _lastReported = new(StringComparer.OrdinalIgnoreCase);
    private readonly Queue<DateTime> _userHostRelaunches = new();
    private DateTime _lastRelaunchAttempt = DateTime.MinValue;
    private DateTime _lastTamperReport = DateTime.MinValue;
    private ManagementEventWatcher? _watcher;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        UpdateBlocked(policies.Current);
        policies.Changed += p =>
        {
            UpdateBlocked(p);
            SafeScan(); // 정책 변경 즉시 이미 실행 중인 차단 대상 종료
        };
        enforcement.Changed += _ => SafeScan(); // 학습 모드 시작 즉시 정리
        userHost.Reported += OnUserHostReport;

        StartWmiWatcher();

        var scanInterval = TimeSpan.FromSeconds(Math.Max(1, options.Value.ProcessScanIntervalSeconds));
        var lastReport = DateTime.MinValue;
        using var timer = new PeriodicTimer(scanInterval);
        try
        {
            do
            {
                SafeScan();
                try
                {
                    EnsureUserHostDuringStudy();
                    inventory.FlushIfDue();
                    if (DateTime.UtcNow - lastReport >= TimeSpan.FromMinutes(1))
                    {
                        ReportRunningProcesses();
                        lastReport = DateTime.UtcNow;
                    }
                }
                catch (Exception ex)
                {
                    log.LogError(ex, "프로세스 감시 오류");
                }
            } while (await timer.WaitForNextTickAsync(ct));
        }
        catch (OperationCanceledException)
        {
            // 서비스 중지
        }
    }

    private void UpdateBlocked(Policy? policy)
    {
        _blocked = new HashSet<string>(
            (policy?.BlockedApps ?? []).Select(Normalize).Where(n => n.Length > 0 && !Protected.Contains(n)),
            StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>"Roblox" / "roblox.exe" / "C:\...\RobloxPlayerBeta.exe" → 파일명.exe 로 정규화</summary>
    private static string Normalize(string name)
    {
        var file = Path.GetFileName(name.Trim());
        return file.Length == 0 ? "" : file.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? file : file + ".exe";
    }

    private static int ConsoleSessionId() =>
        WtsApi.GetConsoleSession() is { HasUser: true } s ? (int)s.SessionId : -1;

    // ---------------- 학습 모드 판단 ----------------

    /// <summary>xKeep 자신. (이름이 아니라 경로로 판단: 다운로드 폴더의 "explorer.exe" 같은 위장 방지)</summary>
    private static bool IsAlwaysAllowed(string path) => path.StartsWith(OwnDir, StringComparison.OrdinalIgnoreCase);

    /// <summary>창이 없어도 학습 모드에서 종료할 위치: 사용자 쓰기 가능 폴더, 스토어 앱</summary>
    private bool IsUserLocation(string path) =>
        _rules.IsUserWritable(path) || path.Contains(@"\WindowsApps\", StringComparison.OrdinalIgnoreCase);

    private bool ShouldKillInStudy(string path, IReadOnlyList<string> allowed) =>
        !IsAlwaysAllowed(path) && !_rules.IsAllowed(path, allowed);

    /// <summary>UserHost 가 보낸 "화면에 창이 보이는 프로세스" 목록 처리</summary>
    private void OnUserHostReport(UserHostReport report, int clientSession)
    {
        if (report.Type != "windows" || report.Pids is not { } pids) return;
        var console = ConsoleSessionId();
        if (clientSession < 0 || clientSession != console) return; // 콘솔 세션의 UserHost 만 신뢰
        var allowed = enforcement.Current.StudyAllowed;

        foreach (var pid in pids.Distinct().Take(200))
        {
            try
            {
                using var p = Process.GetProcessById(pid);
                if (p.SessionId != console) continue; // 다른 세션 프로세스는 건드리지 않음
                var path = ProcessPath.Get(pid);
                if (path is null || _rules.IsSystem(path) || IsAlwaysAllowed(path)) continue;
                inventory.Saw(_rules.InventoryId(path), path);
                if (allowed is not null && ShouldKillInStudy(path, allowed)) Kill(pid, path, study: true);
            }
            catch (ArgumentException)
            {
                // 이미 종료됨
            }
        }
    }

    // ---------------- 감지 ----------------

    private void StartWmiWatcher()
    {
        try
        {
            _watcher = new ManagementEventWatcher(
                new ManagementScope(@"\\.\root\CIMV2"),
                new WqlEventQuery("SELECT ProcessID, ProcessName FROM Win32_ProcessStartTrace"));
            _watcher.EventArrived += (_, e) =>
            {
                try
                {
                    var name = e.NewEvent["ProcessName"] as string;
                    var pid = Convert.ToInt32(e.NewEvent["ProcessID"]);
                    if (name is not null && _blocked.Contains(name))
                    {
                        Kill(pid, name, study: false);
                        return;
                    }
                    if (enforcement.Current.StudyAllowed is { } allowed && ProcessPath.Get(pid) is { } path &&
                        IsUserLocation(path) && ShouldKillInStudy(path, allowed) && InConsoleSession(pid))
                    {
                        Kill(pid, path, study: true);
                    }
                }
                catch (Exception ex)
                {
                    log.LogDebug(ex, "WMI 이벤트 처리 실패");
                }
            };
            _watcher.Start();
            log.LogInformation("WMI 프로세스 시작 감시 활성화");
        }
        catch (Exception ex)
        {
            // WMI 서비스 문제 시에도 주기 스캔으로 계속 차단됨
            log.LogWarning(ex, "WMI 감시 시작 실패 → 주기 스캔만 사용");
        }
    }

    private static bool InConsoleSession(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return p.SessionId == ConsoleSessionId();
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private void SafeScan()
    {
        try
        {
            ScanAndKill();
        }
        catch (Exception ex)
        {
            log.LogError(ex, "프로세스 스캔 오류");
        }
    }

    private void ScanAndKill()
    {
        var blocked = _blocked;
        var allowed = enforcement.Current.StudyAllowed;
        if (blocked.Count == 0 && allowed is null) return;
        var console = ConsoleSessionId();

        foreach (var p in Process.GetProcesses())
        {
            using (p)
            {
                var exe = p.ProcessName + ".exe";
                if (blocked.Contains(exe))
                {
                    Kill(p.Id, exe, study: false);
                    continue;
                }
                if (allowed is null || p.SessionId != console || console < 0) continue;
                if (ProcessPath.Get(p.Id) is { } path && IsUserLocation(path) && ShouldKillInStudy(path, allowed))
                    Kill(p.Id, path, study: true);
            }
        }
    }

    // ---------------- 집행 ----------------

    private void Kill(int pid, string nameOrPath, bool study)
    {
        var name = Path.GetFileName(nameOrPath);
        try
        {
            using var p = Process.GetProcessById(pid);
            p.Kill(entireProcessTree: true);
            log.LogInformation("{Rule} 종료: {Path} (pid {Pid})", study ? "학습 모드" : "차단 목록", nameOrPath, pid);
        }
        catch (ArgumentException)
        {
            return; // 이미 종료됨
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "프로세스 종료 실패: {Name}", name);
            return;
        }

        // 같은 앱을 연타해도 30초에 한 번만 보고·알림
        var now = DateTime.UtcNow;
        if (_lastReported.TryGetValue(name, out var last) && now - last < TimeSpan.FromSeconds(30)) return;
        _lastReported[name] = now;

        var label = Path.GetFileNameWithoutExtension(name);
        server.Enqueue(EventMessage.Now("process_killed", study ? $"학습 모드: {name}" : name));
        _ = userHost.BroadcastAsync(study
            ? new UserHostCommand
            {
                Type = "notify",
                Title = $"학습 시간에는 {label}을(를) 쓸 수 없어요",
                Text = "학습에 꼭 필요하면 여기를 눌러 부모님께 요청하세요.",
                RequestApp = nameOrPath.Contains('\\') ? _rules.InventoryId(nameOrPath) : name,
            }
            : new UserHostCommand
            {
                Type = "notify",
                Title = "실행할 수 없는 프로그램",
                Text = $"{label} 은(는) 부모님이 차단한 프로그램입니다.",
            });
    }

    /// <summary>학습 모드에서는 UserHost(창 목록 보고)가 반드시 떠 있어야 한다</summary>
    private void EnsureUserHostDuringStudy()
    {
        if (enforcement.Current.StudyAllowed is null || userHost.HasClients) return;
        if (WtsApi.GetConsoleSession() is not { HasUser: true, IsActive: true } s) return;
        var now = DateTime.UtcNow;
        if (now - _lastRelaunchAttempt < TimeSpan.FromSeconds(10)) return; // 실행 직후 파이프 연결까지 여유
        _lastRelaunchAttempt = now;
        if (launcher.IsUserHostRunning(s.SessionId)) return;

        launcher.EnsureUserHost(s.SessionId);
        _userHostRelaunches.Enqueue(now);
        while (_userHostRelaunches.Count > 0 && now - _userHostRelaunches.Peek() > TimeSpan.FromMinutes(10))
            _userHostRelaunches.Dequeue();
        if (_userHostRelaunches.Count >= 3 && now - _lastTamperReport > TimeSpan.FromHours(1))
        {
            _lastTamperReport = now;
            server.Enqueue(EventMessage.Now("tamper_suspected", "학습 시간 중 xKeep 트레이 프로그램이 반복해서 종료됨"));
        }
    }

    /// <summary>모니터 앞 사용자 세션의 프로세스 이름 목록 보고 (시스템 서비스 제외)</summary>
    private void ReportRunningProcesses()
    {
        var session = WtsApi.GetConsoleSession();
        if (session is not { HasUser: true }) return;
        var names = Process.GetProcesses()
            .Select(p =>
            {
                using (p) return p.SessionId == session.Value.SessionId ? p.ProcessName + ".exe" : null;
            })
            .OfType<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();
        server.Enqueue(new ProcessesMessage(names));
    }

    public override void Dispose()
    {
        _watcher?.Stop();
        _watcher?.Dispose();
        base.Dispose();
    }
}
