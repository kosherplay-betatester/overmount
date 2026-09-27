using Darkmount.App;
using Darkmount.Screens;
using Darkmount.Sensors;

namespace Darkmount.Tests;

public class AppLogicTests
{
    static readonly DateTime T0 = new(2026, 9, 24, 12, 0, 0);

    public AppLogicTests() => Log.Enabled = false;

    // ---------------------------------------------------------------- AlertEngine

    [Fact]
    public void No_alerts_for_normal_values()
    {
        var e = new AlertEngine(new AlertSettings());
        var s = new Snapshot { CpuTemp = 70, GpuTemp = 65, RamUsedMb = 10000, RamTotalMb = 64000 };

        Assert.Empty(e.Evaluate(s, T0));
        Assert.False(e.HasNewAlert);
    }

    [Fact]
    public void Hot_gpu_raises_an_alert_and_it_is_new_once()
    {
        var e = new AlertEngine(new AlertSettings());

        var alerts = e.Evaluate(new Snapshot { GpuTemp = 91 }, T0);
        Assert.Equal("GPU 91°C", Assert.Single(alerts).Text);
        Assert.True(e.HasNewAlert);

        e.Evaluate(new Snapshot { GpuTemp = 92 }, T0.AddSeconds(2));
        Assert.False(e.HasNewAlert);
    }

    [Fact]
    public void Alert_is_held_for_ten_seconds_after_recovery()
    {
        var e = new AlertEngine(new AlertSettings());
        e.Evaluate(new Snapshot { CpuTemp = 95 }, T0);

        Assert.Single(e.Evaluate(new Snapshot { CpuTemp = 60 }, T0.AddSeconds(9)));
        Assert.Empty(e.Evaluate(new Snapshot { CpuTemp = 60 }, T0.AddSeconds(11)));
    }

    [Fact]
    public void Memory_alerts_use_percentages()
    {
        var e = new AlertEngine(new AlertSettings());
        var s = new Snapshot { RamUsedMb = 59000, RamTotalMb = 64000, VramUsedMb = 15800, VramTotalMb = 16000 };

        var texts = e.Evaluate(s, T0).Select(a => a.Text).ToList();

        Assert.Contains("RAM 92%", texts);
        Assert.Contains("VRAM 99%", texts);
    }

    [Fact]
    public void Low_fps_needs_to_last_four_seconds_and_only_counts_in_game()
    {
        var e = new AlertEngine(new AlertSettings());
        var low = new Snapshot { Fps = 22, GameName = "game.exe" };

        Assert.Empty(e.Evaluate(low, T0));
        Assert.Empty(e.Evaluate(low, T0.AddSeconds(2)));
        Assert.Equal("FPS 22", Assert.Single(e.Evaluate(low, T0.AddSeconds(4))).Text);
        Assert.Empty(new AlertEngine(new AlertSettings()).Evaluate(new Snapshot { Fps = 5 }, T0.AddSeconds(10)));
    }

    [Fact]
    public void Disabled_alerts_never_fire()
    {
        var e = new AlertEngine(new AlertSettings { GpuTempEnabled = false });

        Assert.Empty(e.Evaluate(new Snapshot { GpuTemp = 99 }, T0));
    }

    // ---------------------------------------------------------------- AutoSwitcher

    [Fact]
    public void Auto_mode_follows_the_game()
    {
        var sw = new AutoSwitcher();
        var settings = new AppSettings { Mode = ScreenMode.Auto, DefaultScreen = ScreenKind.Animation };

        Assert.Equal(ScreenKind.Animation, sw.Update(inGame: false, settings));
        Assert.Equal(ScreenKind.Stats, sw.Update(inGame: true, settings));
        Assert.Equal(ScreenKind.Animation, sw.Update(inGame: false, settings));
    }

    [Fact]
    public void Manual_choice_holds_until_the_game_state_changes()
    {
        var sw = new AutoSwitcher();
        var settings = new AppSettings { Mode = ScreenMode.Auto, DefaultScreen = ScreenKind.Stats };
        sw.Update(false, settings);

        sw.Cycle();
        Assert.Equal(ScreenKind.Animation, sw.Update(false, settings));
        Assert.Equal(ScreenKind.Animation, sw.Update(false, settings));
        Assert.Equal(ScreenKind.Stats, sw.Update(true, settings)); // game started → auto again
    }

    [Fact]
    public void Fixed_modes_ignore_games()
    {
        var sw = new AutoSwitcher();

        Assert.Equal(ScreenKind.Animation, sw.Update(true, new AppSettings { Mode = ScreenMode.Animation }));
        Assert.Equal(ScreenKind.Stats, sw.Update(false, new AppSettings { Mode = ScreenMode.Stats }));
    }

    [Fact]
    public void Hotkey_cycles_dashboard_animation_and_the_dock_default_screen()
    {
        Assert.Equal(ScreenMode.Animation, AutoSwitcher.NextMode(ScreenMode.Auto));
        Assert.Equal(ScreenMode.DockDefault, AutoSwitcher.NextMode(ScreenMode.Animation));
        Assert.Equal(ScreenMode.Auto, AutoSwitcher.NextMode(ScreenMode.DockDefault));
        Assert.Equal(ScreenMode.Animation, AutoSwitcher.NextMode(ScreenMode.Clock));
    }

    [Fact]
    public void Fixed_modes_show_the_new_screens()
    {
        var sw = new AutoSwitcher();

        Assert.Equal(ScreenKind.NowPlaying, sw.Update(true, new AppSettings { Mode = ScreenMode.NowPlaying }));
        Assert.Equal(ScreenKind.Clock, sw.Update(false, new AppSettings { Mode = ScreenMode.Clock }));
        Assert.Equal(ScreenKind.Network, sw.Update(false, new AppSettings { Mode = ScreenMode.Network }));
        Assert.Equal(ScreenKind.FocusTimer, sw.Update(false, new AppSettings { Mode = ScreenMode.FocusTimer }));
    }

    [Fact]
    public void Smart_screens_show_a_new_song_for_a_while_and_the_running_focus_timer()
    {
        var sw = new AutoSwitcher();
        var settings = new AppSettings { Mode = ScreenMode.Auto, DefaultScreen = ScreenKind.Clock };

        Assert.Equal(ScreenKind.NowPlaying, sw.Update(false, settings, new ScreenSignals(T0, NewTrack: true)));
        Assert.Equal(ScreenKind.NowPlaying, sw.Update(false, settings, new ScreenSignals(T0.AddSeconds(15))));
        Assert.Equal(ScreenKind.Clock, sw.Update(false, settings, new ScreenSignals(T0 + AutoSwitcher.NowPlayingHold)));
        Assert.Equal(ScreenKind.FocusTimer, sw.Update(false, settings, new ScreenSignals(T0.AddMinutes(1), FocusTimerActive: true)));
        Assert.Equal(ScreenKind.Stats, sw.Update(true, settings, new ScreenSignals(T0.AddMinutes(1), FocusTimerActive: true))); // games win

        settings.SmartScreens = false;
        Assert.Equal(ScreenKind.Clock, sw.Update(false, settings, new ScreenSignals(T0.AddMinutes(2), FocusTimerActive: true, NewTrack: true)));
    }

    [Fact]
    public void Rotation_takes_turns_in_fixed_time_slots()
    {
        var sw = new AutoSwitcher();
        var settings = new AppSettings
        {
            Mode = ScreenMode.Auto, RotateScreens = true, RotateSeconds = 30, Rotation = [ScreenKind.Stats, ScreenKind.Clock, ScreenKind.Network],
        };
        var start = new DateTime(2026, 1, 1, 12, 0, 0); // a multiple of 30 s

        Assert.Equal(ScreenKind.Stats, sw.Update(false, settings, new ScreenSignals(start)));
        Assert.Equal(ScreenKind.Stats, sw.Update(false, settings, new ScreenSignals(start.AddSeconds(29))));
        Assert.Equal(ScreenKind.Clock, sw.Update(false, settings, new ScreenSignals(start.AddSeconds(30))));
        Assert.Equal(ScreenKind.Network, sw.Update(false, settings, new ScreenSignals(start.AddSeconds(60))));
        Assert.Equal(ScreenKind.Stats, sw.Update(false, settings, new ScreenSignals(start.AddSeconds(90))));
        Assert.Equal(ScreenKind.Stats, sw.Update(true, settings, new ScreenSignals(start.AddSeconds(30)))); // in game: stats
    }

    // ---------------------------------------------------------------- Settings

    [Fact]
    public void Stats_layout_is_saved_and_old_settings_get_the_classic_layout()
    {
        var path = Path.Combine(Path.GetTempPath(), $"dmh-{Guid.NewGuid():N}.json");
        try
        {
            SettingsStore.Save(path, new AppSettings
            {
                StatsLayout = new StatsLayout { Primary = StatsValue.Load, GraphLoad = true, GraphTemperature = false, GraphPower = true },
            });
            var loaded = SettingsStore.Load(path).StatsLayout;
            Assert.Equal(StatsValue.Load, loaded.Primary);
            Assert.False(loaded.GraphTemperature);
            Assert.True(loaded.GraphPower);
            Assert.Contains("\"Primary\": \"Load\"", File.ReadAllText(path)); // readable in settings.json

            File.WriteAllText(path, """{ "Mode": "Auto" }""");
            var classic = SettingsStore.Load(path).StatsLayout;
            Assert.Equal(StatsValue.Temperature, classic.Primary);
            Assert.True(classic.GraphLoad && classic.GraphTemperature && !classic.GraphPower);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Settings_round_trip_through_json_and_default_when_missing()
    {
        var path = Path.Combine(Path.GetTempPath(), $"dmh-{Guid.NewGuid():N}.json");
        try
        {
            Assert.Equal(ScreenMode.Auto, SettingsStore.Load(path).Mode);

            var s = new AppSettings { Mode = ScreenMode.Animation, AnimationPath = @"C:\x.gif" };
            s.Alerts.GpuTempMax = 80;
            s.Sensors.GpuIndex = 2;
            SettingsStore.Save(path, s);

            var loaded = SettingsStore.Load(path);
            Assert.Equal(ScreenMode.Animation, loaded.Mode);
            Assert.Equal(@"C:\x.gif", loaded.AnimationPath);
            Assert.Equal(80, loaded.Alerts.GpuTempMax);
            Assert.Equal(2, loaded.Sensors.GpuIndex);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Corrupt_settings_fall_back_to_defaults()
    {
        var path = Path.Combine(Path.GetTempPath(), $"dmh-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, "{ not json");
        try { Assert.Equal(ScreenMode.Auto, SettingsStore.Load(path).Mode); }
        finally { File.Delete(path); }
    }
}
