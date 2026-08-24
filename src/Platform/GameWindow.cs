using System.Runtime.InteropServices;
using static UnnamedGame.Platform.Win32;

namespace UnnamedGame.Platform;

/// <summary>Bare Win32 window with raw-mouse input and a keyboard state table.</summary>
public sealed class GameWindow
{
    private readonly WndProc _proc;   // kept alive: the OS holds a raw pointer to it
    private readonly bool[] _down = new bool[256];
    private readonly bool[] _pressed = new bool[256];
    private readonly List<char> _typed = [];

    public IntPtr Handle { get; private set; }
    public int Width { get; private set; }
    public int Height { get; private set; }
    public bool IsClosed { get; private set; }
    public bool Resized { get; private set; }
    public bool MouseCaptured { get; private set; }

    public float MouseDeltaX { get; private set; }
    public float MouseDeltaY { get; private set; }

    /// <summary>Cursor position in client pixels, for the editor's viewport hit testing.</summary>
    public int MouseX { get; private set; }
    public int MouseY { get; private set; }

    /// <summary>Wheel notches this frame; positive is away from the user.</summary>
    public float WheelDelta { get; private set; }
    private float _accumX, _accumY;
    private readonly bool[] _buttonDown = new bool[3];
    private readonly bool[] _buttonPressed = new bool[3];
    private readonly bool[] _buttonReleased = new bool[3];
    private float _wheelAccum;

    public GameWindow(string title, int width, int height)
    {
        Width = width; Height = height;
        _proc = HandleMessage;
        var instance = GetModuleHandle(null);

        var wc = new WNDCLASSEX
        {
            cbSize = Marshal.SizeOf<WNDCLASSEX>(),
            style = CS_HREDRAW | CS_VREDRAW | CS_OWNDC,
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_proc),
            hInstance = instance,
            hCursor = LoadCursor(IntPtr.Zero, IDC_ARROW),
            lpszClassName = "UnnamedGameWindow",
        };
        if (RegisterClassEx(ref wc) == 0)
            throw new InvalidOperationException($"RegisterClassEx failed: {Marshal.GetLastWin32Error()}");

        var rect = new RECT { right = width, bottom = height };
        AdjustWindowRect(ref rect, WS_OVERLAPPEDWINDOW, false);

        Handle = CreateWindowEx(0, "UnnamedGameWindow", title, WS_OVERLAPPEDWINDOW,
            100, 100, rect.right - rect.left, rect.bottom - rect.top,
            IntPtr.Zero, IntPtr.Zero, instance, IntPtr.Zero);
        if (Handle == IntPtr.Zero)
            throw new InvalidOperationException($"CreateWindowEx failed: {Marshal.GetLastWin32Error()}");

        ShowWindow(Handle, SW_SHOW);

        // Usage page 1 / usage 2 = generic desktop mouse.
        var rid = new[] { new RAWINPUTDEVICE { usUsagePage = 1, usUsage = 2, dwFlags = 0, hwndTarget = Handle } };
        RegisterRawInputDevices(rid, 1, Marshal.SizeOf<RAWINPUTDEVICE>());
    }

    /// <summary>Characters typed this frame, from WM_CHAR: already layout- and repeat-aware.</summary>
    public IReadOnlyList<char> TypedCharacters => _typed;

    public bool IsKeyDown(int vk) => _down[vk & 0xFF];
    public bool WasKeyPressed(int vk) => _pressed[vk & 0xFF];

    /// <summary>0 = left, 1 = right, 2 = middle.</summary>
    public bool IsMouseDown(int button) => _buttonDown[button];
    public bool WasMousePressed(int button) => _buttonPressed[button];
    public bool WasMouseReleased(int button) => _buttonReleased[button];
    public bool WasMousePressed() => _buttonPressed[0];

    public void PumpEvents()
    {
        Array.Clear(_pressed);
        Array.Clear(_buttonPressed);
        Array.Clear(_buttonReleased);
        _typed.Clear();
        Resized = false;
        _accumX = _accumY = 0;
        _wheelAccum = 0;

        while (PeekMessage(out var msg, IntPtr.Zero, 0, 0, PM_REMOVE))
        {
            TranslateMessage(ref msg);
            DispatchMessage(ref msg);
        }

        MouseDeltaX = _accumX;
        MouseDeltaY = _accumY;
        WheelDelta = _wheelAccum;
        if (MouseCaptured) ConfineCursor();
    }

    /// <summary>Puts a new caption on the window; the editor shows the open file there.</summary>
    public void SetTitle(string title) => Win32.SetWindowText(Handle, title);

    /// <summary>Takes back a close request, so an editor can ask about unsaved work first.</summary>
    public void CancelClose() => IsClosed = false;

    /// <summary>Asks the game loop to shut down (the console "quit" command).</summary>
    public void Close()
    {
        IsClosed = true;
        SetMouseCapture(false);
    }

    public void SetMouseCapture(bool captured)
    {
        if (captured == MouseCaptured) return;
        MouseCaptured = captured;
        ShowCursor(!captured);
        if (captured) ConfineCursor(); else ClipCursor(IntPtr.Zero);
    }

    private void ConfineCursor()
    {
        GetClientRect(Handle, out var r);
        var tl = new POINT { x = r.left, y = r.top };
        var br = new POINT { x = r.right, y = r.bottom };
        ClientToScreen(Handle, ref tl);
        ClientToScreen(Handle, ref br);
        var screen = new RECT { left = tl.x, top = tl.y, right = br.x, bottom = br.y };
        ClipCursor(ref screen);
    }

    private IntPtr ButtonDown(int button)
    {
        if (!_buttonDown[button]) _buttonPressed[button] = true;
        _buttonDown[button] = true;
        return IntPtr.Zero;
    }

    private IntPtr ButtonUp(int button)
    {
        if (_buttonDown[button]) _buttonReleased[button] = true;
        _buttonDown[button] = false;
        return IntPtr.Zero;
    }

    private unsafe IntPtr HandleMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case WM_CLOSE:
            case WM_DESTROY:
                IsClosed = true;
                SetMouseCapture(false);
                PostQuitMessage(0);
                return IntPtr.Zero;

            case WM_SIZE:
            {
                int w = (int)((long)lParam & 0xFFFF);
                int h = (int)(((long)lParam >> 16) & 0xFFFF);
                if (w > 0 && h > 0 && (w != Width || h != Height))
                {
                    Width = w; Height = h; Resized = true;
                }
                return IntPtr.Zero;
            }

            case WM_MOUSEMOVE:
                MouseX = (short)((long)lParam & 0xFFFF);
                MouseY = (short)(((long)lParam >> 16) & 0xFFFF);
                return IntPtr.Zero;

            case WM_MOUSEWHEEL:
                _wheelAccum += (short)(((long)wParam >> 16) & 0xFFFF) / 120f;
                return IntPtr.Zero;

            case WM_KILLFOCUS:
                Array.Clear(_down);
                Array.Clear(_buttonDown);
                SetMouseCapture(false);
                return IntPtr.Zero;

            case WM_KEYDOWN:
            case WM_SYSKEYDOWN:
            {
                int vk = (int)wParam & 0xFF;
                if (!_down[vk]) _pressed[vk] = true;
                _down[vk] = true;
                return IntPtr.Zero;
            }

            case WM_CHAR:
                _typed.Add((char)(int)wParam);
                return IntPtr.Zero;

            case WM_KEYUP:
            case WM_SYSKEYUP:
                _down[(int)wParam & 0xFF] = false;
                return IntPtr.Zero;

            case WM_LBUTTONDOWN: return ButtonDown(0);
            case WM_LBUTTONUP: return ButtonUp(0);
            case WM_RBUTTONDOWN: return ButtonDown(1);
            case WM_RBUTTONUP: return ButtonUp(1);
            case WM_MBUTTONDOWN: return ButtonDown(2);
            case WM_MBUTTONUP: return ButtonUp(2);

            case WM_INPUT:
            {
                RAWINPUTMOUSE raw;
                uint size = (uint)sizeof(RAWINPUTMOUSE);
                int headerSize = sizeof(uint) * 2 + IntPtr.Size * 2;
                if (GetRawInputData(lParam, RID_INPUT, (IntPtr)(&raw), ref size, headerSize) != unchecked((uint)-1)
                    && raw.dwType == 0)
                {
                    // Relative movement only; absolute devices (tablets) are ignored.
                    if ((raw.usFlags & 1) == 0)
                    {
                        _accumX += raw.lLastX;
                        _accumY += raw.lLastY;
                    }
                }
                return DefWindowProc(hwnd, msg, wParam, lParam);
            }
        }
        return DefWindowProc(hwnd, msg, wParam, lParam);
    }
}
