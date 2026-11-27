using System.IO.Pipes;
using System.Text;
using XKeep.Agent.Shared;

namespace XKeep.Agent.UserHost;

/// <summary>
/// 서비스의 Named Pipe 에 접속 (양방향). 명령을 받고, 창 목록·요청을 보낸다. 끊기면 2초 후 재접속.
/// </summary>
internal sealed class PipeClient(Action<UserHostCommand> onCommand)
{
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private StreamWriter? _writer;

    public bool IsConnected => _writer is not null;

    public async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await using var pipe = new NamedPipeClientStream(".", Ipc.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
                await pipe.ConnectAsync(5000, ct);
                _writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
                using var reader = new StreamReader(pipe, Encoding.UTF8, false, 4096, leaveOpen: true);
                while (await reader.ReadLineAsync(ct) is { } line)
                {
                    if (UserHostCommand.Parse(line) is { } cmd) onCommand(cmd);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception)
            {
                // 서비스 재시작 중 등 → 재시도
            }
            finally
            {
                _writer = null;
            }
            await Task.Delay(2000, ct).ContinueWith(_ => { });
        }
    }

    /// <returns>전송했으면 true (서비스 미연결이면 false)</returns>
    public async Task<bool> SendAsync(UserHostReport report)
    {
        await _sendGate.WaitAsync();
        try
        {
            if (_writer is not { } w) return false;
            await w.WriteLineAsync(report.ToJsonLine());
            return true;
        }
        catch (Exception)
        {
            return false;
        }
        finally
        {
            _sendGate.Release();
        }
    }
}
