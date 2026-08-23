using System.Runtime.InteropServices;
using static UnnamedGame.Platform.Win32;

namespace UnnamedGame.Platform;

/// <summary>Bare Win32 window with raw-mouse input and a keyboard state table.</summary>
public sealed class GameWindow
{
    private readonly WndProc _proc;   // kept alive: the OS holds a raw pointer to it
    private readonly bool[] _down = new bool[256];
    private readonly bool[] _pressed = new bool[256];

    public IntPtr Handle { get; private set; }
    public int Width { get; private set; }
    public int Height { get; private set; }
    public bool IsClosed { get; private set; }
    public bool Resized { get; private set; }
    public bool MouseCaptured { get; private set; }

    public float MouseDeltaX { get; private set; }
    public float MouseDeltaY { get; private set; }
    private float _accumX, _accumY;
    private bool _lmbDown, _lmbPressed;

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

    public bool IsKeyDown(int vk) => _down[vk & 0xFF];
    public bool WasKeyPressed(int vk) => _pressed[vk & 0xFF];
    public bool WasMousePressed() => _lmbPressed;

    public void PumpEvents()
    {
        Array.Clear(_pressed);
        _lmbPressed = false;
        Resized = false;
        _accumX = _accumY = 0;

        while (PeekMessage(out var msg, IntPtr.Zero, 0, 0, PM_REMOVE))
        {
            TranslateMessage(ref msg);
            DispatchMessage(ref msg);
        }

        MouseDeltaX = _accumX;
        MouseDeltaY = _accumY;
        if (MouseCaptured) ConfineCursor();
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

            case WM_KILLFOCUS:
                Array.Clear(_down);
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

            case WM_KEYUP:
            case WM_SYSKEYUP:
                _down[(int)wParam & 0xFF] = false;
                return IntPtr.Zero;

            case WM_LBUTTONDOWN:
                if (!_lmbDown) _lmbPressed = true;
                _lmbDown = true;
                return IntPtr.Zero;

            case WM_LBUTTONUP:
                _lmbDown = false;
                return IntPtr.Zero;

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
