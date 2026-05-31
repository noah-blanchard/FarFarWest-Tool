using System.Diagnostics;
using FarFarWestTool.Core;

using var mem = new MemoryManager();
mem.Attach("FarFarWest-Win64-Shipping");

var proc = Process.GetProcessesByName("FarFarWest-Win64-Shipping")[0];

Console.WriteLine($"MainModule base : 0x{proc.MainModule!.BaseAddress:X}");
Console.WriteLine("\nTous les modules :");
foreach (ProcessModule module in proc.Modules)
{
    Console.WriteLine($"  0x{module.BaseAddress:X} — {module.ModuleName}");
}

var resolver = new PointerResolver(mem);
var configPath = Path.Combine(
    AppContext.BaseDirectory,
    "Config",
    "addresses.json"
);
resolver.LoadConfig(configPath);

// Status de toutes les chains au démarrage
resolver.PrintStatus();

// Lecture directe par clé
var currency = resolver.Read<int>("spell_cooldown_1");
Console.WriteLine($"Currency : {currency}");

// Lecture array fragments (20 armes, stride 8 bytes)
// var fragments = resolver.ReadArray<int>("weapon_fragments_base", 20, stride: 8);
// for (int i = 0; i < fragments.Length; i++)
//     Console.WriteLine($"Arme {i} : {fragments[i]} fragments");

Console.ReadKey();