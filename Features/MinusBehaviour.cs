using FarFarWestTool.Core;
using FarFarWestTool.Overlay;

namespace FarFarWestTool.Features;

public sealed class MinusBehaviour : IFeatureBehaviour
{
    public HotkeyMode HotkeyMode => HotkeyMode.OneShot;

    public void Execute(MemoryManager mem, PointerResolver resolver, FeatureInstance instance)
    {
        double current = MemoryWriter.ReadAsDouble(resolver, instance.Key, instance.Entry.Type);
        MemoryWriter.WriteTyped(resolver, instance.Key, instance.Entry.Type, current - instance.RuntimeValue);
    }

    public void Tick(MemoryManager mem, PointerResolver resolver, FeatureInstance instance) { }
}
