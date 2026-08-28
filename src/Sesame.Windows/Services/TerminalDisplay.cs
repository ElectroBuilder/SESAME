using System.Text;

namespace Sesame.Services;

public sealed class TerminalDisplay
{
    private readonly StringBuilder _buf = new();
    private int _lineStart;
    private bool _pendingCr;
    private AnsiState _ansi;
    private int _ansiHold;
    private const int Cap = 250_000;
    private const int AnsiMax = 96;

    private enum AnsiState
    {
        Ground,
        Esc,
        Csi,
        Osc,
        OscEsc,
        Charset,
        Ss3,
        MaybeCsi
    }

    public string Text => _buf.ToString();

    public void Clear()
    {
        _buf.Clear();
        _lineStart = 0;
        _pendingCr = false;
        _ansi = AnsiState.Ground;
        _ansiHold = 0;
    }

    public void Write(string raw)
    {
        if (string.IsNullOrEmpty(raw)) return;
        foreach (var c in raw)
            Feed(c);
        if (_buf.Length > Cap)
        {
            var drop = _buf.Length - Cap + 50_000;
            _buf.Remove(0, drop);
            var nl = _buf.ToString().LastIndexOf('\n');
            _lineStart = nl < 0 ? 0 : nl + 1;
        }
    }

    private void Feed(char c)
    {
        switch (_ansi)
        {
            case AnsiState.Ground:
                if (c == '\u001b' || c == '\u009b' || c == '\u009d')
                {
                    _ansi = c == '\u009d' ? AnsiState.Osc : c == '\u009b' ? AnsiState.Csi : AnsiState.Esc;
                    _ansiHold = 0;
                    return;
                }
                if (c == '[')
                {
                    _ansi = AnsiState.MaybeCsi;
                    return;
                }
                Put(c);
                return;

            case AnsiState.MaybeCsi:
                if (c == '?' || char.IsAsciiDigit(c))
                {
                    _ansi = AnsiState.Csi;
                    _ansiHold = 2;
                    return;
                }
                _ansi = AnsiState.Ground;
                Put('[');
                Feed(c);
                return;

            case AnsiState.Esc:
                _ansiHold = 0;
                _ansi = c switch
                {
                    '[' => AnsiState.Csi,
                    ']' => AnsiState.Osc,
                    'P' or 'X' or '^' or '_' => AnsiState.Osc,
                    '(' or ')' or '*' or '+' => AnsiState.Charset,
                    'O' => AnsiState.Ss3,
                    _ => AnsiState.Ground
                };
                return;

            case AnsiState.Csi:
                _ansiHold++;
                if (c >= '@' && c <= '~')
                    _ansi = AnsiState.Ground;
                else if (_ansiHold > AnsiMax || c < ' ')
                    _ansi = AnsiState.Ground;
                return;

            case AnsiState.Osc:
                _ansiHold++;
                if (c == '\u0007' || _ansiHold > 512)
                    _ansi = AnsiState.Ground;
                else if (c == '\u001b')
                    _ansi = AnsiState.OscEsc;
                return;

            case AnsiState.OscEsc:
                _ansi = AnsiState.Ground;
                return;

            case AnsiState.Charset:
            case AnsiState.Ss3:
                _ansi = AnsiState.Ground;
                return;
        }
    }

    private void Put(char c)
    {
        switch (c)
        {
            case '\a':
                return;
            case '\r':
                _pendingCr = true;
                return;
            case '\n':
                _pendingCr = false;
                _buf.Append('\n');
                _lineStart = _buf.Length;
                return;
            case '\b':
                if (_pendingCr)
                {
                    _buf.Length = _lineStart;
                    _pendingCr = false;
                }
                if (_buf.Length > _lineStart)
                    _buf.Length--;
                return;
            default:
                if (char.IsControl(c)) return;
                if (_pendingCr)
                {
                    _buf.Length = _lineStart;
                    _pendingCr = false;
                }
                _buf.Append(c);
                return;
        }
    }
}
