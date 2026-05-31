using FarFarWestTool.Core;

namespace FarFarWestTool.Features;

internal static class MemoryWriter
{
    public static void WriteTyped(PointerResolver resolver, string key, string type, double value)
    {
        switch (type.ToLowerInvariant())
        {
            case "float":  resolver.Write<float>(key,  (float)value);  break;
            case "double": resolver.Write<double>(key, value);         break;
            case "long":   resolver.Write<long>(key,   (long)value);   break;
            case "byte":   resolver.Write<byte>(key,   (byte)value);   break;
            default:       resolver.Write<int>(key,    (int)value);    break;
        }
    }

    public static double ReadAsDouble(PointerResolver resolver, string key, string type)
    {
        return type.ToLowerInvariant() switch
        {
            "float"  => resolver.TryRead<float>(key,  out var fv) ? fv : 0.0,
            "double" => resolver.TryRead<double>(key, out var dv) ? dv : 0.0,
            "long"   => resolver.TryRead<long>(key,   out var lv) ? lv : 0.0,
            "byte"   => resolver.TryRead<byte>(key,   out var bv) ? bv : 0.0,
            _        => resolver.TryRead<int>(key,    out var iv) ? iv : 0.0,
        };
    }
}
