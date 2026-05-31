using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using ImGuiNET;
using Veldrid;
using Veldrid.Sdl2;
using Veldrid.StartupUtilities;
using FarFarWestTool.Core;
using FarFarWestTool.Features;

namespace FarFarWestTool.Overlay;

// ─────────────────────────────────────────────────────────────────────────────
// Win32 — fenêtre toujours au-dessus
// ─────────────────────────────────────────────────────────────────────────────

internal static class Win32Overlay
{
    // Window styles
    public const int GWL_STYLE        = -16;
    public const int GWL_EXSTYLE      = -20;
    public const int WS_POPUP         = unchecked((int)0x80000000);
    public const int WS_VISIBLE       = 0x10000000;
    public const int WS_EX_LAYERED    = 0x00080000;
    public const int WS_EX_TOOLWINDOW = 0x00000080;
    public const int WS_EX_NOACTIVATE = 0x08000000;

    public static readonly IntPtr HWND_TOPMOST = new(-1);

    public const uint SWP_NOMOVE      = 0x0002;
    public const uint SWP_NOSIZE      = 0x0001;
    public const uint SWP_NOACTIVATE  = 0x0010;
    public const uint SWP_FRAMECHANGED = 0x0020;

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT    { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    public struct MARGINS { public int Left, Right, Top, Bottom; }

    [DllImport("user32.dll")] public static extern int    GetWindowLong(IntPtr hWnd, int nIndex);
    [DllImport("user32.dll")] public static extern int    SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
    [DllImport("user32.dll")] public static extern bool   SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);
    [DllImport("user32.dll")] public static extern bool   GetWindowRect(IntPtr hWnd, out RECT lpRect);
    [DllImport("user32.dll")] public static extern IntPtr FindWindow(string? lpClassName, string lpWindowName);
    [DllImport("dwmapi.dll")] public static extern int    DwmExtendFrameIntoClientArea(IntPtr hWnd, ref MARGINS pMarInset);
}

// ─────────────────────────────────────────────────────────────────────────────
// OverlayWindow
// ─────────────────────────────────────────────────────────────────────────────

public sealed class OverlayWindow : IDisposable
{
    // ── Veldrid / ImGui ───────────────────────────────────────────────────────
    private GraphicsDevice?  _gd;
    private CommandList?     _cl;
    private ImGuiRenderer?   _renderer;
    private Sdl2Window?      _window;

    // ── Core ──────────────────────────────────────────────────────────────────
    private readonly MemoryManager   _mem;
    private readonly PointerResolver _resolver;

    // ── Features / hotkeys ────────────────────────────────────────────────────
    private List<FeatureInstance> _instances = [];
    private HotkeyManager         _hotkeys   = new();

    // ── Overlay state ─────────────────────────────────────────────────────────
    private bool _collapsed  = false;
    private Keys _toggleKey  = Keys.Insert;

    // ── Layout ────────────────────────────────────────────────────────────────
    private const int   OverlayWidth  = 310;
    private const int   OverlayHeight = 540;
    private const int   MarginX       = 12;
    private const int   MarginY       = 40;
    private const float ContentWidth  = 294f;

    // ── Colours ───────────────────────────────────────────────────────────────
    private static readonly Vector4 ColActive   = new(0.18f, 0.95f, 0.55f, 1f);
    private static readonly Vector4 ColInactive = new(0.65f, 0.65f, 0.65f, 1f);
    private static readonly Vector4 ColDanger   = new(0.95f, 0.30f, 0.30f, 1f);
    private static readonly Vector4 ColTitle    = new(1.00f, 0.80f, 0.20f, 1f);
    private static readonly Vector4 ColGroup    = new(0.85f, 0.60f, 0.10f, 1f);
    private static readonly Vector4 ColBg       = new(0.06f, 0.06f, 0.08f, 0.88f);

    public OverlayWindow(MemoryManager mem, PointerResolver resolver)
    {
        _mem      = mem;
        _resolver = resolver;
    }

    // ── Init ──────────────────────────────────────────────────────────────────

    public void Initialize()
    {
        (int px, int py) = GetGameWindowOrigin();

        var wci = new WindowCreateInfo(
            x:                  px + MarginX,
            y:                  py + MarginY,
            windowWidth:        OverlayWidth,
            windowHeight:       OverlayHeight,
            windowInitialState: WindowState.Normal,
            windowTitle:        "FFW Tool");

        VeldridStartup.CreateWindowAndGraphicsDevice(
            wci,
            new GraphicsDeviceOptions(debug: false, swapchainDepthFormat: null, syncToVerticalBlank: true),
            GraphicsBackend.Direct3D11,
            out _window!,
            out _gd!);

        _cl       = _gd.ResourceFactory.CreateCommandList();
        _renderer = new ImGuiRenderer(_gd, _gd.SwapchainFramebuffer.OutputDescription,
                                      OverlayWidth, OverlayHeight);

        SetupStyle();
        ApplyWindowFlags();
        RebuildInstances();
    }

    // ── Main loop ─────────────────────────────────────────────────────────────

    public void Run()
    {
        while (_window!.Exists)
        {
            var snap = _window.PumpEvents();
            if (!_window.Exists) break;

            if (_resolver.ReloadIfChanged())
                RebuildInstances();

            _hotkeys.ProcessPending();

            if (!_collapsed)
                TickFeatures();

            _renderer!.Update(1f / 60f, snap);
            RenderOverlay();

            _cl!.Begin();
            _cl.SetFramebuffer(_gd!.SwapchainFramebuffer);
            _cl.ClearColorTarget(0, new RgbaFloat(0f, 0f, 0f, 0f));
            _renderer.Render(_gd, _cl);
            _cl.End();
            _gd.SubmitCommands(_cl);
            _gd.SwapBuffers();
        }
    }

    // ── Feature tick ─────────────────────────────────────────────────────────

    private void TickFeatures()
    {
        if (!_mem.IsAttached) return;

        foreach (var inst in _instances)
        {
            try { inst.Behaviour.Tick(_mem, _resolver, inst); }
            catch { /* pointer chain may be invalid — skip silently */ }
        }
    }

    // ── Rebuild on hot-reload ─────────────────────────────────────────────────

    private void RebuildInstances()
    {
        _hotkeys.Dispose();
        _hotkeys  = new HotkeyManager();
        _instances = FeatureRegistry.Build(_resolver.Config);

        if (Enum.TryParse<Keys>(_resolver.Config.OverlayToggleKey, ignoreCase: true, out var tk))
            _toggleKey = tk;

        _hotkeys.Register(_toggleKey, HotkeyMode.OneShot, () => _collapsed = !_collapsed);

        foreach (var inst in _instances)
        {
            var captured = inst;
            _hotkeys.Register(captured.Hotkey, captured.Behaviour.HotkeyMode,
                () => captured.Behaviour.Execute(_mem, _resolver, captured));
        }
    }

    // ── Render ────────────────────────────────────────────────────────────────

    private void RenderOverlay()
    {
        var wFlags =
            ImGuiWindowFlags.NoTitleBar    |
            ImGuiWindowFlags.NoResize      |
            ImGuiWindowFlags.NoMove        |
            ImGuiWindowFlags.NoScrollbar   |
            ImGuiWindowFlags.NoSavedSettings |
            ImGuiWindowFlags.AlwaysAutoResize;

        if (_collapsed)
        {
            ImGui.SetNextWindowPos(Vector2.Zero, ImGuiCond.Always);
            ImGui.SetNextWindowBgAlpha(0.88f);
            ImGui.Begin("##bar", wFlags);
            // force min-width
            ImGui.Dummy(new Vector2(ContentWidth, 1));
            ImGui.SameLine(0, 0);
            ImGui.SetCursorPosX(8);
            ImGui.TextColored(ColTitle, $"FFW TOOL");
            ImGui.SameLine();
            ImGui.TextColored(ColInactive, $"[{_toggleKey}] expand");
            ImGui.End();
            return;
        }

        ImGui.SetNextWindowPos(Vector2.Zero, ImGuiCond.Always);
        ImGui.SetNextWindowBgAlpha(0.88f);
        ImGui.Begin("##overlay", wFlags);

        // min-width anchor
        ImGui.Dummy(new Vector2(ContentWidth, 1));

        RenderHeader();
        ImGui.Separator();
        RenderFeatureList();
        ImGui.Separator();
        RenderStatusBar();

        ImGui.End();
    }

    private void RenderHeader()
    {
        const string title = "FAR FAR WEST TOOL";
        float tw = ImGui.CalcTextSize(title).X;
        ImGui.SetCursorPosX((ContentWidth - tw) * 0.5f + 8);
        ImGui.TextColored(ColTitle, title);

        string sub = $"[{_toggleKey}] hide";
        float sw = ImGui.CalcTextSize(sub).X;
        ImGui.SetCursorPosX((ContentWidth - sw) * 0.5f + 8);
        ImGui.TextColored(ColInactive, sub);
        ImGui.Spacing();
    }

    private void RenderFeatureList()
    {
        var groups = _instances
            .GroupBy(i => i.Entry.Group ?? string.Empty)
            .OrderBy(g => g.Key);

        foreach (var group in groups)
        {
            if (!string.IsNullOrEmpty(group.Key))
            {
                ImGui.Spacing();
                ImGui.TextColored(ColGroup, group.Key.ToUpperInvariant());
                ImGui.Separator();
            }

            foreach (var inst in group)
                RenderRow(inst);

            ImGui.Spacing();
        }
    }

    private void RenderRow(FeatureInstance inst)
    {
        string label   = string.IsNullOrEmpty(inst.Entry.Label) ? inst.Key : inst.Entry.Label;
        string keyName = inst.Hotkey.ToString();

        if (inst.Behaviour is FreezeBehaviour)
        {
            // Checkbox toggles IsActive directly (mirrors the hotkey)
            bool active = inst.IsActive;
            var  col    = active ? ColActive : ColInactive;

            ImGui.PushStyleColor(ImGuiCol.CheckMark, col);
            if (ImGui.Checkbox($"##chk_{inst.Key}", ref active))
                inst.IsActive = active;
            ImGui.PopStyleColor();

            ImGui.SameLine();
            ImGui.TextColored(col, $"{label}  [{keyName}]");

            // Editable freeze value (right-aligned)
            ImGui.SameLine(ContentWidth - 55);
            float rv = (float)inst.RuntimeValue;
            ImGui.SetNextItemWidth(60);
            if (ImGui.InputFloat($"##v_{inst.Key}", ref rv, 0f, 0f, "%.0f"))
                inst.RuntimeValue = rv;
        }
        else
        {
            // Button fires Execute immediately
            string sign = inst.Behaviour is MinusBehaviour ? "-" : "+";
            if (ImGui.Button($"{sign}  {label}  [{keyName}]##btn_{inst.Key}"))
                inst.Behaviour.Execute(_mem, _resolver, inst);

            // Editable delta value (right-aligned)
            ImGui.SameLine(ContentWidth - 55);
            float rv = (float)inst.RuntimeValue;
            ImGui.SetNextItemWidth(60);
            if (ImGui.InputFloat($"##v_{inst.Key}", ref rv, 0f, 0f, "%.0f"))
                inst.RuntimeValue = rv;
        }
    }

    private void RenderStatusBar()
    {
        var col = _mem.IsAttached ? ColActive : ColDanger;
        var txt = _mem.IsAttached ? $"● {_mem.ProcessName}" : "● NOT ATTACHED";
        ImGui.TextColored(col, txt);
    }

    // ── Game window position ──────────────────────────────────────────────────

    private (int x, int y) GetGameWindowOrigin()
    {
        try
        {
            if (_mem.ProcessId != 0)
            {
                var proc = Process.GetProcessById(_mem.ProcessId);
                if (proc.MainWindowHandle != IntPtr.Zero &&
                    Win32Overlay.GetWindowRect(proc.MainWindowHandle, out var rect))
                    return (rect.Left, rect.Top);
            }
        }
        catch { }
        return (0, 0);
    }

    // ── Win32 window setup ────────────────────────────────────────────────────

    private void ApplyWindowFlags()
    {
        var hwnd = Win32Overlay.FindWindow(null, "FFW Tool");
        if (hwnd == IntPtr.Zero) return;

        // Borderless: strip title bar, borders, system menu — pure popup window
        Win32Overlay.SetWindowLong(hwnd, Win32Overlay.GWL_STYLE,
            Win32Overlay.WS_POPUP | Win32Overlay.WS_VISIBLE);

        // Layered (DWM per-pixel alpha) + no taskbar icon + no focus steal
        Win32Overlay.SetWindowLong(hwnd, Win32Overlay.GWL_EXSTYLE,
            Win32Overlay.WS_EX_LAYERED    |
            Win32Overlay.WS_EX_TOOLWINDOW |
            Win32Overlay.WS_EX_NOACTIVATE);

        // Always-on-top + commit the style change
        Win32Overlay.SetWindowPos(hwnd, Win32Overlay.HWND_TOPMOST,
            0, 0, 0, 0,
            Win32Overlay.SWP_NOMOVE       |
            Win32Overlay.SWP_NOSIZE       |
            Win32Overlay.SWP_NOACTIVATE   |
            Win32Overlay.SWP_FRAMECHANGED);

        // Extend DWM glass into entire client area.
        // Pixels cleared to (0,0,0,0) become fully transparent — only ImGui content shows.
        var margins = new Win32Overlay.MARGINS { Left = -1, Right = -1, Top = -1, Bottom = -1 };
        Win32Overlay.DwmExtendFrameIntoClientArea(hwnd, ref margins);
    }

    // ── Style ─────────────────────────────────────────────────────────────────

    private static void SetupStyle()
    {
        var style = ImGui.GetStyle();

        style.WindowRounding = 6f;
        style.FrameRounding  = 4f;
        style.ItemSpacing    = new Vector2(8, 5);
        style.WindowPadding  = new Vector2(10, 8);
        style.FramePadding   = new Vector2(6, 3);
        style.IndentSpacing  = 14f;

        var c = style.Colors;
        c[(int)ImGuiCol.WindowBg]        = ColBg;
        c[(int)ImGuiCol.FrameBg]         = new Vector4(0.12f, 0.12f, 0.16f, 1f);
        c[(int)ImGuiCol.FrameBgHovered]  = new Vector4(0.20f, 0.20f, 0.26f, 1f);
        c[(int)ImGuiCol.CheckMark]       = ColActive;
        c[(int)ImGuiCol.Button]          = new Vector4(0.18f, 0.18f, 0.24f, 1f);
        c[(int)ImGuiCol.ButtonHovered]   = new Vector4(0.25f, 0.25f, 0.32f, 1f);
        c[(int)ImGuiCol.ButtonActive]    = new Vector4(0.18f, 0.95f, 0.55f, 0.3f);
        c[(int)ImGuiCol.Separator]       = new Vector4(0.22f, 0.22f, 0.28f, 1f);
        c[(int)ImGuiCol.Text]            = new Vector4(0.90f, 0.90f, 0.92f, 1f);
        c[(int)ImGuiCol.TextDisabled]    = ColInactive;
    }

    // ── IDisposable ───────────────────────────────────────────────────────────

    public void Dispose()
    {
        _hotkeys.Dispose();
        _renderer?.Dispose();
        _cl?.Dispose();
        _gd?.Dispose();
    }
}
