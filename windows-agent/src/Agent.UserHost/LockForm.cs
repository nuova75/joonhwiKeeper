namespace XKeep.Agent.UserHost;

/// <summary>
/// 전체 화면·최상위 잠금 창. Alt+F4 등으로 닫히지 않고, 포커스를 잃으면 2초마다 다시 앞으로 나온다.
/// 이 창은 "안내" 수단이다. 자녀가 UserHost 자체를 종료하면 서비스가 재실행하고,
/// 잠금 해제 상태가 유예 시간 이상 지속되면 서비스가 세션 연결을 끊는다.
/// </summary>
internal sealed class LockForm : Form
{
    private readonly Label _reason;
    private readonly System.Windows.Forms.Timer _keepOnTop;
    private bool _allowClose;

    public LockForm(Screen screen, string reason)
    {
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        Bounds = screen.Bounds;
        TopMost = true;
        ShowInTaskbar = false;
        BackColor = Color.FromArgb(20, 51, 43);
        KeyPreview = true;

        _reason = new Label
        {
            Dock = DockStyle.Fill,
            ForeColor = Color.White,
            Font = new Font("Malgun Gothic", 28, FontStyle.Bold),
            TextAlign = ContentAlignment.MiddleCenter,
        };
        SetReason(reason);
        Controls.Add(_reason);

        _keepOnTop = new System.Windows.Forms.Timer { Interval = 2000 };
        _keepOnTop.Tick += (_, _) =>
        {
            if (!_allowClose)
            {
                TopMost = true;
                Activate();
            }
        };
        _keepOnTop.Start();
    }

    public void SetReason(string reason) => _reason.Text = $"🔒\n\n{reason}";

    public void Unlock()
    {
        _allowClose = true;
        _keepOnTop.Stop();
        Close();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        // 사용자 닫기(Alt+F4) 차단. Windows 종료/로그오프는 허용
        if (!_allowClose && e.CloseReason == CloseReason.UserClosing) e.Cancel = true;
        base.OnFormClosing(e);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Alt && e.KeyCode == Keys.F4) e.Handled = true;
        base.OnKeyDown(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _keepOnTop.Dispose();
        base.Dispose(disposing);
    }
}
