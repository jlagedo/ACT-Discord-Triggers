using System;
using System.Drawing;
using System.Windows.Forms;
using ACT_DiscordTriggers.Core.Update;

namespace ACT_DiscordTriggers {
  // The "a new version is available" confirm dialog: a code-built WinForms form (no designer,
  // no dependency) showing the release title + plain-text notes with Update / Skip. Returns
  // true when the user chooses to update.
  internal static class UpdatePromptForm {
    public static bool Ask(UpdateInfo info) {
      using (var form = new Form()) {
        form.Text = "ACT Discord Triggers — Update available";
        form.StartPosition = FormStartPosition.CenterScreen;
        form.FormBorderStyle = FormBorderStyle.Sizable;
        form.MinimizeBox = false;
        form.MaximizeBox = false;
        form.ShowIcon = false;
        form.ClientSize = new Size(520, 420);
        form.MinimumSize = new Size(420, 320);
        form.Font = new Font("Segoe UI", 9f);

        var header = new Label {
          Dock = DockStyle.Top,
          Height = 52,
          Padding = new Padding(14, 12, 14, 6),
          Text = $"Version {info.TagName} is available.\nYou are running v{typeof(AppInfo).Assembly.GetName().Version}.",
          Font = new Font("Segoe UI", 10f, FontStyle.Bold),
        };

        var notes = new TextBox {
          Dock = DockStyle.Fill,
          Multiline = true,
          ReadOnly = true,
          ScrollBars = ScrollBars.Vertical,
          WordWrap = true,
          BorderStyle = BorderStyle.None,
          BackColor = SystemColors.Window,
          Text = string.IsNullOrWhiteSpace(info.Notes) ? "(no release notes)" : info.Notes.Replace("\n", Environment.NewLine),
        };
        var notesHost = new Panel { Dock = DockStyle.Fill, Padding = new Padding(14, 0, 14, 8) };
        notesHost.Controls.Add(notes);

        var buttons = new FlowLayoutPanel {
          Dock = DockStyle.Bottom,
          FlowDirection = FlowDirection.RightToLeft,
          Height = 52,
          Padding = new Padding(10, 8, 10, 10),
        };
        var update = new Button { Text = "Update now", DialogResult = DialogResult.Yes, AutoSize = true, Padding = new Padding(8, 2, 8, 2) };
        var skip = new Button { Text = "Skip", DialogResult = DialogResult.No, AutoSize = true, Padding = new Padding(8, 2, 8, 2) };
        buttons.Controls.Add(update);
        buttons.Controls.Add(skip);

        // Add fill first, then docked top/bottom, so z-order lays them out correctly.
        form.Controls.Add(notesHost);
        form.Controls.Add(buttons);
        form.Controls.Add(header);
        form.AcceptButton = update;
        form.CancelButton = skip;

        return form.ShowDialog() == DialogResult.Yes;
      }
    }
  }
}
