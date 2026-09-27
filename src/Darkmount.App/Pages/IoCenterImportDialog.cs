using Darkmount.App.IoCenter;

namespace Darkmount.App.Pages;

/// <summary>Lets the user pick IO Center profiles on this PC and/or exported .ioprofile files to import.</summary>
public sealed class IoCenterImportDialog : Form
{
    readonly CheckedListBox _list = new()
    {
        CheckOnClick = true, Dock = DockStyle.Fill, Font = Ui.Body, BackColor = Ui.Panel, ForeColor = Ui.Text, BorderStyle = BorderStyle.None,
        IntegralHeight = false,
    };
    readonly List<string> _paths = [];

    IoCenterImportDialog()
    {
        Ui.BeginLayout(this);
        Text = "Import from IO Center";
        Icon = AppIcon.Window;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(620, 440);
        MinimizeBox = MaximizeBox = false;
        BackColor = Ui.Back;
        ForeColor = Ui.Text;
        Font = Ui.Body;

        var intro = Ui.Note("Pick the IO Center profiles to bring over. Lighting (custom layers and the built-in effect), key " +
                            "bindings, display-key pictures and the linked game come along; keys that open programs become macros. " +
                            "IO Center's own files are only read, never changed.", 580);
        intro.Dock = DockStyle.Top;
        intro.Padding = new Padding(12, 10, 12, 6);

        foreach (var p in IoCenterImport.ListInstalled())
        {
            _paths.Add(p.Path);
            _list.Items.Add($"{p.Name}   ·   saved {p.Modified:d MMM yyyy HH:mm}", isChecked: true);
        }
        var listHost = new Panel { Dock = DockStyle.Fill, Padding = new Padding(12, 4, 12, 4) };
        listHost.Controls.Add(_list);
        if (_paths.Count == 0)
        {
            var empty = Ui.Note("No IO Center profiles were found on this PC. Use \"Add an exported file…\" to import a .ioprofile " +
                                "file (IO Center → Profiles → Export).", 560);
            empty.Dock = DockStyle.Top;
            listHost.Controls.Add(empty);
        }

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 56, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(10) };
        var import = Ui.Button("Import", (_, _) => { DialogResult = DialogResult.OK; Close(); }, primary: true);
        buttons.Controls.AddRange([Ui.Button("Cancel", (_, _) => Close()), import, Ui.Button("Add an exported file…", (_, _) => Browse())]);
        AcceptButton = import;

        Controls.Add(listHost);
        Controls.Add(buttons);
        Controls.Add(intro);
        Ui.EndLayout(this);
    }

    void Browse()
    {
        using var dlg = new OpenFileDialog
        {
            Title = "Choose exported IO Center profiles", Filter = "IO Center profiles|*.ioprofile|All files|*.*", Multiselect = true,
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        foreach (var file in dlg.FileNames)
        {
            if (_paths.Contains(file, StringComparer.OrdinalIgnoreCase)) continue;
            _paths.Add(file);
            _list.Items.Add($"{Path.GetFileName(file)}   ·   {Path.GetDirectoryName(file)}", isChecked: true);
        }
    }

    /// <summary>The chosen files, or null when cancelled.</summary>
    public static IReadOnlyList<string>? Ask(IWin32Window? owner)
    {
        using var dialog = new IoCenterImportDialog();
        if (dialog.ShowDialog(owner) != DialogResult.OK) return null;
        return dialog._list.CheckedIndices.Cast<int>().Select(i => dialog._paths[i]).ToList();
    }
}
