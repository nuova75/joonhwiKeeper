using System.Net.WebSockets;
using System.Reflection;
using System.Text;
using System.Threading.Channels;
using XKeep.Agent.Service.State;
using XKeep.Agent.Shared;

namespace XKeep.Agent.Service.Net;

/// <summary>
/// 중계 서버와의 상시 WebSocket 연결.
///  - 수신: {type:"policy"} → PolicyStore 갱신 (워커들이 즉시 재판정), {type:"request_result"} → 이벤트
///  - 송신: 워커들이 Enqueue 한 사용량·프로세스·이벤트 (오프라인 동안 최대 500개 보관)
///  - 끊기면 지수 백오프(2s → 60s) 재접속. 서비스 정상 종료 시 goodbye 전송.
/// </summary>
public sealed class ServerConnection(PolicyStore policies, ILogger<ServerConnection> log) : BackgroundService
{
    private readonly Channel<AgentMessage> _outbox = Channel.CreateBounded<AgentMessage>(
        new BoundedChannelOptions(500) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });

    private ClientWebSocket? _ws;

    public static string AgentVersion { get; } =
        Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";

    public bool IsConnected => _ws?.State == WebSocketState.Open;

    public void Enqueue(AgentMessage msg) => _outbox.Writer.TryWrite(msg);

    /// <summary>아이의 요청에 대한 부모 결정 (UserHost 알림으로 전달)</summary>
    public event Action<RequestResult>? RequestResultReceived;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var attempt = 0;
        while (!ct.IsCancellationRequested)
        {
            var config = AgentConfigStore.Load();
            if (config is null)
            {
                log.LogWarning("페어링되지 않음. 'XKeep.Agent.Service.exe pair --server <url> --code <6자리>' 실행 필요");
                await Task.Delay(TimeSpan.FromSeconds(30), ct);
                continue;
            }

            try
            {
                using var ws = new ClientWebSocket();
                ws.Options.SetRequestHeader("Authorization", $"Bearer {config.Token}");
                ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(30);
                await ws.ConnectAsync(config.WebSocketUri, ct);
                _ws = ws;
                attempt = 0;
                log.LogInformation("서버 연결됨: {Uri}", config.WebSocketUri);

                await SendAsync(ws, new HelloMessage(AgentVersion, policies.Current?.Version ?? 0), ct);

                using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
                var recv = ReceiveLoop(ws, linked.Token);
                var send = SendLoop(ws, linked.Token);
                await Task.WhenAny(recv, send);
                await linked.CancelAsync();

                if (ws.CloseStatus is { } status && (int)status is 4401 or 4001)
                {
                    log.LogError("서버가 기기 인증을 거부함({Status}). 부모앱에서 기기가 삭제됐는지 확인 후 다시 페어링하세요.", status);
                    await Task.Delay(TimeSpan.FromMinutes(10), ct);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                log.LogWarning("서버 연결 실패: {Message}", ex.Message);
            }
            finally
            {
                _ws = null;
            }

            var backoff = TimeSpan.FromSeconds(Math.Min(60, 2 << Math.Min(attempt++, 5)));
            await Task.Delay(backoff, ct).ContinueWith(_ => { });
        }
    }

    private async Task ReceiveLoop(ClientWebSocket ws, CancellationToken ct)
    {
        var buffer = new byte[16 * 1024];
        using var ms = new MemoryStream();
        while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            var result = await ws.ReceiveAsync(buffer, ct);
            if (result.MessageType == WebSocketMessageType.Close) return;
            ms.Write(buffer, 0, result.Count);
            if (!result.EndOfMessage) continue;

            var text = Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length);
            ms.SetLength(0);
            try
            {
                if (ServerMessageParser.TryParsePolicy(text) is { } policy) policies.Update(policy);
                else if (ServerMessageParser.TryParseRequestResult(text) is { } result) RequestResultReceived?.Invoke(result);
            }
            catch (Exception ex)
            {
                log.LogWarning(ex, "서버 메시지 처리 실패");
            }
        }
    }

    private async Task SendLoop(ClientWebSocket ws, CancellationToken ct)
    {
        await foreach (var msg in _outbox.Reader.ReadAllAsync(ct))
        {
            await SendAsync(ws, msg, ct);
        }
    }

    private static Task SendAsync(ClientWebSocket ws, AgentMessage msg, CancellationToken ct) =>
        ws.SendAsync(Encoding.UTF8.GetBytes(msg.ToJson()), WebSocketMessageType.Text, true, ct);

    /// <summary>서비스 중지(PC 종료 포함) 시 goodbye 를 보내 서버의 "연결 끊김" 경고를 억제</summary>
    public override async Task StopAsync(CancellationToken ct)
    {
        if (IsConnected)
        {
            // ClientWebSocket 은 동시 Send 를 허용하지 않으므로 직접 보내지 않고 송신 큐를 거친다
            Enqueue(new GoodbyeMessage("service_stop"));
            await Task.Delay(TimeSpan.FromSeconds(1), CancellationToken.None);
        }
        await base.StopAsync(ct);
    }
}
