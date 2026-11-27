using System.Runtime.InteropServices;
using XKeep.Agent.Shared;

namespace XKeep.Agent.UserHost;

/// <summary>
/// 트레이 아이콘 + 잠금 창 + 아이 쪽 메뉴(공부 시작, 요청, 내 규칙).
/// 서비스가 보내는 status 를 그대로 화면에 반영하고, 3초마다 화면에 보이는 창 목록을 서비스에 보고한다.
/// 잠금·차단 판단은 하지 않는다 (판단·집행은 서비스 책임).
/// </summary>
internal sealed class TrayApp : ApplicationContext
{
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool LockWorkStation();

    private readonly NotifyIcon _tray;
    private readonly SynchronizationContext _ui;
    private readonly CancellationTokenSource _cts = new();
    private readonly List<LockForm> _lockForms = [];
    private readonly PipeClient _client;
    private readonly System.Windows.Forms.Timer _windowTimer = new() { Interval = 3000 };
    private readonly ToolStripMenuItem _studyMenu = new("📚 공부 시작");
    private bool _locked;
    private string? _studyUntil;
    private string _rules = "아직 규칙을 받지 못했어요.";
    /// <summary>마지막 알림이 "막힌 프로그램"이면 말풍선 클릭 시 요청 창에 미리 채울 값</summary>
    private string? _pendingRequestApp;
    private RequestForm? _requestForm;

    public TrayApp()
    {
        _ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
        _client = new PipeClient(cmd => _ui.Post(_ => Handle(cmd), null));

        foreach (var minutes in new[] { 30, 60, 90 })
            _studyMenu.DropDownItems.Add($"{minutes}분", null, (_, _) => StartStudy(minutes));
        var menu = new ContextMenuStrip();
        menu.Items.Add(_studyMenu);
        menu.Items.Add("학습에 필요한 사이트·프로그램 요청…", null, (_, _) => OpenRequest(null));
        menu.Items.Add("내 규칙 보기", null, (_, _) => MessageBox.Show(_rules, "xKeep · 내 규칙"));
        // 종료 메뉴 없음: 자녀가 트레이에서 끌 수 없게

        _tray = new NotifyIcon { Icon = SystemIcons.Shield, Text = "xKeep 보호 중", Visible = true, ContextMenuStrip = menu };
        _tray.BalloonTipClicked += (_, _) => { if (_pendingRequestApp is { } app) OpenRequest(app); };

        _windowTimer.Tick += async (_, _) =>
        {
            if (_client.IsConnected)
                await _client.SendAsync(new UserHostReport { Type = "windows", Pids = WindowScanner.VisibleWindowPids() });
        };
        _windowTimer.Start();
        _ = _client.RunAsync(_cts.Token);
    }

    private async void StartStudy(int minutes)
    {
        var ok = await _client.SendAsync(new UserHostReport { Type = "study_start", Minutes = minutes });
        _tray.ShowBalloonTip(4000, "xKeep", ok
            ? $"{minutes}분 학습 모드를 시작해요. 학습 프로그램·사이트만 열려요."
            : "보호 서비스에 연결되지 않았어요.", ToolTipIcon.Info);
    }

    private void OpenRequest(string? presetApp)
    {
        _requestForm?.Close();
        _requestForm = new RequestForm(
            (kind, value, note) => _client.SendAsync(new UserHostReport { Type = "request", Kind = kind, Value = value, Note = note }),
            presetApp);
        _requestForm.FormClosed += (_, _) => _requestForm = null;
        _requestForm.Show();
        _requestForm.Activate();
    }

    private void Handle(UserHostCommand cmd)
    {
        switch (cmd.Type)
        {
            case "status":
                var text = cmd.StudyUntil is { } until ? $"xKeep · 📚 학습 시간 ~{until}"
                    : cmd.RemainingMinutes is { } r ? $"xKeep · 자유 {cmd.UsedMinutes}분 · {r}분 남음"
                    : $"xKeep · 자유 {cmd.UsedMinutes}분";
                _tray.Text = text.Length > 63 ? text[..63] : text; // NotifyIcon.Text 최대 63자
                if (cmd.Rules is { } rules) _rules = rules;
                if (cmd.Locked) ShowLock(cmd.Reason ?? "잠겼어요");
                else HideLock();
                if (cmd.StudyUntil != _studyUntil)
                {
                    if (cmd.StudyUntil is not null && _studyUntil is null)
                        _tray.ShowBalloonTip(5000, "📚 학습 시간", $"{cmd.StudyUntil}까지 학습 프로그램·사이트만 열려요.", ToolTipIcon.Info);
                    else if (cmd.StudyUntil is null && _studyUntil is not null && !cmd.Locked)
                        _tray.ShowBalloonTip(5000, "xKeep", "학습 시간이 끝났어요. 수고했어요!", ToolTipIcon.Info);
                    _studyUntil = cmd.StudyUntil;
                }
                // 학습 중이거나 잠겨 있으면 "공부 시작" 은 의미 없음
                _studyMenu.Enabled = !cmd.Locked && cmd.StudyUntil is null;
                if (!cmd.Locked && cmd.StudyUntil is null && cmd.RemainingMinutes is 5 or 1)
                    _tray.ShowBalloonTip(5000, "xKeep", $"자유 시간이 {cmd.RemainingMinutes}분 남았어요.", ToolTipIcon.Info);
                break;
            case "notify":
                _pendingRequestApp = cmd.RequestApp;
                _tray.ShowBalloonTip(5000, cmd.Title ?? "xKeep", cmd.Text ?? "", cmd.RequestApp is null ? ToolTipIcon.Info : ToolTipIcon.Warning);
                break;
        }
    }

    private void ShowLock(string reason)
    {
        if (_locked)
        {
            foreach (var f in _lockForms) f.SetReason(reason);
            return;
        }
        _locked = true;
        // 모니터마다 한 장씩 덮는다
        foreach (var screen in Screen.AllScreens)
        {
            var form = new LockForm(screen, reason);
            _lockForms.Add(form);
            form.Show();
        }
        // Windows 잠금 화면으로 전환 (사용자 세션에서 호출해야 동작)
        LockWorkStation();
    }

    private void HideLock()
    {
        if (!_locked) return;
        _locked = false;
        foreach (var f in _lockForms) f.Unlock();
        _lockForms.Clear();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _cts.Cancel();
            _windowTimer.Dispose();
            _tray.Dispose();
        }
        base.Dispose(disposing);
    }
}
