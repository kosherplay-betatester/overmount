using Darkmount.App.IoCenter;
using Darkmount.Keyboard;
using Darkmount.Keyboard.Lamps;
using Microsoft.Win32;

namespace Darkmount.Tests;

/// <summary>Scene files (My presets import/export), the 1.5 effects, and IO Center's startup switch.</summary>
public class LightingLibraryTests
{
    // Keys as lamps (lamp id = key id) plus a ring of 40 edge LEDs (lamp ids 500+).
    static readonly IReadOnlyList<LampPoint> Lamps = BuildLamps();

    static IReadOnlyList<LampPoint> BuildLamps()
    {
        var rects = KeyGeometry.Keys(PhysicalLayout.Ansi, NumpadSide.Right)
            .Where(r => KeyIds.Find(r.KeyId)?.Zone is KeyZone.Keyboard or KeyZone.Numpad).ToList();
        float w = rects.Max(r => r.X + r.Width), h = rects.Max(r => r.Y + r.Height);
        var list = rects.Select(r => new LampPoint(r.KeyId, (r.X + r.Width / 2) / w, (r.Y + r.Height / 2) / h, true, r.KeyId)).ToList();
        for (int i = 0; i < 40; i++)
        {
            double a = i / 40.0 * 2 * Math.PI;
            list.Add(new LampPoint(500 + i, 0.5 + 0.5 * Math.Cos(a), 0.5 + 0.5 * Math.Sin(a), false, 0, i + 1));
        }
        return list;
    }

    static int KeyWithUsage(int usage) => KeyIds.All.First(k => k.Zone == KeyZone.Keyboard && k.HidUsage == usage).Id;

    static SceneContext Ctx(double t, params KeyPress[] presses) => new() { Seconds = t, Lamps = Lamps, KeyPresses = presses };

    static Dictionary<int, LampColor> Render(SceneEffect effect, SceneContext ctx, int speed = 5) =>
        SceneRenderer.Render(new LightingScene { Layers = [SceneEffects.CreateLayer(effect) is var l ? Tuned(l, speed) : null!] }, ctx);

    static LightLayer Tuned(LightLayer l, int speed) { l.Speed = speed; return l; }

    static int Level(LampColor c) => c.R + c.G + c.B;

    static double Total(Dictionary<int, LampColor> frame, Func<int, bool> lamps) => frame.Where(kv => lamps(kv.Key)).Sum(kv => Level(kv.Value));

    // ---------------------------------------------------------------- scene files

    [Fact]
    public void Scene_files_round_trip_every_preset()
    {
        var all = ScenePresets.All;
        var back = SceneFiles.Parse(SceneFiles.Serialize(all));

        Assert.Equal(all.Count, back.Count);
        for (int i = 0; i < all.Count; i++)
        {
            Assert.Equal(all[i].Name, back[i].Name);
            Assert.Equal(System.Text.Json.JsonSerializer.Serialize(all[i].Layers), System.Text.Json.JsonSerializer.Serialize(back[i].Layers));
        }
    }

    [Theory]
    [InlineData("{ not json")]
    [InlineData("""{ "Format": "something-else", "Scenes": [ { "Name": "x" } ] }""")]
    [InlineData("""{ "Format": "overmount-scene", "Scenes": [] }""")]
    [InlineData("""{ "Format": "overmount-scene", "Scenes": [ { "Name": "x", "Layers": [ { "Effect": "FromTheFuture" } ] } ] }""")]
    [InlineData("null")]
    public void Scene_files_refuse_anything_else(string json) => Assert.Throws<FormatException>(() => SceneFiles.Parse(json));

    [Fact]
    public void Imported_scenes_are_cleaned_up()
    {
        var layers = string.Join(",", Enumerable.Range(0, 12).Select(i => $$"""{ "Name": "L{{i}}", "Effect": "Static", "Colors": null }"""));
        var json = $$"""{ "Format": "overmount-scene", "Version": 7, "Scenes": [ null, { "Name": "  Bad\nname\t  ", "Layers": [ {{layers}} ] } ] }""";

        var scene = Assert.Single(SceneFiles.Parse(json));

        Assert.Equal("Badname", scene.Name);
        Assert.Equal(LightingScene.MaxLayers, scene.Layers.Count);
        Assert.All(scene.Layers, l => Assert.NotNull(l.Colors));
        Assert.NotEmpty(SceneRenderer.Render(scene, Ctx(1)));
    }

    [Fact]
    public void Unique_names_count_up_and_stay_short()
    {
        Assert.Equal("Glow", SceneFiles.UniqueName("Glow", ["Other"]));
        Assert.Equal("Glow (2)", SceneFiles.UniqueName("Glow", ["glow"]));
        Assert.Equal("Glow (3)", SceneFiles.UniqueName("Glow", ["Glow", "Glow (2)"]));
        var longName = new string('x', SceneFiles.MaxNameLength);
        Assert.True(SceneFiles.UniqueName(longName, [longName]).Length <= SceneFiles.MaxNameLength);
    }

    [Fact]
    public void Imports_are_bounded_and_bad_colours_dropped()
    {
        var layer = new LightLayer
        {
            Colors = [.. Enumerable.Repeat("FF0000", 5000), null!, "nothex"], Speed = 99, Brightness = -5,
            Keys = [.. Enumerable.Range(0, 100_000)], KeyColors = new() { [5] = null!, [6] = "#00FF00", [7] = "zzz" },
        };
        var clean = SceneFiles.Sanitize(new LightingScene { Name = null!, Description = null!, Background = null!, Layers = [layer] });

        var l = Assert.Single(clean.Layers);
        Assert.True(l.Colors.Count <= 7 && l.Colors.All(SceneFiles.IsHex));
        Assert.True(l.Keys.Count <= 512);
        Assert.Equal(["#00FF00"], l.KeyColors.Values);
        Assert.Equal((10, 0), (l.Speed, l.Brightness));
        Assert.Equal(("Imported scene", "", "000000"), (clean.Name, clean.Description, clean.Background));
    }

    [Fact]
    public void Settings_with_broken_library_entries_still_load()
    {
        var path = Path.Combine(Path.GetTempPath(), $"dmh-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, """{ "CustomScenes": [ null, { "Name": "Mine", "Description": null, "Layers": null } ], "FavoriteScenes": [ null, "Fire", "Fire" ] }""");
            var s = Darkmount.App.SettingsStore.Load(path);
            var mine = Assert.Single(s.CustomScenes);
            Assert.Equal("Mine", mine.Name);
            Assert.NotNull(mine.Description);
            Assert.Empty(mine.Layers);
            Assert.Equal(["Fire"], s.FavoriteScenes);

            File.WriteAllText(path, "{ corrupt");
            Assert.True(Darkmount.App.SettingsStore.Load(path).AlertsOptIn); // a later save must not switch alerts off again
        }
        finally { File.Delete(path); }
    }

    // ---------------------------------------------------------------- 1.5 effects

    [Fact]
    public void Demo_typing_includes_space_backspace_and_enter()
    {
        var keys = Enumerable.Range(0, 40).SelectMany(t => SceneDemo.Context(t * 0.5, Lamps).KeyPresses).Select(p => p.KeyId).ToHashSet();

        Assert.Contains(KeyWithUsage(0x2C), keys);
        Assert.Contains(KeyWithUsage(0x2A), keys);
        Assert.Contains(KeyWithUsage(0x28), keys);
    }

    [Fact]
    public void Enter_bursts_and_flashes_the_frame_while_letters_only_glow_softly()
    {
        int enter = KeyWithUsage(0x28), letter = KeyWithUsage(0x04);
        var afterEnter = Render(SceneEffect.WordWaves, Ctx(10.05, new KeyPress(enter, 10)));
        var afterLetter = Render(SceneEffect.WordWaves, Ctx(10.05, new KeyPress(letter, 10)));

        Assert.True(Total(afterEnter, id => id >= 500) > 0);
        Assert.Equal(0, Total(afterLetter, id => id >= 500));
        Assert.True(Level(afterLetter[letter]) > 0);
    }

    [Fact]
    public void Space_wave_travels_sideways()
    {
        int space = KeyWithUsage(0x2C), leftCtrl = KeyWithUsage(0xE0);
        int At(double t) => Level(Render(SceneEffect.WordWaves, Ctx(20 + t, new KeyPress(space, 20)))[leftCtrl]);

        Assert.True(At(0.02) < 20);
        Assert.True(Enumerable.Range(1, 30).Max(i => At(i * 0.02)) > 150, "the wave reaches the far end of the bottom row");
    }

    [Fact]
    public void Typing_snake_connects_the_keys_you_type()
    {
        int a = KeyWithUsage(0x04), l = KeyWithUsage(0x0F), s = KeyWithUsage(0x16), f = KeyWithUsage(0x09);
        var frame = Render(SceneEffect.TypingSnake, Ctx(5, new KeyPress(a, 4.5), new KeyPress(l, 4.8)));

        Assert.True(Level(frame[a]) > 0 && Level(frame[l]) > 0);
        Assert.True(Level(frame[s]) > 0 && Level(frame[f]) > 0, "keys on the path between them glow too");
        Assert.All(Render(SceneEffect.TypingSnake, Ctx(9, new KeyPress(a, 4.5), new KeyPress(l, 4.8))).Values, c => Assert.Equal(0, Level(c)));
    }

    [Fact]
    public void Holding_one_key_keeps_the_snake_awake()
    {
        int a = KeyWithUsage(0x04), bs = KeyWithUsage(0x2A);
        var presses = new[] { new KeyPress(a, 0.5) }.Concat(Enumerable.Range(0, 60).Select(i => new KeyPress(bs, 1 + i * 0.05))).ToArray();
        var frame = Render(SceneEffect.TypingSnake, Ctx(3.95, [.. presses.Where(p => p.At > -0.05)]));

        Assert.True(Level(frame[bs]) > 0);
    }

    [Fact]
    public void Drops_fall_down_and_splash_on_the_bottom_edge()
    {
        int q = KeyWithUsage(0x14), a = KeyWithUsage(0x04), z = KeyWithUsage(0x1D);
        var start = Render(SceneEffect.KeyDrops, Ctx(1.0, new KeyPress(q, 1)));
        var falling = Render(SceneEffect.KeyDrops, Ctx(1.25, new KeyPress(q, 1)));
        Assert.True(Level(start[q]) > Level(start[z]));
        Assert.True(Level(falling[a]) + Level(falling[z]) > Level(start[a]) + Level(start[z]));

        bool splashed = Enumerable.Range(1, 40).Any(i => Total(Render(SceneEffect.KeyDrops, Ctx(1 + i * 0.05, new KeyPress(q, 1))),
            id => id >= 500 && Lamps.First(p => p.LampId == id).Y > 0.9) > 0);
        Assert.True(splashed);
    }

    [Fact]
    public void Paint_splashes_dry_out_within_four_seconds_even_at_speed_one()
    {
        int key = KeyWithUsage(0x0B);
        Assert.True(Level(Render(SceneEffect.PaintSplash, Ctx(3.2, new KeyPress(key, 3)), speed: 1)[key]) > 0);
        Assert.All(Render(SceneEffect.PaintSplash, Ctx(6.9, new KeyPress(key, 3)), speed: 1).Values, c => Assert.Equal(0, Level(c)));
    }

    [Fact]
    public void Day_and_night_is_dim_at_night_and_bright_at_noon()
    {
        double At(double hours) => Total(SceneRenderer.Render(new LightingScene { Layers = [SceneEffects.CreateLayer(SceneEffect.DayNight)] },
            new SceneContext { Seconds = 1, Lamps = Lamps, LocalHours = hours }), _ => true);

        Assert.True(At(2) * 2 < At(13));
        Assert.Equal(At(12), Total(Render(SceneEffect.DayNight, Ctx(1)), _ => true)); // unknown time = noon
        Assert.True(At(23.99) > 0 && Math.Abs(At(23.99) - At(0.01)) < At(0.01) * 0.05, "no jump at midnight");
    }

    [Fact]
    public void Vu_meter_grows_with_the_music()
    {
        double With(double level) => Total(SceneRenderer.Render(new LightingScene { Layers = [SceneEffects.CreateLayer(SceneEffect.VuMeter)] },
            new SceneContext { Seconds = 1, Lamps = Lamps, AudioLevel = level, AudioBands = [.. Enumerable.Repeat(level, 8)] }), _ => true);

        Assert.True(With(0.1) < With(0.5) && With(0.5) < With(0.95));
    }

    // ---------------------------------------------------------------- IO Center autostart

    [Fact]
    public void Startup_switch_bytes_match_task_manager()
    {
        Assert.False(StartupApproval.IsDisabled((byte[]?)null));
        Assert.False(StartupApproval.IsDisabled(StartupApproval.EnabledValue()));
        Assert.True(StartupApproval.IsDisabled([3, 0, 0, 0, 0x2D, 0x99, 0x5C, 0x18, 0xE1, 0x50, 0xDD, 0x01]));
        var off = StartupApproval.DisabledValue(new DateTime(2026, 9, 30, 12, 0, 0, DateTimeKind.Utc));
        Assert.Equal(12, off.Length);
        Assert.True(StartupApproval.IsDisabled(off));
        Assert.Equal(new DateTime(2026, 9, 30, 12, 0, 0, DateTimeKind.Utc), DateTime.FromFileTimeUtc(BitConverter.ToInt64(off, 4)));
    }

    [Fact]
    public void Io_center_autostart_is_turned_off_like_task_manager_does_and_its_run_entry_stays()
    {
        var path = $@"Software\OverMountTests\{Guid.NewGuid():N}";
        using var hive = Registry.CurrentUser.CreateSubKey(path);
        try
        {
            var io = new IoCenterAutostart(hive);
            Assert.False(io.IsEnabled());

            using (var run = hive.CreateSubKey(StartupApproval.RunKey))
            {
                run.SetValue("IO Center", @"C:\Program Files\IO Center\IO_Center.exe --minimize");
                run.SetValue("Something else", @"C:\Tools\other.exe");
            }
            Assert.Equal(["IO Center"], io.Entries());
            Assert.True(io.IsEnabled());

            Assert.True(io.Disable());
            Assert.False(io.IsEnabled());
            using (var run = hive.OpenSubKey(StartupApproval.RunKey)!) Assert.NotNull(run.GetValue("IO Center"));
            using (var approved = hive.OpenSubKey(StartupApproval.ApprovedRunKey)!)
            {
                Assert.Equal(3, ((byte[])approved.GetValue("IO Center")!)[0]);
                Assert.Null(approved.GetValue("Something else"));
            }

            StartupApproval.SetEnabled(hive, "Something else", true); // already enabled: nothing written
            using (var approved = hive.OpenSubKey(StartupApproval.ApprovedRunKey)!) Assert.Null(approved.GetValue("Something else"));
        }
        finally { Registry.CurrentUser.DeleteSubKeyTree(path, throwOnMissingSubKey: false); }
    }
}
