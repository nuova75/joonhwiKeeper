using System.Runtime.InteropServices;
using System.Text;

namespace XKeep.Agent.Service.Native;

/// <summary>
/// pid → 실행 파일 전체 경로. Process.MainModule 은 보호 프로세스·32/64비트 혼합에서 예외가 나므로
/// PROCESS_QUERY_LIMITED_INFORMATION + QueryFullProcessImageName 을 쓴다 (LocalSystem 이면 거의 모든 프로세스 가능).
/// </summary>
public static class ProcessPath
{
    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    public static string? Get(int pid)
    {
        var h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)pid);
        if (h == IntPtr.Zero) return null;
        try
        {
            var sb = new StringBuilder(1024);
            var size = (uint)sb.Capacity;
            return QueryFullProcessImageNameW(h, 0, sb, ref size) ? sb.ToString(0, (int)size) : null;
        }
        finally
        {
            CloseHandle(h);
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool QueryFullProcessImageNameW(IntPtr process, uint flags, StringBuilder exeName, ref uint size);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr h);
}
