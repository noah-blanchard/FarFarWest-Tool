using FarFarWestTool.Core;
using FarFarWestTool.Overlay;

namespace FarFarWestTool.Features;

public static class FeatureRegistry
{
    private static readonly Dictionary<string, IFeatureBehaviour> Behaviours = new(StringComparer.OrdinalIgnoreCase)
    {
        ["freeze"] = new FreezeBehaviour(),
        ["add"]    = new AddBehaviour(),
        ["minus"]  = new MinusBehaviour(),
    };

    public static List<FeatureInstance> Build(AddressConfig config)
    {
        var instances = new List<FeatureInstance>();

        foreach (var (key, entry) in config.Pointers)
        {
            if (string.IsNullOrWhiteSpace(entry.Behaviour))
                continue;

            if (!Behaviours.TryGetValue(entry.Behaviour, out var behaviour))
            {
                Console.WriteLine($"[FeatureRegistry] Unknown behaviour '{entry.Behaviour}' for '{key}' — skipped.");
                continue;
            }

            if (!Enum.TryParse<Keys>(entry.Hotkey, ignoreCase: true, out var hotkey))
            {
                Console.WriteLine($"[FeatureRegistry] Unknown hotkey '{entry.Hotkey}' for '{key}' — skipped.");
                continue;
            }

            instances.Add(new FeatureInstance
            {
                Key          = key,
                Entry        = entry,
                Behaviour    = behaviour,
                Hotkey       = hotkey,
                RuntimeValue = entry.Value,
            });
        }

        return instances;
    }
}
