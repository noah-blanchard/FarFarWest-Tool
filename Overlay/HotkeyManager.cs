using System.Runtime.InteropServices;

namespace FarFarWestTool.Overlay;

// ─────────────────────────────────────────────────────────────────────────────
// Enums
// ─────────────────────────────────────────────────────────────────────────────

public enum HotkeyMode
{
    /// <summary>Action déclenchée une seule fois par appui.</summary>
    OneShot,

    /// <summary>Toggle : premier appui active, deuxième désactive.</summary>
    Toggle,

    /// <summary>Actif tant que la touche est maintenue.</summary>
    Hold
}

public enum Keys
{
    F1  = 0x70,
    F2  = 0x71,
    F3  = 0x72,
    F4  = 0x73,
    F5  = 0x74,
    F6  = 0x75,
    F7  = 0x76,
    F8  = 0x77,
    F9  = 0x78,
    F10 = 0x79,
    F11 = 0x7A,
    F12 = 0x7B,

    P = 0x50,
    M = 0x4D,

    Insert = 0x2D,
    Delete = 0x2E,
    Home   = 0x24,
    End    = 0x23,
    NumPad0 = 0x60,
    NumPad1 = 0x61,
    NumPad2 = 0x62,
    NumPad3 = 0x63,
    NumPad4 = 0x64,
}

// ─────────────────────────────────────────────────────────────────────────────
// Hotkey entry
// ─────────────────────────────────────────────────────────────────────────────

internal sealed class HotkeyEntry
{
    public Keys        Key      { get; init; }
    public HotkeyMode  Mode     { get; init; }
    public Action      Callback { get; init; } = () => { };

    // State interne
    public bool WasDown      { get; set; } = false;
    public bool ToggleState  { get; set; } = false;
}

// ─────────────────────────────────────────────────────────────────────────────
// HotkeyManager
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Gestionnaire de hotkeys globales via GetAsyncKeyState.
/// Supporte OneShot, Toggle, et Hold.
/// À appeler à chaque frame depuis la boucle principale.
/// </summary>
public sealed class HotkeyManager : IDisposable
{
    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    private readonly List<HotkeyEntry> _entries = [];
    private readonly Queue<Action>     _pending = new();
    private readonly object            _lock    = new();

    // ── Enregistrement ────────────────────────────────────────────────────────

    /// <summary>
    /// Enregistre une hotkey avec son mode et son callback.
    /// </summary>
    public void Register(Keys key, HotkeyMode mode, Action callback)
    {
        lock (_lock)
        {
            _entries.Add(new HotkeyEntry
            {
                Key      = key,
                Mode     = mode,
                Callback = callback
            });
        }
    }

    /// <summary>
    /// Supprime toutes les hotkeys associées à une touche.
    /// </summary>
    public void Unregister(Keys key)
    {
        lock (_lock)
            _entries.RemoveAll(e => e.Key == key);
    }

    // ── Polling (à appeler chaque frame) ──────────────────────────────────────

    /// <summary>
    /// Poll l'état de toutes les touches enregistrées.
    /// À appeler à chaque itération de la boucle principale.
    /// </summary>
    public void Poll()
    {
        lock (_lock)
        {
            foreach (var entry in _entries)
            {
                bool isDown = (GetAsyncKeyState((int)entry.Key) & 0x8000) != 0;

                switch (entry.Mode)
                {
                    case HotkeyMode.OneShot:
                        // Déclenche sur le front montant (appui, pas maintien)
                        if (isDown && !entry.WasDown)
                            EnqueueCallback(entry.Callback);
                        break;

                    case HotkeyMode.Toggle:
                        // Bascule l'état sur front montant
                        if (isDown && !entry.WasDown)
                        {
                            entry.ToggleState = !entry.ToggleState;
                            EnqueueCallback(entry.Callback);
                        }
                        break;

                    case HotkeyMode.Hold:
                        // Callback continu tant que la touche est enfoncée
                        if (isDown)
                            EnqueueCallback(entry.Callback);
                        break;
                }

                entry.WasDown = isDown;
            }
        }
    }

    /// <summary>
    /// Exécute les callbacks en attente — à appeler depuis le thread principal.
    /// </summary>
    public void ProcessPending()
    {
        Poll();

        while (_pending.TryDequeue(out var action))
        {
            try { action(); }
            catch (Exception ex)
            {
                Console.WriteLine($"[HotkeyManager] Callback error: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Retourne l'état toggle courant d'une touche.
    /// </summary>
    public bool GetToggleState(Keys key)
    {
        lock (_lock)
            return _entries.FirstOrDefault(e => e.Key == key)?.ToggleState ?? false;
    }

    // ── Internal ──────────────────────────────────────────────────────────────

    private void EnqueueCallback(Action callback)
    {
        lock (_pending)
            _pending.Enqueue(callback);
    }

    public void Dispose()
    {
        lock (_lock)
            _entries.Clear();
    }
}