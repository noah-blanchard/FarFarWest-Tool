using FarFarWestTool.Core;
using FarFarWestTool.Overlay;

namespace FarFarWestTool.Features;

public static class FeatureRegistry
{
    private static readonly Dictionary<string, IFeatureBehaviour> Behaviours = new(StringComparer.OrdinalIgnoreCase)
    {
        ["freeze"]      = new FreezeBehaviour(),
        ["add"]         = new AddBehaviour(),
        ["minus"]       = new MinusBehaviour(),
        ["groupToggle"] = new GroupToggleBehaviour(),
    };

    public static List<FeatureInstance> Build(AddressConfig config)
    {
        var instances = new List<FeatureInstance>();
        var byKey     = new Dictionary<string, FeatureInstance>(StringComparer.Ordinal);

        // Pass 1 — build all regular (non-groupToggle) instances
        foreach (var (key, entry) in config.Pointers)
        {
            if (string.IsNullOrWhiteSpace(entry.Behaviour))
                continue;

            if (entry.Behaviour.Equals("groupToggle", StringComparison.OrdinalIgnoreCase))
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

            var inst = new FeatureInstance
            {
                Key          = key,
                Entry        = entry,
                Behaviour    = behaviour,
                Hotkey       = hotkey,
                RuntimeValue = entry.Value,
            };

            instances.Add(inst);
            byKey[key] = inst;
        }

        // Pass 2 — build groupToggle instances, link targets, mark members
        foreach (var (key, entry) in config.Pointers)
        {
            if (!entry.Behaviour.Equals("groupToggle", StringComparison.OrdinalIgnoreCase))
                continue;

            if (!Enum.TryParse<Keys>(entry.Hotkey, ignoreCase: true, out var hotkey))
            {
                Console.WriteLine($"[FeatureRegistry] Unknown hotkey '{entry.Hotkey}' for groupToggle '{key}' — skipped.");
                continue;
            }

            var targets = new List<FeatureInstance>();
            foreach (var targetKey in entry.Targets)
            {
                if (byKey.TryGetValue(targetKey, out var target))
                {
                    targets.Add(target);
                    target.IsGroupMember = true;
                }
                else
                {
                    Console.WriteLine($"[FeatureRegistry] groupToggle '{key}': target '{targetKey}' not found — skipped.");
                }
            }

            instances.Add(new FeatureInstance
            {
                Key          = key,
                Entry        = entry,
                Behaviour    = Behaviours["groupToggle"],
                Hotkey       = hotkey,
                RuntimeValue = entry.Value,
                Targets      = targets,
            });
        }

        return instances;
    }
}
