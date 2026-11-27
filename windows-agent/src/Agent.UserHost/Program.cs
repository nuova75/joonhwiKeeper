namespace XKeep.Agent.UserHost;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        // 세션당 1개만 실행
        using var mutex = new Mutex(true, @"Local\xKeepUserHost", out var created);
        if (!created) return;

        ApplicationConfiguration.Initialize();
        Application.Run(new TrayApp());
    }
}
