using System.Net.Http.Json;
using XKeep.Agent.Service.Net;
using XKeep.Agent.Service.State;
using XKeep.Agent.Shared;

namespace XKeep.Agent.Service;

/// <summary>
/// 관리자 PowerShell 에서 1회 실행:
///   XKeep.Agent.Service.exe pair --server https://xkeep.example.com --code 123456
/// %ProgramData%\xKeep\agent.json(SYSTEM/관리자 전용 ACL)에 기기 토큰을 저장한다.
/// </summary>
public static class PairCommand
{
    public static async Task<int> RunAsync(string[] args)
    {
        string? Arg(string name)
        {
            var i = Array.IndexOf(args, name);
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        }

        var server = Arg("--server")?.TrimEnd('/');
        var code = Arg("--code");
        if (server is null || code is null)
        {
            Console.Error.WriteLine("사용법: XKeep.Agent.Service.exe pair --server <https://서버주소> --code <6자리 코드>");
            return 2;
        }

        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            var res = await http.PostAsJsonAsync($"{server}/api/agent/pair",
                new PairRequest(code, "windows", ServerConnection.AgentVersion), AgentJson.Options);
            if (!res.IsSuccessStatusCode)
            {
                Console.Error.WriteLine($"페어링 실패 ({(int)res.StatusCode}): {await res.Content.ReadAsStringAsync()}");
                return 1;
            }
            var pair = await res.Content.ReadFromJsonAsync<PairResponse>(AgentJson.Options)
                       ?? throw new InvalidOperationException("빈 응답");

            AgentConfigStore.Save(new AgentConfig(server, pair.DeviceId, pair.Token));
            AgentPaths.WriteAtomic(AgentPaths.PolicyFile,
                System.Text.Json.JsonSerializer.Serialize(pair.Policy, AgentJson.Options));
            Console.WriteLine($"페어링 완료: deviceId={pair.DeviceId}");
            Console.WriteLine("서비스를 재시작하세요: sc.exe stop xKeepAgent && sc.exe start xKeepAgent");
            return 0;
        }
        catch (UnauthorizedAccessException)
        {
            Console.Error.WriteLine("관리자 권한으로 실행하세요 (ProgramData\\xKeep 쓰기 권한 필요).");
            return 1;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"페어링 실패: {ex.Message}");
            return 1;
        }
    }
}
