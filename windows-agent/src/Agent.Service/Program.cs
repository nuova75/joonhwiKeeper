using XKeep.Agent.Service;
using XKeep.Agent.Service.Enforcement;
using XKeep.Agent.Service.Ipc;
using XKeep.Agent.Service.Native;
using XKeep.Agent.Service.Net;
using XKeep.Agent.Service.State;
using XKeep.Agent.Service.Workers;

// 페어링 CLI 모드
if (args.Length > 0 && args[0] == "pair")
{
    return await PairCommand.RunAsync(args);
}

AgentPaths.EnsureSecureRoot();

var builder = Host.CreateApplicationBuilder(args);
// sc.exe 로 등록된 서비스로 실행되면 Windows 서비스 수명주기에 연결, 콘솔 실행 시엔 일반 콘솔 앱으로 동작(디버깅용)
builder.Services.AddWindowsService(o => o.ServiceName = "xKeepAgent");
builder.Services.Configure<AgentOptions>(builder.Configuration.GetSection("Agent"));
builder.Services.Configure<HostOptions>(o => o.ShutdownTimeout = TimeSpan.FromSeconds(5));

builder.Services.AddSingleton<PolicyStore>();
builder.Services.AddSingleton<UsageStore>();
builder.Services.AddSingleton<UserSessionLauncher>();
builder.Services.AddSingleton<EnforcementState>();
builder.Services.AddSingleton<BrowserPolicy>();
builder.Services.AddSingleton<AppInventory>();

// 다른 워커가 주입받아야 하는 BackgroundService 는 싱글톤 + HostedService 로 함께 등록
builder.Services.AddSingleton<ServerConnection>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<ServerConnection>());
builder.Services.AddSingleton<UserHostPipeServer>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<UserHostPipeServer>());

builder.Services.AddHostedService<ProcessMonitorWorker>();
builder.Services.AddHostedService<SessionControlWorker>();

await builder.Build().RunAsync();
return 0;
