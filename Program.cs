using FarFarWestTool.Core;
using FarFarWestTool.Overlay;

// ─────────────────────────────────────────────────────────────────────────────
// Bootstrap
// ─────────────────────────────────────────────────────────────────────────────

Console.Title = "FarFarWest Tool";
Console.WriteLine("=== FarFarWest Tool ===");
Console.WriteLine($".NET {Environment.Version}");
Console.WriteLine();

// ── Memory + Config ───────────────────────────────────────────────────────────

using var mem = new MemoryManager();
var resolver  = new PointerResolver(mem);

var configPath = Path.Combine(AppContext.BaseDirectory, "Config", "addresses.json");
resolver.LoadConfig(configPath);

// ── Attente du jeu ────────────────────────────────────────────────────────────

Console.WriteLine($"En attente de '{resolver.ProcessName}'...");

while (!mem.EnsureAttached(resolver.ProcessName))
{
    Console.Write(".");
    Thread.Sleep(2000);
}

Console.WriteLine();
Console.WriteLine($"Attaché ! ModuleBase = 0x{mem.ModuleBase:X}");
Console.WriteLine();

// ── Status des pointer chains ─────────────────────────────────────────────────

resolver.PrintStatus();

// ── Overlay ───────────────────────────────────────────────────────────────────

using var overlay = new OverlayWindow(mem, resolver);
overlay.Initialize();
overlay.Run();