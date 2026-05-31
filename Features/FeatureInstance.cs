using FarFarWestTool.Core;
using FarFarWestTool.Overlay;

namespace FarFarWestTool.Features;

public sealed class FeatureInstance
{
    public string            Key          { get; init; } = string.Empty;
    public PointerEntry      Entry        { get; init; } = new();
    public IFeatureBehaviour Behaviour    { get; init; } = null!;
    public Keys              Hotkey       { get; init; }
    public bool              IsActive     { get; set; }

    /// <summary>Mutable at runtime — editable from the overlay UI.</summary>
    public double            RuntimeValue { get; set; }
}
