namespace XKeep.Agent.UserHost;

/// <summary>"학습에 필요해요" 요청 창: 사이트 주소 또는 막힌 프로그램을 부모에게 요청</summary>
internal sealed class RequestForm : Form
{
    private readonly RadioButton _site = new() { Text = "사이트", Checked = true, AutoSize = true };
    private readonly RadioButton _app = new() { Text = "프로그램", AutoSize = true };
    private readonly TextBox _value = new() { Width = 360, PlaceholderText = "예: ebsi.co.kr 또는 주소창의 주소 붙여넣기" };
    private readonly TextBox _note = new() { Width = 360, MaxLength = 200, PlaceholderText = "왜 필요한지 (선택)" };
    private readonly Button _send = new() { Text = "부모님께 요청", AutoSize = true };

    public RequestForm(Func<string, string, string, Task<bool>> send, string? presetApp = null)
    {
        Text = "학습에 필요한 사이트·프로그램 요청";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Font = new Font("Malgun Gothic", 10f);
        TopMost = true;

        if (presetApp is not null)
        {
            _app.Checked = true;
            _value.Text = presetApp;
        }

        var layout = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, Padding = new Padding(16) };
        var kinds = new FlowLayoutPanel { AutoSize = true };
        kinds.Controls.AddRange([_site, _app]);
        layout.Controls.AddRange([
            new Label { Text = "무엇이 필요한가요?", AutoSize = true }, kinds,
            _value, _note, _send,
            new Label { Text = "부모님이 허락하면 알림으로 알려드려요.", AutoSize = true, ForeColor = Color.DimGray },
        ]);
        Controls.Add(layout);
        AcceptButton = _send;

        _send.Click += async (_, _) =>
        {
            var value = _value.Text.Trim();
            if (value.Length == 0) return;
            _send.Enabled = false;
            var ok = await send(_site.Checked ? "site" : "app", value, _note.Text.Trim());
            if (ok)
            {
                Close();
            }
            else
            {
                _send.Enabled = true;
                MessageBox.Show(this, "보호 서비스에 연결되지 않았어요. 잠시 후 다시 시도하세요.", "xKeep");
            }
        };
    }
}
