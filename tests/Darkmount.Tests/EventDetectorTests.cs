using Darkmount.App.Input;
using Darkmount.Keyboard.Lamps;

namespace Darkmount.Tests;

public class EventDetectorTests
{
    [Fact]
    public void A_steady_kick_drum_gives_one_beat_per_kick()
    {
        var detector = new BeatDetector();
        double[] quiet = [0.1, 0.1, 0.1], kick = [0.9, 0.85, 0.8];
        for (int frame = 0; frame < 90; frame++)                          // 3 s at 30 fps, a kick every 0.5 s
        {
            double t = frame / 30.0;
            bool onKick = frame % 15 == 0;
            detector.Feed(t, onKick ? kick : quiet, onKick ? 0.9 : 0.1);
        }
        Assert.InRange(detector.Beats.Count, 5, 6);
    }

    [Fact]
    public void Silence_and_steady_loudness_are_not_beats()
    {
        var detector = new BeatDetector();
        for (int frame = 0; frame < 90; frame++) detector.Feed(frame / 30.0, [0.7, 0.7, 0.7], 0.7);
        Assert.Empty(detector.Beats);
    }

    [Fact]
    public void A_sudden_bright_screen_is_one_flash_in_its_colour()
    {
        var detector = new FlashDetector();
        LampColor[] dark = [.. Enumerable.Repeat(new LampColor(20, 20, 30), 192)];
        LampColor[] boom = [.. Enumerable.Repeat(new LampColor(255, 180, 60), 192)];
        for (int i = 0; i < 10; i++) detector.Feed(i * 0.08, dark);
        detector.Feed(0.8, boom);
        detector.Feed(0.88, boom);                                        // still bright: the same flash
        var flash = Assert.Single(detector.Flashes);
        Assert.Equal(0.8, flash.At);
        Assert.Equal(new LampColor(255, 180, 60), flash.Color);
    }
}
