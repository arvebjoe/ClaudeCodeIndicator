using System.Drawing;
using System.Windows.Forms;

namespace ClaudeCodeIndicator;

/// <summary>Tiny modal dialog for entering the ESP32 IP / hostname.</summary>
internal sealed class IpInputForm : Form
{
    private readonly TextBox _box;
    public string Value => _box.Text;

    public IpInputForm(string current)
    {
        Text = "ESP32 address";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterScreen;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        ClientSize = new Size(320, 112);

        var label = new Label
        {
            Text = "ESP32 IP address or hostname (leave blank to disable):",
            AutoSize = false,
            Location = new Point(12, 12),
            Size = new Size(296, 30)
        };
        _box = new TextBox
        {
            Text = current,
            Location = new Point(12, 46),
            Size = new Size(296, 23)
        };
        var ok = new Button
        {
            Text = "OK",
            DialogResult = DialogResult.OK,
            Location = new Point(152, 78),
            Size = new Size(75, 25)
        };
        var cancel = new Button
        {
            Text = "Cancel",
            DialogResult = DialogResult.Cancel,
            Location = new Point(233, 78),
            Size = new Size(75, 25)
        };

        Controls.Add(label);
        Controls.Add(_box);
        Controls.Add(ok);
        Controls.Add(cancel);
        AcceptButton = ok;
        CancelButton = cancel;
    }
}
