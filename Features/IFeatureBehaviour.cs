using FarFarWestTool.Core;
using FarFarWestTool.Overlay;

namespace FarFarWestTool.Features;

public interface IFeatureBehaviour
{
    HotkeyMode HotkeyMode { get; }

    /// <summary>Called by the hotkey callback — toggles active state or fires a one-shot write.</summary>
    void Execute(MemoryManager mem, PointerResolver resolver, FeatureInstance instance);

    /// <summary>Called every frame — used by Freeze to hold a value in memory.</summary>
    void Tick(MemoryManager mem, PointerResolver resolver, FeatureInstance instance);
}
