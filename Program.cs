using System.Diagnostics;
using FarFarWestTool.Core;

using var mem = new MemoryManager();
mem.Attach("FarFarWest-Win64-Shipping");

var proc = Process.GetProcessesByName("FarFarWest-Win64-Shipping")[0];

var resolver = new PointerResolver(mem);
var configPath = Path.Combine(
    AppContext.BaseDirectory,
    "Config",
    "addresses.json"
);
resolver.LoadConfig(configPath);

resolver.PrintStatus();

var currency = resolver.Read<int>("spell_cooldown_1");
Console.WriteLine($"Spell Cooldown 1 : {currency}");
Console.ReadKey();