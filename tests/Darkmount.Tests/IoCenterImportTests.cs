using System.IO.Compression;
using System.Text.Json.Nodes;
using Darkmount.App;
using Darkmount.App.IoCenter;
using Darkmount.App.Macros;
using Darkmount.Keyboard;
using Darkmount.Keyboard.Lamps;
using Darkmount.QLink;
using SkiaSharp;

namespace Darkmount.Tests;

public class IoCenterImportTests
{
    const string Rainbow = """
        [{"first":"#ffff0000","second":0.0},{"first":"#ffffff00","second":0.5},{"first":"#ff0000ff","second":1.0}]
        """;

    /// <summary>A profile shaped like IO Center's own files (cereal JSON), with made-up content.</summary>
    static string ProfileJson(string mode = "Custom", string imageSource = "file:///C:/nowhere/pic.ioasset", string linkedApp = "") =>
        Template.Replace("@APP@", linkedApp).Replace("@MODE@", mode).Replace("@RAINBOW@", Rainbow).Replace("@IMAGE@", imageSource);

    const string Template = """
        {"data":{"cereal_class_version":1000,"id":"{00000000-0000-0000-0000-000000000001}","name":"Test profile","linkedAppPath":"@APP@",
         "disabledDevices":[],"properties":[
          {"polymorphic_id":2147483649,"polymorphic_name":"LightingsProperty","ptr_wrapper":{"id":1,"data":{"subs":[{"key":"Dark Mount","value":
            {"polymorphic_id":1073741824,"ptr_wrapper":{"id":2,"data":{"enabled":true,"mode":"@MODE@",
              "generalModeData":{"data":[{"impl":{"index":11,"data":{"direction":"Clockwise","colorMode":"Gradient","singleColor":"#ffff2800",
                  "dualColors":{"first":"#ff00ff00","second":"#ffffffff"},"gradient":@RAINBOW@,"speed":30,"brightness":80}}}],
                "groupType":"All","zoneType":"AllZones"},
              "customModeData":{"layers":[
                {"id":"a","name":"Edges","assignments":["Led_KeyboardTop1","Led_KeyboardRight1","Led_NumpadLeft11"],
                 "effectData":{"nullopt":false,"data":{"impl":{"index":11,"data":{"direction":"Counterclockwise","colorMode":"Gradient",
                   "gradient":@RAINBOW@,"speed":10,"brightness":100}}}}},
                {"id":"b","name":"Typing","assignments":["Key_A","Key_Space","Key_Numpad5","Key_Fn"],
                 "effectData":{"nullopt":false,"data":{"impl":{"index":6,"data":{"colorMode":"Dual",
                   "dualColors":{"first":"#ff112233","second":"#ff445566"},"speed":50,"brightness":90}}}}},
                {"id":"c","name":"Video","assignments":["Key_Q"],"effectData":{"nullopt":false,"data":{"impl":{"index":12,"data":{}}}}},
                {"id":"d","name":"Empty","assignments":["Key_Q"],"effectData":{"nullopt":true}},
                {"id":"e","name":"WASD","assignments":["Key_W","Key_A","Key_S","Key_D"],
                 "effectData":{"nullopt":false,"data":{"impl":{"index":10,"data":{"color":"#ffff1493","brightness":70}}}}}
              ]}}}}}]}}},
          {"polymorphic_id":2147483650,"polymorphic_name":"KeyBindingsProperty","ptr_wrapper":{"id":3,"data":{"subs":[{"key":"Dark Mount","value":
            {"ptr_wrapper":{"id":4,"data":{"enabled":true,"layerBindingsData":[
              {"key":"Common","value":[
                {"key":"Key_NumpadImage0","value":{"impl":{"index":7,"data":{"url":"file:///C:/Tools/App.exe"}}}},
                {"key":"Key_NumpadImage1","value":{"impl":{"index":7,"data":{"url":""}}}},
                {"key":"Key_NumpadImage2","value":{"impl":{"index":10,"data":{"action":"TaskManager"}}}},
                {"key":"Key_NumpadImage3","value":{"impl":{"index":9,"data":{"url":"https://example.com/"}}}},
                {"key":"Key_Mute","value":{"impl":{"index":4,"data":{"action":"Mute","audioDeviceId":""}}}},
                {"key":"Key_CapsLock","value":{"impl":{"index":1,"data":{}}}},
                {"key":"Key_F1","value":{"impl":{"index":13,"data":{"macro":"x"}}}}]},
              {"key":"Fn1","value":[
                {"key":"Key_PageUp","value":{"impl":{"index":4,"data":{"action":"IncreaseVolume"}}}},
                {"key":"Key_UpArrow","value":{"impl":{"index":12,"data":{"action":"IncreaseBrightness","effect":"None"}}}}]}],
              "fnLayerColors":[]}}}}]}}},
          {"polymorphic_id":2147483651,"polymorphic_name":"NumpadProperty","ptr_wrapper":{"id":5,"data":{"subs":[{"key":"Dark Mount","value":
            {"ptr_wrapper":{"id":6,"data":{"images":[
              {"key":"Key_NumpadImage4","value":{"impl":{"index":0,"data":{"source":"@IMAGE@",
                "cropRect":{"x":0.25,"y":0.0,"width":0.5,"height":1.0}}}}},
              {"key":"Key_NumpadImage5","value":{"impl":{"index":0,"data":{"source":"qrc:/builtin/0.ioasset"}}}}]}}}}]}}}
         ]}}
        """;

    static byte[] Png(int w, int h)
    {
        using var bmp = new SKBitmap(w, h);
        bmp.Erase(SKColors.Orange);
        using var data = SKImage.FromBitmap(bmp).Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    static IoCenterImportResult Import(string json, Func<string, byte[]?>? asset = null) =>
        IoCenterImport.Parse(json, KeyboardModel.DarkMount, asset ?? (_ => null), n => 1000 + n);

    [Fact]
    public void Custom_layers_become_a_studio_scene_in_the_same_order()
    {
        var r = Import(ProfileJson());
        var scene = r.Profile.Scene!;

        Assert.True(r.Profile.RgbEnabled);
        Assert.Equal(["Edges", "Typing", "WASD"], scene.Layers.Select(l => l.Name)); // unsupported and empty layers dropped
        var edges = scene.Layers[0];
        Assert.Equal(SceneEffect.Tornado, edges.Effect);
        Assert.Equal(SceneDirection.CounterClockwise, edges.Direction);
        Assert.Equal(SceneColorMode.Gradient, edges.ColorMode);
        Assert.Equal(["FF0000", "FFFF00", "0000FF"], edges.Colors);
        Assert.Equal(1, edges.Speed);
        Assert.Empty(edges.Keys);
        Assert.Equal([1000 + 2, 1000 + 23, 1000 + 65], edges.EdgeLamps); // Top1, Right1 (top-right corner), NumpadLeft11 (ring start)

        var typing = scene.Layers[1];
        Assert.Equal(SceneEffect.Reactive, typing.Effect);
        Assert.Equal(SceneColorMode.Dual, typing.ColorMode);
        Assert.Equal(["112233", "445566"], typing.Colors);
        Assert.Equal(5, typing.Speed);
        Assert.Equal(90, typing.Brightness);
        Assert.Equal(new[] { 30, 55, 57, 76 }, typing.Keys); // A, Fn, Space, Numpad 5

        var wasd = scene.Layers[2];
        Assert.Equal(SceneEffect.Static, wasd.Effect);
        Assert.Equal(["FF1493"], wasd.Colors);
        Assert.Equal(70, wasd.Brightness);
        Assert.Contains(r.Notes, n => n.Contains("Video"));
    }

    [Fact]
    public void The_general_effect_becomes_the_keyboards_own_effect()
    {
        var r = Import(ProfileJson(mode: "General"));

        Assert.False(r.Profile.RgbEnabled);
        Assert.Equal(LightingMode.General, r.Profile.Keyboard.LightingMode);
        var c = r.Profile.Keyboard.LayerConfig!;
        Assert.Equal(Effect.Tornado, c.Effect);
        Assert.Equal(Direction.Clockwise, c.Direction);
        Assert.Equal(ColorMode.Gradient, c.ColorMode);
        Assert.Equal(80, c.Brightness);
        Assert.Equal(30, c.Speed);
        Assert.Equal([0, 50, 100], c.Colors.Select(s => s.Position));
        c.Encode(Lighting.TopLayer); // valid for the keyboard
        Assert.NotNull(r.Profile.Scene); // the custom layers stay available in the studio
    }

    [Fact]
    public void Key_bindings_map_to_keyboard_bindings_and_launchers()
    {
        var r = Import(ProfileJson());
        var b = r.Profile.Keyboard.Bindings!.ToDictionary(x => (x.KeyId, x.Layer), x => x.Action);

        Assert.Equal(new BindingAction.WindowsShortcut(WindowsShortcutAction.TaskManager), b[(KeyIds.DisplayKey1 + 2, Layer.Common)]);
        Assert.Equal(new BindingAction.Media(MediaAction.Mute), b[(KeyIds.DockMute, Layer.Common)]);
        Assert.Equal(new BindingAction.Disabled(), b[(KeyIds.CapsLock, Layer.Common)]);
        Assert.Equal(new BindingAction.Media(MediaAction.VolumeUp), b[(67, Layer.Fn)]);
        Assert.Equal(new BindingAction.Backlight(BacklightAction.IncreaseBrightness), b[(KeyIds.Up, Layer.Fn)]);
        Assert.False(b.ContainsKey((KeyIds.DisplayKey1 + 1, Layer.Common))); // empty "open file" slot: nothing
        Assert.False(b.ContainsKey((KeyIds.F1, Layer.Common))); // IO Center macro: skipped with a note
        Assert.Contains(r.Notes, n => n.Contains("1 key binding"));

        Assert.Equal(2, r.Launchers.Count);
        Assert.Equal(new LaunchProgramStep(@"C:\Tools\App.exe"), r.Launchers[0].Step);
        Assert.Equal(KeyIds.DisplayKey1, r.Launchers[0].KeyId);
        Assert.Equal(new OpenUrlStep("https://example.com/"), r.Launchers[1].Step);
    }

    [Fact]
    public void Launchers_become_macros_with_free_triggers_bound_to_the_keys()
    {
        var existing = new Macro { Name = "Mine", Trigger = new MacroTrigger(TriggerKey.F13), Steps = [new DelayStep(10)] };
        var macros = new List<Macro> { existing };
        var r = Import(ProfileJson());

        var notes = IoCenterMacros.Attach(r, macros);

        Assert.Equal(3, macros.Count);
        Assert.Equal(new MacroTrigger(TriggerKey.F14), macros[1].Trigger);
        Assert.Equal(new MacroTrigger(TriggerKey.F15), macros[2].Trigger);
        Assert.Equal("Open App", macros[1].Name);
        var bound = r.Profile.Keyboard.Bindings!.Single(b => b.KeyId == KeyIds.DisplayKey1);
        Assert.Equal(new BindingAction.FKey(HidUsage.FKey(14)), bound.Action);
        Assert.Equal(2, notes.Count);

        // Importing again reuses the same macros instead of adding copies.
        var again = Import(ProfileJson());
        IoCenterMacros.Attach(again, macros);
        Assert.Equal(3, macros.Count);
    }

    [Fact]
    public void Launchers_ignore_any_keyboard_triggers_and_still_get_F13_to_F24()
    {
        var macros = new List<Macro> { new() { Name = "Mine", Trigger = new MacroTrigger(TriggerKey.K, Ctrl: true), Steps = [new DelayStep(10)] } };

        IoCenterMacros.Attach(Import(ProfileJson()), macros);

        Assert.Equal(new MacroTrigger(TriggerKey.F13), macros[1].Trigger);
        Assert.Equal(new MacroTrigger(TriggerKey.F14), macros[2].Trigger);
    }

    [Fact]
    public void Trigger_action_sends_Win_and_refuses_non_F13_to_F24_triggers()
    {
        Assert.Equal(new BindingAction.StandardKey(KeyModifiers.LeftWin | KeyModifiers.LeftCtrl, HidUsage.FKey(15)),
            IoCenterMacros.TriggerAction(new MacroTrigger(TriggerKey.F15, Ctrl: true, Win: true)));
        Assert.Throws<ArgumentException>(() => IoCenterMacros.TriggerAction(new MacroTrigger(TriggerKey.K, Ctrl: true)));
    }

    [Fact]
    public void Linked_app_becomes_the_profile_game()
    {
        var r = Import(ProfileJson(linkedApp: "C:/Games/Shooter/shooter.exe"));
        Assert.Equal(["shooter.exe"], r.Profile.Games);
    }

    [Fact]
    public void Display_key_pictures_are_cropped_and_converted_builtin_ones_skipped()
    {
        var png = Png(200, 100);
        var r = Import(ProfileJson(), url => url.EndsWith("pic.ioasset") ? png : null);

        var jpegs = r.Profile.Keyboard.DisplayKeyJpegs!;
        Assert.Equal([4], jpegs.Keys);
        using var decoded = DisplayKeys.DecodeStored(jpegs[4]);
        Assert.NotNull(decoded);
        Assert.Equal(DisplayKeys.ImageSize, decoded!.Width);
    }

    [Fact]
    public void Light_Mount_import_skips_display_keys_and_dock_buttons()
    {
        var r = IoCenterImport.Parse(ProfileJson(), KeyboardModel.LightMount, _ => Png(10, 10));

        Assert.Null(r.Profile.Keyboard.DisplayKeyJpegs);
        Assert.DoesNotContain(r.Profile.Keyboard.Bindings!, b => b.KeyId >= KeyIds.DisplayKey1);
        Assert.Empty(r.Launchers);
    }

    [Fact]
    public void Without_the_keyboard_whole_ring_selections_still_light_every_edge()
    {
        var names = new JsonArray();
        foreach (var side in new[] { ("KeyboardTop", 21), ("KeyboardRight", 11), ("KeyboardBottom", 21), ("KeyboardLeft", 11) })
            for (int i = 1; i <= side.Item2; i++) names.Add($"Led_{side.Item1}{i}");
        var json = ProfileJson().Replace("""["Led_KeyboardTop1","Led_KeyboardRight1","Led_NumpadLeft11"]""", names.ToJsonString());

        var r = IoCenterImport.Parse(json, KeyboardModel.DarkMount, _ => null, lampOfEdgeLight: null);

        Assert.True(r.Profile.Scene!.Layers[0].AllEdges);
    }

    [Fact]
    public void Exported_zip_profiles_load_with_their_pictures()
    {
        var path = Path.Combine(Path.GetTempPath(), $"dmh-{Guid.NewGuid():N}.ioprofile");
        try
        {
            using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
            {
                using (var w = new StreamWriter(zip.CreateEntry("data").Open()))
                    w.Write(ProfileJson(imageSource: "file:///C:/Users/someone/AppData/Roaming/be%20quiet!/IO%20Center/assets/numpad/abc-123.ioasset"));
                using var s = zip.CreateEntry("assets/numpad/abc-123").Open();
                s.Write(Png(64, 64));
            }
            var r = IoCenterImport.Load(path, KeyboardModel.DarkMount);

            Assert.Equal("Test profile", r.Profile.Name);
            Assert.True(r.Profile.Keyboard.DisplayKeyJpegs!.ContainsKey(4));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Installed_profiles_are_listed_by_name()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"dmh-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "a.ioprofile"), ProfileJson());
            File.WriteAllText(Path.Combine(dir, "broken.ioprofile"), "{ not json");
            var list = IoCenterImport.ListInstalled(dir);
            Assert.Equal(["Test profile"], list.Select(p => p.Name));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void Edge_led_names_cover_all_96_edge_lights_once()
    {
        var names = new List<string>();
        foreach (var (part, across) in new[] { ("Keyboard", 21), ("Numpad", 5) })
        {
            for (int i = 1; i <= across; i++) names.AddRange([$"Led_{part}Top{i}", $"Led_{part}Bottom{i}"]);
            for (int i = 1; i <= 11; i++) names.AddRange([$"Led_{part}Left{i}", $"Led_{part}Right{i}"]);
        }
        var numbers = names.Select(n => IoCenterNames.EdgeLightOf(n)!.Value).ToList();

        Assert.Equal(Enumerable.Range(1, 96), numbers.Order());
        Assert.Equal(2, IoCenterNames.EdgeLightOf("Led_KeyboardTop1"));
        Assert.Equal(1, IoCenterNames.EdgeLightOf("Led_KeyboardLeft11"));
        Assert.Equal(65, IoCenterNames.EdgeLightOf("Led_NumpadLeft11"));
        Assert.Null(IoCenterNames.EdgeLightOf("Led_KeyboardTop22"));
        Assert.Null(IoCenterNames.EdgeLightOf("Key_A"));
    }

    [Fact]
    public void Key_names_map_to_dark_mount_key_ids()
    {
        Assert.Equal((byte?)30, IoCenterNames.KeyIdOf("Key_A"));
        Assert.Equal((byte?)1, IoCenterNames.KeyIdOf("Key_GraveAccentAndTilde"));
        Assert.Equal((byte?)103, IoCenterNames.KeyIdOf("Key_LeftGui"));
        Assert.Equal((byte?)104, IoCenterNames.KeyIdOf("Key_RightGui"));
        Assert.Equal((byte?)86, IoCenterNames.KeyIdOf("Key_NumpadEnter"));
        Assert.Equal((byte?)105, IoCenterNames.KeyIdOf("Key_NonUsBackslash"));
        Assert.Equal((byte?)(KeyIds.DisplayKey1 + 7), IoCenterNames.KeyIdOf("Key_NumpadImage7"));
        Assert.Equal((byte?)KeyIds.DockNext, IoCenterNames.KeyIdOf("Key_ScanNextTrack"));
        Assert.Null(IoCenterNames.KeyIdOf("Key_NumpadImage8"));
        Assert.Null(IoCenterNames.KeyIdOf("Key_Nonsense"));
    }
}
