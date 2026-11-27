using System.Runtime.InteropServices;
using System.Text;

namespace XKeep.Agent.UserHost;

/// <summary>
/// 지금 화면(작업 표시줄)에 보이는 최상위 창을 가진 프로세스 id 목록.
/// 서비스(Session 0)는 사용자 데스크톱의 창을 볼 수 없어서 UserHost 가 대신 조사해 보고한다.
/// 스토어(UWP) 앱은 창 주인이 ApplicationFrameHost 이므로 자식 창의 실제 앱 프로세스도 함께 넣는다.
/// </summary>
internal static class WindowScanner
{
    public static List<int> VisibleWindowPids()
    {
        var pids = new HashSet<int>();
        EnumWindows((hwnd, _) =>
        {
            if (!IsAppWindow(hwnd)) return true;
            GetWindowThreadProcessId(hwnd, out var pid);
            pids.Add((int)pid);
            // UWP: 프레임 창 안의 실제 앱 프로세스
            EnumChildWindows(hwnd, (child, _) =>
            {
                GetWindowThreadProcessId(child, out var childPid);
                if (childPid != pid) pids.Add((int)childPid);
                return true;
            }, IntPtr.Zero);
            return true;
        }, IntPtr.Zero);
        pids.Remove(Environment.ProcessId);
        return pids.ToList();
    }

    private static bool IsAppWindow(IntPtr hwnd)
    {
        if (!IsWindowVisible(hwnd) || GetWindow(hwnd, GW_OWNER) != IntPtr.Zero) return false;
        if ((GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64() & WS_EX_TOOLWINDOW) != 0) return false;
        if (GetWindowTextLength(hwnd) == 0) return false;
        // 가상 데스크톱 등으로 숨겨진(cloaked) 창 제외
        return DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, out var cloaked, sizeof(int)) != 0 || cloaked == 0;
    }

    private delegate bool EnumProc(IntPtr hwnd, IntPtr lParam);

    private const int GWL_EXSTYLE = -20;
    private const long WS_EX_TOOLWINDOW = 0x80;
    private const uint GW_OWNER = 4;
    private const int DWMWA_CLOAKED = 14;

    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc cb, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool EnumChildWindows(IntPtr parent, EnumProc cb, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr hwnd, uint cmd);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowTextLength(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out int value, int size);
}
