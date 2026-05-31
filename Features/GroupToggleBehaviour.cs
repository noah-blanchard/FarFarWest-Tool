using FarFarWestTool.Core;
using FarFarWestTool.Overlay;

namespace FarFarWestTool.Features;

public sealed class GroupToggleBehaviour : IFeatureBehaviour
{
    public HotkeyMode HotkeyMode => HotkeyMode.OneShot;

    public void Execute(MemoryManager mem, PointerResolver resolver, FeatureInstance instance)
    {
        instance.IsActive = !instance.IsActive;
        if (instance.Targets == null) return;
        foreach (var target in instance.Targets)
            target.IsActive = instance.IsActive;
    }

    public void Tick(MemoryManager mem, PointerResolver resolver, FeatureInstance instance) { }
}
