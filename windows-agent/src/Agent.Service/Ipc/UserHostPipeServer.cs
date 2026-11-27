using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using XKeep.Agent.Shared;

namespace XKeep.Agent.Service.Ipc;

/// <summary>
/// 서비스 ↔ UserHost 채널 (Named Pipe, 한 줄 JSON, 양방향).
///  - 보내기: UserHostCommand (상태·알림)
///  - 받기: UserHostReport (창 목록·요청·공부 시작) → Reported 이벤트. 보낸 프로세스의 세션 id 를 함께 전달
/// 파이프 ACL: SYSTEM 전체 권한, 로그인 사용자는 읽기/쓰기(접속)만.
///
/// 보안 메모: 다른 프로세스가 같은 이름의 가짜 파이프를 먼저 만들 수 있지만(pipe squatting),
/// 그 경우 UserHost 화면만 속일 수 있고 실제 집행(세션 끊기, 프로세스 종료)은 서비스가 하므로 우회되지 않는다.
/// </summary>
public sealed class UserHostPipeServer(ILogger<UserHostPipeServer> log) : BackgroundService
{
    private readonly ConcurrentDictionary<Guid, StreamWriter> _clients = new();
    private UserHostCommand? _lastStatus;

    public bool HasClients => !_clients.IsEmpty;

    /// <summary>(보고 내용, 보낸 프로세스의 세션 id)</summary>
    public event Action<UserHostReport, int>? Reported;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            NamedPipeServerStream? pipe = null;
            try
            {
                pipe = NamedPipeServerStreamAcl.Create(
                    Ipc.PipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 0, CreateSecurity());
                await pipe.WaitForConnectionAsync(ct);
                Register(pipe);
            }
            catch (OperationCanceledException)
            {
                pipe?.Dispose();
                break;
            }
            catch (Exception ex)
            {
                pipe?.Dispose();
                log.LogWarning(ex, "파이프 수락 실패");
                await Task.Delay(1000, ct);
            }
        }
    }

    private void Register(NamedPipeServerStream pipe)
    {
        var id = Guid.NewGuid();
        var writer = new StreamWriter(pipe, new UTF8Encoding(false)) { AutoFlush = true };
        _clients[id] = writer;
        log.LogInformation("UserHost 연결됨 ({Count})", _clients.Count);
        // 새로 붙은 UserHost 에 현재 상태를 즉시 전달 (잠금 중에 재실행된 경우 바로 잠금 화면)
        if (_lastStatus is { } s) _ = WriteAsync(id, writer, s);
        _ = ReadLoopAsync(id, pipe, ClientSessionId(pipe));
    }

    private async Task ReadLoopAsync(Guid id, NamedPipeServerStream pipe, int sessionId)
    {
        using var reader = new StreamReader(pipe, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: false, leaveOpen: true);
        try
        {
            while (await reader.ReadLineAsync() is { } line)
            {
                if (line.Length > 64 * 1024) continue; // 비정상 크기 무시
                UserHostReport? report;
                try { report = UserHostReport.Parse(line); }
                catch (Exception) { continue; }
                if (report is not null) Reported?.Invoke(report, sessionId);
            }
        }
        catch (IOException)
        {
            // 연결 끊김
        }
        finally
        {
            if (_clients.TryRemove(id, out var w)) await w.DisposeAsync();
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetNamedPipeClientProcessId(Microsoft.Win32.SafeHandles.SafePipeHandle pipe, out uint processId);

    /// <summary>접속한 클라이언트 프로세스의 세션 id (-1 = 알 수 없음 → 보고를 신뢰하지 않음)</summary>
    private static int ClientSessionId(NamedPipeServerStream pipe)
    {
        try
        {
            if (!GetNamedPipeClientProcessId(pipe.SafePipeHandle, out var pid)) return -1;
            using var p = Process.GetProcessById((int)pid);
            return p.SessionId;
        }
        catch (Exception)
        {
            return -1;
        }
    }

    public async Task BroadcastAsync(UserHostCommand cmd)
    {
        if (cmd.Type == "status") _lastStatus = cmd;
        foreach (var (id, writer) in _clients) await WriteAsync(id, writer, cmd);
    }

    private async Task WriteAsync(Guid id, StreamWriter writer, UserHostCommand cmd)
    {
        try
        {
            await writer.WriteLineAsync(cmd.ToJsonLine());
        }
        catch (IOException)
        {
            // UserHost 종료됨 (자녀가 작업 관리자로 끈 경우 포함) → 다음 틱에 재실행됨
            if (_clients.TryRemove(id, out var w)) await w.DisposeAsync();
        }
    }

    private static PipeSecurity CreateSecurity()
    {
        var sec = new PipeSecurity();
        sec.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        sec.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null), PipeAccessRights.ReadWrite, AccessControlType.Allow));
        return sec;
    }
}
