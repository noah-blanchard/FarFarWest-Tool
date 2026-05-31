using System.Numerics;
using System.Runtime.InteropServices;
using ImGuiNET;
using Veldrid;
using Veldrid.StartupUtilities;
using FarFarWestTool.Core;

namespace FarFarWestTool.Overlay;

// ─────────────────────────────────────────────────────────────────────────────
// Win32 — fenêtre toujours au-dessus
// ─────────────────────────────────────────────────────────────────────────────

internal static class Win32Overlay
{
    public const int GWL_EXSTYLE      = -20;
    public const int WS_EX_LAYERED    = 0x00080000;
    public const int WS_EX_TRANSPARENT = 0x00000020;
    public const int WS_EX_TOPMOST    = 0x00000008;
    public const int WS_EX_TOOLWINDOW = 0x00000080;
    public const int WS_EX_NOACTIVATE = 0x08000000;

    public static readonly IntPtr HWND_TOPMOST = new(-1);

    public const uint SWP_NOMOVE     = 0x0002;
    public const uint SWP_NOSIZE     = 0x0001;
    public const uint SWP_NOACTIVATE = 0x0010;

    [DllImport("user32.dll")]
    public static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    public static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll")]
    public static extern bool SetWindowPos(
        IntPtr hWnd, IntPtr hWndInsertAfter,
        int X, int Y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll")]
    public static extern bool SetLayeredWindowAttributes(
        IntPtr hWnd, uint crKey, byte bAlpha, uint dwFlags);

    [DllImport("user32.dll")]
    public static extern IntPtr FindWindow(string? lpClassName, string lpWindowName);
}

// ─────────────────────────────────────────────────────────────────────────────
// Feature state — ce que l'overlay contrôle
// ─────────────────────────────────────────────────────────────────────────────

public sealed class FeatureState
{
    // Cooldowns
    public bool FreezeCooldowns { get; set; } = false;
    public double[] CooldownValues { get; set; } = new double[4];

    // Currency
    public bool LockCurrency { get; set; } = false;
    public int CurrencyValue { get; set; } = 0;
    public int CurrencyLockTarget { get; set; } = 9999;

    // Weapon fragments
    public int[] FragmentValues { get; set; } = new int[20];
    public int FragmentSetTarget { get; set; } = 99;
}

// ─────────────────────────────────────────────────────────────────────────────
// OverlayWindow
// ─────────────────────────────────────────────────────────────────────────────

public sealed class OverlayWindow : IDisposable
{
    // ── Veldrid / ImGui ───────────────────────────────────────────────────────
    private GraphicsDevice?   _gd;
    private CommandList?      _cl;
    private ImGuiRenderer?    _renderer;
    private Sdl2Window?       _window;

    // ── State ─────────────────────────────────────────────────────────────────
    private readonly MemoryManager    _mem;
    private readonly PointerResolver  _resolver;
    private readonly FeatureState     _state = new();
    private readonly HotkeyManager   _hotkeys;

    // ── Config overlay ────────────────────────────────────────────────────────
    private const int   OverlayWidth  = 280;
    private const int   OverlayHeight = 320;
    private const int   MarginRight   = 20;
    private const int   MarginTop     = 20;
    private const float BgAlpha       = 0.82f;

    // Couleurs HUD
    private static readonly Vector4 ColActive   = new(0.18f, 0.95f, 0.55f, 1f);  // vert
    private static readonly Vector4 ColInactive = new(0.65f, 0.65f, 0.65f, 1f);  // gris
    private static readonly Vector4 ColDanger   = new(0.95f, 0.30f, 0.30f, 1f);  // rouge
    private static readonly Vector4 ColTitle    = new(1.00f, 0.80f, 0.20f, 1f);  // or
    private static readonly Vector4 ColBg       = new(0.06f, 0.06f, 0.08f, BgAlpha);

    public OverlayWindow(MemoryManager mem, PointerResolver resolver)
    {
        _mem      = mem;
        _resolver = resolver;
        _hotkeys  = new HotkeyManager();
    }

    // ── Init ──────────────────────────────────────────────────────────────────

    public void Initialize()
    {
        // Résolution écran via SDL
        SDL2.SDL.SDL_Init(SDL2.SDL.SDL_INIT_VIDEO);
        SDL2.SDL.SDL_GetCurrentDisplayMode(0, out var dm);
        int screenW = dm.w;
        int screenH = dm.h;

        int posX = screenW - OverlayWidth - MarginRight;
        int posY = MarginTop;

        var wci = new WindowCreateInfo(
            x:            posX,
            y:            posY,
            windowWidth:  OverlayWidth,
            windowHeight: OverlayHeight,
            windowInitialState: WindowState.Normal,
            windowTitle:  "FFW Tool"
        );

        VeldridStartup.CreateWindowAndGraphicsDevice(
            wci,
            new GraphicsDeviceOptions(debug: false, swapchainDepthFormat: null, syncToVerticalBlank: true),
            GraphicsBackend.OpenGL,
            out _window!,
            out _gd!);

        _cl       = _gd.ResourceFactory.CreateCommandList();
        _renderer = new ImGuiRenderer(_gd, _gd.SwapchainFramebuffer.OutputDescription,
                                      OverlayWidth, OverlayHeight);

        SetupStyle();
        SetWindowTopmost();
        RegisterHotkeys();
    }

    // ── Boucle principale ─────────────────────────────────────────────────────

    public void Run()
    {
        while (_window!.Exists)
        {
            var snap = _window.PumpEvents();
            if (!_window.Exists) break;

            // Hot-reload config si addresses.json modifié
            _resolver.ReloadIfChanged();

            // Traitement hotkeys
            _hotkeys.ProcessPending();

            // Tick des features actives
            TickFeatures();

            // Rendu ImGui
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

    // ── Tick features ─────────────────────────────────────────────────────────

    private void TickFeatures()
    {
        if (!_mem.IsAttached) return;

        // Freeze cooldowns — écrit 0.0 à chaque tick
        if (_state.FreezeCooldowns)
        {
            for (int i = 0; i < 4; i++)
            {
                var key = $"spell_cooldown_{i}";
                _resolver.TryWrite<double>(key, 0.0);
            }
        }

        // Lire les valeurs courantes pour l'affichage
        for (int i = 0; i < 4; i++)
        {
            var key = $"spell_cooldown_{i}";
            if (_resolver.TryRead<double>(key, out var cd))
                _state.CooldownValues[i] = cd;
        }

        // Lock currency
        if (_state.LockCurrency)
            _resolver.TryWrite<int>("currency", _state.CurrencyLockTarget);

        if (_resolver.TryRead<int>("currency", out var curr))
            _state.CurrencyValue = curr;

        // Lire fragments
        for (int i = 0; i < 20; i++)
        {
            // Chaque arme est à base + i*8
            var resolved = _resolver.Resolve("weapon_fragments_base");
            if (resolved != IntPtr.Zero && _mem.TryRead<int>(resolved + i * 8, out var frag))
                _state.FragmentValues[i] = frag;
        }
    }

    // ── Rendu ImGui ───────────────────────────────────────────────────────────

    private void RenderOverlay()
    {
        // Fenêtre fixe, non déplaçable, sans decorations
        ImGui.SetNextWindowPos(Vector2.Zero, ImGuiCond.Always);
        ImGui.SetNextWindowSize(new Vector2(OverlayWidth, OverlayHeight), ImGuiCond.Always);
        ImGui.SetNextWindowBgAlpha(BgAlpha);

        var flags =
            ImGuiWindowFlags.NoTitleBar    |
            ImGuiWindowFlags.NoResize      |
            ImGuiWindowFlags.NoMove        |
            ImGuiWindowFlags.NoScrollbar   |
            ImGuiWindowFlags.NoSavedSettings;

        ImGui.Begin("##overlay", flags);

        RenderHeader();
        ImGui.Separator();
        RenderCooldownSection();
        ImGui.Separator();
        RenderCurrencySection();
        ImGui.Separator();
        RenderFragmentsSection();
        ImGui.Separator();
        RenderStatusBar();

        ImGui.End();
    }

    // ── Sections ──────────────────────────────────────────────────────────────

    private void RenderHeader()
    {
        ImGui.SetCursorPosX((OverlayWidth - ImGui.CalcTextSize("FAR FAR WEST").X) * 0.5f);
        ImGui.TextColored(ColTitle, "FAR FAR WEST");

        ImGui.SetCursorPosX((OverlayWidth - ImGui.CalcTextSize("TOOL v1.0").X) * 0.5f);
        ImGui.TextColored(ColInactive, "TOOL v1.0");

        ImGui.Spacing();
    }

    private void RenderCooldownSection()
    {
        ImGui.TextColored(ColTitle, "SPELL COOLDOWNS");
        ImGui.Spacing();

        // Toggle freeze — F1
        bool freeze = _state.FreezeCooldowns;
        if (ImGui.Checkbox("Freeze All [F1]", ref freeze))
            _state.FreezeCooldowns = freeze;

        ImGui.Spacing();

        // Barres de cooldown
        string[] labels = ["Slot 1", "Slot 2", "Slot 3", "Slot 4"];
        for (int i = 0; i < 4; i++)
        {
            double cd      = _state.CooldownValues[i];
            double maxCd   = 60.0; // ajuste selon tes sorts
            float  pct     = (float)Math.Clamp(cd / maxCd, 0.0, 1.0);
            var    col     = _state.FreezeCooldowns ? ColActive : (pct > 0.1f ? ColDanger : ColActive);

            ImGui.TextColored(ColInactive, labels[i]);
            ImGui.SameLine(60);

            ImGui.PushStyleColor(ImGuiCol.PlotHistogram, col);
            ImGui.ProgressBar(pct, new Vector2(160, 14), $"{cd:F1}s");
            ImGui.PopStyleColor();
        }

        ImGui.Spacing();
    }

    private void RenderCurrencySection()
    {
        ImGui.TextColored(ColTitle, "CURRENCY");
        ImGui.Spacing();

        // Valeur courante
        ImGui.TextColored(ColInactive, "Current:");
        ImGui.SameLine();
        ImGui.TextColored(ColActive, $"{_state.CurrencyValue:N0}");

        // Lock + target
        bool lockCurr = _state.LockCurrency;
        if (ImGui.Checkbox("Lock [F2]", ref lockCurr))
            _state.LockCurrency = lockCurr;

        ImGui.SameLine();
        int target = _state.CurrencyLockTarget;
        ImGui.SetNextItemWidth(80);
        if (ImGui.InputInt("##curr_target", ref target, 0))
            _state.CurrencyLockTarget = Math.Max(0, target);

        ImGui.Spacing();
    }

    private void RenderFragmentsSection()
    {
        ImGui.TextColored(ColTitle, "WEAPON FRAGMENTS");
        ImGui.Spacing();

        // Set all
        int setTarget = _state.FragmentSetTarget;
        ImGui.SetNextItemWidth(60);
        if (ImGui.InputInt("##frag_target", ref setTarget, 0))
            _state.FragmentSetTarget = Math.Clamp(setTarget, 0, 9999);

        ImGui.SameLine();
        if (ImGui.Button("Set All [F3]"))
            SetAllFragments(_state.FragmentSetTarget);

        ImGui.Spacing();

        // Affichage compact des fragments
        for (int i = 0; i < 20; i++)
        {
            int val = _state.FragmentValues[i];
            bool unlocked = val >= 6;

            ImGui.TextColored(unlocked ? ColActive : ColInactive,
                $"W{i + 1,2}: {val,4}");

            // 4 par ligne
            if ((i + 1) % 4 != 0) ImGui.SameLine(0, 12);
        }

        ImGui.Spacing();
    }

    private void RenderStatusBar()
    {
        var statusCol = _mem.IsAttached ? ColActive : ColDanger;
        var statusTxt = _mem.IsAttached
            ? $"● {_mem.ProcessName}"
            : "● NOT ATTACHED";

        ImGui.TextColored(statusCol, statusTxt);
    }

    // ── Actions ───────────────────────────────────────────────────────────────

    private void SetAllFragments(int value)
    {
        var resolved = _resolver.Resolve("weapon_fragments_base");
        if (resolved == IntPtr.Zero) return;

        for (int i = 0; i < 20; i++)
            _mem.TryWrite<int>(resolved + i * 8, value);
    }

    // ── Hotkeys ───────────────────────────────────────────────────────────────

    private void RegisterHotkeys()
    {
        // F1 — toggle freeze cooldowns
        _hotkeys.Register(Keys.F1, HotkeyMode.Toggle, () =>
        {
            _state.FreezeCooldowns = !_state.FreezeCooldowns;
        });

        // F2 — toggle lock currency
        _hotkeys.Register(Keys.F2, HotkeyMode.Toggle, () =>
        {
            _state.LockCurrency = !_state.LockCurrency;
        });

        // F3 — set all fragments (one-shot)
        _hotkeys.Register(Keys.F3, HotkeyMode.OneShot, () =>
        {
            SetAllFragments(_state.FragmentSetTarget);
        });
    }

    // ── Style ─────────────────────────────────────────────────────────────────

    private static void SetupStyle()
    {
        var style = ImGui.GetStyle();

        style.WindowRounding    = 6f;
        style.FrameRounding     = 4f;
        style.ItemSpacing       = new Vector2(8, 5);
        style.WindowPadding     = new Vector2(12, 10);
        style.FramePadding      = new Vector2(6, 3);
        style.IndentSpacing     = 14f;

        var colors = style.Colors;
        colors[(int)ImGuiCol.WindowBg]         = ColBg;
        colors[(int)ImGuiCol.FrameBg]          = new Vector4(0.12f, 0.12f, 0.16f, 1f);
        colors[(int)ImGuiCol.FrameBgHovered]   = new Vector4(0.20f, 0.20f, 0.26f, 1f);
        colors[(int)ImGuiCol.CheckMark]        = ColActive;
        colors[(int)ImGuiCol.SliderGrab]       = ColActive;
        colors[(int)ImGuiCol.Button]           = new Vector4(0.18f, 0.18f, 0.24f, 1f);
        colors[(int)ImGuiCol.ButtonHovered]    = new Vector4(0.25f, 0.25f, 0.32f, 1f);
        colors[(int)ImGuiCol.ButtonActive]     = new Vector4(0.18f, 0.95f, 0.55f, 0.3f);
        colors[(int)ImGuiCol.Separator]        = new Vector4(0.22f, 0.22f, 0.28f, 1f);
        colors[(int)ImGuiCol.Text]             = new Vector4(0.90f, 0.90f, 0.92f, 1f);
        colors[(int)ImGuiCol.TextDisabled]     = ColInactive;
    }

    // ── Toujours au-dessus ────────────────────────────────────────────────────

    private void SetWindowTopmost()
    {
        var hwnd = Win32Overlay.FindWindow(null, "FFW Tool");
        if (hwnd == IntPtr.Zero) return;

        // Extended style : layered + no taskbar + no activate
        int exStyle = Win32Overlay.GetWindowLong(hwnd, Win32Overlay.GWL_EXSTYLE);
        exStyle |= Win32Overlay.WS_EX_LAYERED    |
                   Win32Overlay.WS_EX_TOOLWINDOW |
                   Win32Overlay.WS_EX_NOACTIVATE;
        Win32Overlay.SetWindowLong(hwnd, Win32Overlay.GWL_EXSTYLE, exStyle);

        // Toujours au-dessus
        Win32Overlay.SetWindowPos(
            hwnd,
            Win32Overlay.HWND_TOPMOST,
            0, 0, 0, 0,
            Win32Overlay.SWP_NOMOVE | Win32Overlay.SWP_NOSIZE | Win32Overlay.SWP_NOACTIVATE);
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