using FarFarWestTool.Core;
using FarFarWestTool.Overlay;

namespace FarFarWestTool.Features;

public sealed class SetBehaviour : IFeatureBehaviour
{
    public HotkeyMode HotkeyMode => HotkeyMode.OneShot;

    public void Execute(MemoryManager mem, PointerResolver resolver, FeatureInstance instance)
    {
        if (!instance.IsActive)
        {
            instance.PendingValue = MemoryWriter.ReadAsDouble(resolver, instance.Key, instance.Entry.Type);
            instance.IsActive = true;
        }
        else
        {
            MemoryWriter.WriteTyped(resolver, instance.Key, instance.Entry.Type, instance.PendingValue);
            instance.IsActive = false;
        }
    }

    public void Tick(MemoryManager mem, PointerResolver resolver, FeatureInstance instance) { }
}
