using System.Drawing;
using System.Windows.Forms;

namespace Offstage;

/// <summary>A minimal single-line text prompt (WinForms has no built-in InputBox).</summary>
internal static class InputDialog
{
    public static string? Show(string prompt, string title, string initial = "")
    {
        using var form = new Form
        {
            Text = title,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterScreen,
            ClientSize = new Size(380, 130),
            MinimizeBox = false,
            MaximizeBox = false,
            ShowInTaskbar = false
        };

        var label = new Label { Text = prompt, AutoSize = false, Bounds = new Rectangle(12, 12, 356, 20) };
        var box = new TextBox { Text = initial, Bounds = new Rectangle(12, 40, 356, 24) };
        var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, Bounds = new Rectangle(200, 84, 80, 30) };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Bounds = new Rectangle(288, 84, 80, 30) };

        form.Controls.AddRange(new Control[] { label, box, ok, cancel });
        form.AcceptButton = ok;
        form.CancelButton = cancel;

        return form.ShowDialog() == DialogResult.OK && box.Text.Trim().Length > 0
            ? box.Text.Trim()
            : null;
    }
}
