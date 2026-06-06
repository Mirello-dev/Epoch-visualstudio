using System;
using System.Drawing;
using System.Windows.Forms;

namespace EpochVisualStudio.UI
{
    /// <summary>
    /// A small modal text-input dialog. Visual Studio has no direct equivalent to
    /// VS Code's <c>window.showInputBox</c>, so this provides the same behaviour:
    /// returns the entered string, or <c>null</c> if the user cancels.
    /// </summary>
    internal static class InputBox
    {
        public static string Show(string title, string prompt, string initialValue = "", bool password = false)
        {
            using (var form = new Form())
            using (var label = new Label())
            using (var textBox = new TextBox())
            using (var okButton = new Button())
            using (var cancelButton = new Button())
            {
                form.Text = title;
                form.FormBorderStyle = FormBorderStyle.FixedDialog;
                form.StartPosition = FormStartPosition.CenterScreen;
                form.MinimizeBox = false;
                form.MaximizeBox = false;
                form.ShowInTaskbar = false;
                form.ClientSize = new Size(440, 130);

                label.SetBounds(12, 12, 416, 40);
                label.Text = prompt;
                label.AutoSize = false;

                textBox.SetBounds(12, 58, 416, 23);
                textBox.Text = initialValue ?? string.Empty;
                if (password)
                {
                    textBox.UseSystemPasswordChar = true;
                }

                okButton.Text = "OK";
                okButton.DialogResult = DialogResult.OK;
                okButton.SetBounds(262, 95, 75, 25);

                cancelButton.Text = "Cancel";
                cancelButton.DialogResult = DialogResult.Cancel;
                cancelButton.SetBounds(353, 95, 75, 25);

                form.Controls.AddRange(new Control[] { label, textBox, okButton, cancelButton });
                form.AcceptButton = okButton;
                form.CancelButton = cancelButton;

                var result = form.ShowDialog();
                if (result != DialogResult.OK)
                {
                    return null;
                }

                var value = textBox.Text;
                return string.IsNullOrEmpty(value) ? null : value;
            }
        }
    }
}
