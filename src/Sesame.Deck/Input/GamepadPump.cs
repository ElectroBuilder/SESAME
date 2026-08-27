using System.Diagnostics;
using Avalonia.Threading;

namespace Sesame.Deck.Input;

public enum PadAction
{
    Up, Down, Left, Right, Confirm, Back, Menu, PrevTab, NextTab
}

/// <summary>
/// Keyboard always works (Steam Input can map the Deck controls to keys).
/// On Linux, /dev/input/js0 is also read when Steam is not grabbing the pad.
/// </summary>
public sealed class GamepadPump : IDisposable
{
    private readonly Action<PadAction> _onAction;
    private readonly CancellationTokenSource _cts = new();
    private DateTime _lastAxis;

    public GamepadPump(Action<PadAction> onAction)
    {
        _onAction = onAction;
        if (OperatingSystem.IsLinux())
            _ = Task.Run(() => ReadJoystick(_cts.Token));
    }

    public static bool TryKey(Avalonia.Input.Key key, out PadAction action)
    {
        action = key switch
        {
            Avalonia.Input.Key.Up or Avalonia.Input.Key.W => PadAction.Up,
            Avalonia.Input.Key.Down or Avalonia.Input.Key.S => PadAction.Down,
            Avalonia.Input.Key.Left or Avalonia.Input.Key.A => PadAction.Left,
            Avalonia.Input.Key.Right or Avalonia.Input.Key.D => PadAction.Right,
            Avalonia.Input.Key.Enter or Avalonia.Input.Key.Space or Avalonia.Input.Key.OemPlus => PadAction.Confirm,
            Avalonia.Input.Key.Escape or Avalonia.Input.Key.Back => PadAction.Back,
            Avalonia.Input.Key.Tab => PadAction.NextTab,
            Avalonia.Input.Key.Oem3 or Avalonia.Input.Key.F1 => PadAction.Menu,
            Avalonia.Input.Key.Q or Avalonia.Input.Key.PageUp => PadAction.PrevTab,
            Avalonia.Input.Key.E or Avalonia.Input.Key.PageDown => PadAction.NextTab,
            _ => (PadAction)(-1)
        };
        return (int)action >= 0;
    }

    private void ReadJoystick(CancellationToken ct)
    {
        foreach (var path in new[] { "/dev/input/js0", "/dev/input/js1" })
        {
            try
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                var buf = new byte[8];
                while (!ct.IsCancellationRequested)
                {
                    var n = fs.Read(buf, 0, 8);
                    if (n < 8) break;
                    var value = BitConverter.ToInt16(buf, 4);
                    var type = buf[6];
                    var number = buf[7];
                    PadAction? action = null;
                    if ((type & 0x01) != 0 && value != 0)
                    {
                        action = number switch
                        {
                            0 => PadAction.Confirm,
                            1 => PadAction.Back,
                            6 => PadAction.Menu,
                            7 => PadAction.Menu,
                            4 => PadAction.PrevTab,
                            5 => PadAction.NextTab,
                            _ => null
                        };
                    }
                    else if ((type & 0x02) != 0)
                    {
                        if (DateTime.UtcNow - _lastAxis < TimeSpan.FromMilliseconds(180)) continue;
                        if (number is 0 or 6)
                        {
                            if (value > 16000) action = PadAction.Right;
                            else if (value < -16000) action = PadAction.Left;
                        }
                        else if (number is 1 or 7)
                        {
                            if (value > 16000) action = PadAction.Down;
                            else if (value < -16000) action = PadAction.Up;
                        }
                        if (action is not null) _lastAxis = DateTime.UtcNow;
                    }
                    if (action is PadAction a)
                        Dispatcher.UIThread.Post(() => _onAction(a));
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("joystick " + path + ": " + ex.Message);
            }
        }
    }

    public void Dispose() => _cts.Cancel();
}
