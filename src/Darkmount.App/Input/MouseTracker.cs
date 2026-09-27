using System.Runtime.InteropServices;
using Darkmount.Keyboard.Lamps;

namespace Darkmount.App.Input;

/// <summary>
/// Where the mouse pointer is across all screens (0..1) and recent clicks, for the Mouse spotlight effect. Polled once per
/// lighting frame, and only while a layer uses it; nothing is stored beyond the last few clicks or sent anywhere.
/// </summary>
public sealed class MouseTracker
{
    readonly Queue<MouseClick> _clicks = new();
    readonly bool[] _down = new bool[3];

    public double? X { get; private set; }
    public double? Y { get; private set; }
    public IReadOnlyList<MouseClick> Clicks => [.. _clicks];

    public void Poll(double seconds)
    {
        if (!GetCursorPos(out var pt)) { X = Y = null; return; }
        var screens = SystemInformation.VirtualScreen;
        if (screens.Width <= 0 || screens.Height <= 0) return;
        X = Math.Clamp((pt.X - screens.Left) / (double)screens.Width, 0, 1);
        Y = Math.Clamp((pt.Y - screens.Top) / (double)screens.Height, 0, 1);

        int[] buttons = [0x01, 0x02, 0x04]; // left, right, middle
        for (int i = 0; i < buttons.Length; i++)
        {
            bool down = (GetAsyncKeyState(buttons[i]) & 0x8000) != 0;
            if (down && !_down[i]) _clicks.Enqueue(new MouseClick(X.Value, Y.Value, i, seconds));
            _down[i] = down;
        }
        while (_clicks.Count > 0 && (seconds - _clicks.Peek().At > 2 || _clicks.Count > 12)) _clicks.Dequeue();
    }

    [StructLayout(LayoutKind.Sequential)] struct Point { public int X, Y; }
    [DllImport("user32.dll")] static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")] static extern short GetAsyncKeyState(int key);
}
