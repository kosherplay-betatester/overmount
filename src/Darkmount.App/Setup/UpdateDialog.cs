namespace Darkmount.App.Setup;

public enum UpdateChoice { UpdateNow, Later, Skip }

/// <summary>"A new version is available" with the release notes, then a download window that hands over to the setup.</summary>
public static class UpdateDialog
{
    public static UpdateChoice Offer(ReleaseInfo release, IWin32Window? owner = null)
    {
        using var f = NewForm($"OverMount {release.Version.ToString(3)} is available", 640, 480);
        var choice = UpdateChoice.Later;
        var head = new Label
        {
            Text = $"OverMount {release.Version.ToString(3)} is available — you have {Installer.CurrentVersion.ToString(3)}.",
            AutoSize = true, Font = new Font("Segoe UI Semibold", 12f), ForeColor = Ui.Text, Dock = DockStyle.Top, Padding = new Padding(16, 14, 16, 6),
        };
        var notes = new TextBox
        {
            Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill, BorderStyle = BorderStyle.None,
            BackColor = Ui.Panel, ForeColor = Ui.Text, Font = Ui.Body,
            Text = string.IsNullOrWhiteSpace(release.Notes) ? "See the release page for what's new." : release.Notes.Replace("\r\n", "\n").Replace("\n", "\r\n"),
        };
        var notesHost = new Panel { Dock = DockStyle.Fill, Padding = new Padding(16, 4, 16, 4) };
        notesHost.Controls.Add(notes);
        var bar = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 58, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(12) };
        bool canInstall = release.SetupUrl is not null;
        var update = Ui.Button(canInstall ? "Update now" : "Open the release page", (_, _) =>
        {
            if (canInstall) choice = UpdateChoice.UpdateNow;
            else Pages.HomePage.Open(release.PageUrl);
            f.Close();
        }, primary: true);
        bar.Controls.AddRange([update, Ui.Button("Later", (_, _) => f.Close()),
            Ui.Button("Skip this version", (_, _) => { choice = UpdateChoice.Skip; f.Close(); })]);
        var link = new LinkLabel { Text = "Release page", AutoSize = true, LinkColor = Ui.Accent, Margin = new Padding(0, 10, 16, 0) };
        link.LinkClicked += (_, _) => Pages.HomePage.Open(release.PageUrl);
        bar.Controls.Add(link);
        f.AcceptButton = update;
        f.Controls.Add(notesHost);
        f.Controls.Add(bar);
        f.Controls.Add(head);
        Ui.EndLayout(f);
        f.ShowDialog(owner);
        return choice;
    }

    /// <summary>Downloads and verifies the update, then starts it (it closes this app and restarts the new version).</summary>
    public static async Task DownloadAndInstall(ReleaseInfo release, IWin32Window? owner = null)
    {
        using var cts = new CancellationTokenSource();
        var f = NewForm("Updating OverMount", 460, 170);
        var label = new Label { Text = $"Downloading version {release.Version.ToString(3)}…", AutoSize = true, ForeColor = Ui.Text, Location = new Point(18, 18) };
        var bar = new ProgressBar { Location = new Point(18, 50), Size = new Size(410, 20), Maximum = 1000 };
        var cancel = Ui.Button("Cancel", (_, _) => cts.Cancel());
        cancel.Location = new Point(338, 86);
        f.Controls.AddRange([label, bar, cancel]);
        f.FormClosing += (_, _) => cts.Cancel();
        Ui.EndLayout(f);
        f.Show(owner);
        try
        {
            var path = await UpdateChecker.DownloadAsync(release, new Progress<double>(p => bar.Value = (int)Math.Clamp(p * 1000, 0, 1000)), cts.Token);
            label.Text = "Installing… OverMount restarts in a moment.";
            cancel.Enabled = false;
            Log.Write($"Starting the update to {release.Version.ToString(3)}");
            UpdateChecker.StartUpdate(path);
        }
        catch (OperationCanceledException) { f.Close(); }
        catch (Exception e) when (e is HttpRequestException or IOException or InvalidDataException or InvalidOperationException
                                      or System.ComponentModel.Win32Exception or UnauthorizedAccessException)
        {
            Log.Write($"Update failed: {e.Message}");
            f.Close();
            MessageBox.Show(owner, $"The update couldn't be downloaded: {e.Message}\n\nYou can get it from {release.PageUrl}", "OverMount",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    /// <summary>A dialog in 96-DPI units; call <see cref="Ui.EndLayout"/> once its controls are added.</summary>
    static Form NewForm(string title, int w, int h)
    {
        var f = new Form();
        Ui.BeginLayout(f);
        f.Text = title;
        f.ClientSize = new Size(w, h);
        f.StartPosition = FormStartPosition.CenterScreen;
        f.FormBorderStyle = FormBorderStyle.FixedDialog;
        f.MaximizeBox = f.MinimizeBox = false;
        f.BackColor = Ui.Back;
        f.ForeColor = Ui.Text;
        f.Font = Ui.Body;
        f.Icon = AppIcon.Window;
        return f;
    }
}
