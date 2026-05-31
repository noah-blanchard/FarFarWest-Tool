using FarFarWestTool.Core;
using FarFarWestTool.Overlay;

namespace FarFarWestTool.Features;

public sealed class FreezeBehaviour : IFeatureBehaviour
{
    public HotkeyMode HotkeyMode => HotkeyMode.Toggle;

    public void Execute(MemoryManager mem, PointerResolver resolver, FeatureInstance instance)
        => instance.IsActive = !instance.IsActive;

    public void Tick(MemoryManager mem, PointerResolver resolver, FeatureInstance instance)
    {
        if (!instance.IsActive) return;
        MemoryWriter.WriteTyped(resolver, instance.Key, instance.Entry.Type, instance.RuntimeValue);
    }
}
