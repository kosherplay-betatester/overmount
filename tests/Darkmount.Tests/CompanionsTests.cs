using System.Xml.Linq;
using Darkmount.App.Setup;

namespace Darkmount.Tests;

public class CompanionsTests
{
    [Theory]
    [InlineData(@"C:\Program Files\HWiNFO64\", null, @"C:\Program Files\HWiNFO64\")]
    [InlineData(null, "\"C:\\Program Files (x86)\\MSI Afterburner\\uninstall.exe\"", @"C:\Program Files (x86)\MSI Afterburner")]
    [InlineData("", @"C:\Tools\RTSS\RTSS.exe,0", @"C:\Tools\RTSS")]
    [InlineData(null, "uninstall.exe", null)] // not a full path: can't tell where
    [InlineData(null, null, null)]
    public void Install_folder_comes_from_the_installed_apps_entry(string? location, string? icon, string? expected) =>
        Assert.Equal(expected, Companions.FolderFromEntry(location, icon));

    [Fact]
    public void Winget_installs_the_exact_package_silently_without_questions()
    {
        var args = Companions.WingetInstallArgs(Companions.Afterburner);

        Assert.StartsWith("install --id Guru3D.Afterburner --exact --silent", args);
        Assert.Contains("--accept-package-agreements", args);
        Assert.Contains("--accept-source-agreements", args);
        Assert.Contains("--disable-interactivity", args);
        Assert.Equal(["Guru3D.Afterburner", "Guru3D.RTSS", "REALiX.HWiNFO"], Companions.All.Select(a => a.WingetId));
    }

    [Fact]
    public void Afterburner_autostart_task_matches_afterburners_own()
    {
        var xml = Companions.AfterburnerTaskXml(@"C:\Program Files (x86)\MSI Afterburner\MSIAfterburner.exe", "S-1-5-21-1-2-3-1001");
        XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
        var task = XDocument.Parse(xml).Root!;

        Assert.StartsWith("<?xml version=\"1.0\" encoding=\"UTF-16\"?>", xml);
        Assert.Equal(@"\MSIAfterburner", task.Descendants(ns + "URI").Single().Value);
        Assert.Equal("HighestAvailable", task.Descendants(ns + "RunLevel").Single().Value);
        Assert.Equal("PT0S", task.Descendants(ns + "ExecutionTimeLimit").Single().Value); // never stopped after 72 h
        Assert.Equal("false", task.Descendants(ns + "DisallowStartIfOnBatteries").Single().Value);
        Assert.Equal("false", task.Descendants(ns + "StopIfGoingOnBatteries").Single().Value);
        Assert.Equal(@"C:\Program Files (x86)\MSI Afterburner\MSIAfterburner.exe", task.Descendants(ns + "Command").Single().Value);
        Assert.Equal("/s", task.Descendants(ns + "Arguments").Single().Value);
        Assert.All(task.Descendants(ns + "UserId"), u => Assert.Equal("S-1-5-21-1-2-3-1001", u.Value));
        Assert.Single(task.Descendants(ns + "LogonTrigger"));
    }

    [Fact]
    public void Task_xml_escapes_odd_paths()
    {
        var xml = Companions.AfterburnerTaskXml(@"D:\Apps & Tools\<MSI>\MSIAfterburner.exe", "S-1");
        XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";

        Assert.Equal(@"D:\Apps & Tools\<MSI>\MSIAfterburner.exe", XDocument.Parse(xml).Descendants(ns + "Command").Single().Value);
    }

    static CompanionStatus S(CompanionApp app, bool installed, bool running) => new(app, installed ? @"C:\x\" + app.ExeName : null, running);

    [Fact]
    public void Assistant_is_offered_when_afterburner_or_rivatuner_is_missing()
    {
        var allGood = new[] { S(Companions.Afterburner, true, true), S(Companions.Rtss, true, true), S(Companions.HwInfo, false, false) };
        Assert.False(Companions.NeedsAttention(allGood, afterburnerAutostarts: false)); // HWiNFO is optional

        var noAfterburner = new[] { S(Companions.Afterburner, false, false), S(Companions.Rtss, true, false), S(Companions.HwInfo, true, true) };
        Assert.True(Companions.NeedsAttention(noAfterburner, afterburnerAutostarts: false));
        Assert.True(Companions.NeedsAttention(noAfterburner, afterburnerAutostarts: true)); // missing beats autostart
    }

    [Fact]
    public void Installed_but_not_running_counts_only_without_autostart()
    {
        // Right after logon Afterburner may not be up yet: its logon task will start it (and RivaTuner).
        var notYet = new[] { S(Companions.Afterburner, true, false), S(Companions.Rtss, true, false), S(Companions.HwInfo, false, false) };

        Assert.False(Companions.NeedsAttention(notYet, afterburnerAutostarts: true));
        Assert.True(Companions.NeedsAttention(notYet, afterburnerAutostarts: false));
    }

    [Fact]
    public void Only_program_files_counts_as_trusted_for_elevated_tasks()
    {
        var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        Assert.True(Companions.IsUnderProgramFiles(Path.Combine(pf86, "MSI Afterburner")));
        Assert.True(Companions.IsUnderProgramFiles(Path.Combine(pf, "HWiNFO64") + "\\"));
        Assert.False(Companions.IsUnderProgramFiles(pf + @" Evil\Afterburner"));                    // a look-alike folder name
        Assert.False(Companions.IsUnderProgramFiles(Path.Combine(pf, "..", "Users", "x", "Fake")));     // walks back out
        Assert.False(Companions.IsUnderProgramFiles(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)));
    }
}
