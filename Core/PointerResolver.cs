using System.Text.Json;
using System.Text.Json.Serialization;

namespace FarFarWestTool.Core;

// ─────────────────────────────────────────────────────────────────────────────
// Modèles de config (addresses.json)
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Représente une pointer chain dans le fichier de config.
/// </summary>
public sealed class PointerEntry
{
    /// <summary>Offset statique depuis la base du module (hex string, ex: "0x3A1F20")</summary>
    [JsonPropertyName("staticOffset")]
    public string StaticOffset { get; init; } = "0x0";

    /// <summary>Offsets intermédiaires de la chaîne (hex strings, ex: "0x40")</summary>
    [JsonPropertyName("offsets")]
    public string[] Offsets { get; init; } = [];

    /// <summary>Parse les offsets en int[] depuis les hex strings</summary>
    [JsonIgnore]
    public int[] OffsetValues =>
        Array.ConvertAll(Offsets, o => Convert.ToInt32(o.Replace("0x", ""), 16));

    /// <summary>Type de la valeur finale (int, float, double, long, byte)</summary>
    [JsonPropertyName("type")]
    public string Type { get; init; } = "int";

    /// <summary>Description lisible — pour le debug et l'overlay</summary>
    [JsonPropertyName("description")]
    public string Description { get; init; } = string.Empty;

    /// <summary>Parse le staticOffset en long depuis la string hex</summary>
    [JsonIgnore]
    public long StaticOffsetValue =>
        Convert.ToInt64(StaticOffset.Replace("0x", ""), 16);
}

/// <summary>
/// Racine du fichier addresses.json
/// </summary>
public sealed class AddressConfig
{
    [JsonPropertyName("process")]
    public string Process { get; init; } = "FarFarWest-Win64-Shipping";

    [JsonPropertyName("pointers")]
    public Dictionary<string, PointerEntry> Pointers { get; init; } = [];
}

// ─────────────────────────────────────────────────────────────────────────────
// Résultat d'une résolution
// ─────────────────────────────────────────────────────────────────────────────

public sealed class ResolvedPointer
{
    public string Key { get; init; } = string.Empty;
    public IntPtr Address { get; init; }
    public bool IsValid => Address != IntPtr.Zero;
    public PointerEntry Entry { get; init; } = new();

    public override string ToString() =>
        $"[{Key}] 0x{Address:X} ({Entry.Description})";
}

// ─────────────────────────────────────────────────────────────────────────────
// PointerResolver
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Charge les pointer chains depuis addresses.json et les résout
/// via le MemoryManager. Supporte le rechargement à chaud du JSON.
/// </summary>
public sealed class PointerResolver
{
    private readonly MemoryManager _mem;
    private AddressConfig _config = new();
    private string _configPath = string.Empty;
    private DateTime _lastLoaded = DateTime.MinValue;

    // Cache des adresses résolues — invalidé à chaque EnsureResolved()
    private readonly Dictionary<string, ResolvedPointer> _cache = [];

    public PointerResolver(MemoryManager mem)
    {
        _mem = mem;
    }

    // ── Chargement config ─────────────────────────────────────────────────────

    /// <summary>
    /// Charge le fichier addresses.json.
    /// Lance FileNotFoundException si le fichier n'existe pas.
    /// </summary>
    public void LoadConfig(string path = "Config/addresses.json")
    {
        if (!File.Exists(path))
            throw new FileNotFoundException($"Config file not found: {path}");

        var json = File.ReadAllText(path);
        _config = JsonSerializer.Deserialize<AddressConfig>(json)
            ?? throw new InvalidDataException("Failed to deserialize addresses.json");

        _configPath = path;
        _lastLoaded = DateTime.Now;
        _cache.Clear();

        Console.WriteLine($"[PointerResolver] Loaded {_config.Pointers.Count} entries from {path}");
    }

    /// <summary>
    /// Recharge le config si le fichier a été modifié depuis le dernier chargement.
    /// À appeler dans la boucle principale pour le hot-reload.
    /// </summary>
    public bool ReloadIfChanged()
    {
        if (string.IsNullOrEmpty(_configPath)) return false;

        var lastWrite = File.GetLastWriteTime(_configPath);
        if (lastWrite <= _lastLoaded) return false;

        LoadConfig(_configPath);
        Console.WriteLine("[PointerResolver] Config reloaded (file changed).");
        return true;
    }

    /// <summary>
    /// Nom du process depuis la config.
    /// </summary>
    public string ProcessName => _config.Process;

    // ── Résolution ────────────────────────────────────────────────────────────

    /// <summary>
    /// Résout une pointer chain par sa clé (ex: "currency", "spell_cooldown_0").
    /// Utilise le cache si l'adresse a déjà été résolue.
    /// </summary>
    public ResolvedPointer Resolve(string key)
    {
        // if (_cache.TryGetValue(key, out var cached))
        //     return cached;

        if (!_config.Pointers.TryGetValue(key, out var entry))
            throw new KeyNotFoundException($"Pointer key '{key}' not found in config.");

        var address = _mem.Resolve(entry.StaticOffsetValue, entry.OffsetValues);

        var result = new ResolvedPointer
        {
            Key     = key,
            Address = address,
            Entry   = entry
        };

        _cache[key] = result;
        return result;
    }

    /// <summary>
    /// Résout toutes les pointer chains du config.
    /// Utile au démarrage pour détecter les chaînes cassées.
    /// </summary>
    public Dictionary<string, ResolvedPointer> ResolveAll()
    {
        _cache.Clear();
        var results = new Dictionary<string, ResolvedPointer>();

        foreach (var key in _config.Pointers.Keys)
        {
            try
            {
                results[key] = Resolve(key);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[PointerResolver] Failed to resolve '{key}': {ex.Message}");
                results[key] = new ResolvedPointer { Key = key, Address = IntPtr.Zero };
            }
        }

        return results;
    }

    /// <summary>
    /// Invalide le cache — à appeler après un redémarrage du jeu
    /// pour forcer la re-résolution de toutes les chaînes.
    /// </summary>
    public void InvalidateCache() => _cache.Clear();

    // ── Lecture/écriture via clé ──────────────────────────────────────────────

    /// <summary>
    /// Lit la valeur au bout de la pointer chain identifiée par sa clé.
    /// </summary>
    public T Read<T>(string key) where T : unmanaged
    {
        var resolved = Resolve(key);
        if (!resolved.IsValid)
            throw new MemoryException($"Pointer '{key}' resolved to null.");

        return _mem.Read<T>(resolved.Address);
    }

    /// <summary>
    /// Tente de lire sans exception — retourne false si la chaîne est invalide.
    /// </summary>
    public bool TryRead<T>(string key, out T value) where T : unmanaged
    {
        try
        {
            value = Read<T>(key);
            return true;
        }
        catch
        {
            value = default;
            return false;
        }
    }

    /// <summary>
    /// Écrit une valeur au bout de la pointer chain identifiée par sa clé.
    /// </summary>
    public void Write<T>(string key, T value) where T : unmanaged
    {
        var resolved = Resolve(key);
        if (!resolved.IsValid)
            throw new MemoryException($"Pointer '{key}' resolved to null.");

        _mem.Write(resolved.Address, value);
    }

    // ── Array helpers ─────────────────────────────────────────────────────────

    /// <summary>
    /// Résout la base d'un array et lit 'count' éléments espacés de 'stride' bytes.
    /// Parfait pour tes weapon fragments : ReadArray&lt;int&gt;("weapon_fragments_base", 20, stride: 8)
    /// </summary>
    public T[] ReadArray<T>(string key, int count, int stride = -1) where T : unmanaged
    {
        var resolved = Resolve(key);
        if (!resolved.IsValid)
            throw new MemoryException($"Pointer '{key}' resolved to null.");

        return _mem.ReadArray<T>(resolved.Address, count, stride);
    }

    /// <summary>
    /// Écrit un array depuis la base résolue par la clé.
    /// </summary>
    public void WriteArray<T>(string key, T[] values, int stride = -1) where T : unmanaged
    {
        var resolved = Resolve(key);
        if (!resolved.IsValid)
            throw new MemoryException($"Pointer '{key}' resolved to null.");

        _mem.WriteArray(resolved.Address, values, stride);
    }

    // ── Diagnostics ──────────────────────────────────────────────────────────

    /// <summary>
    /// Affiche l'état de toutes les pointer chains — utile au démarrage.
    /// </summary>
    public void PrintStatus()
    {
        Console.WriteLine("\n=== Pointer Chain Status ===");
        var all = ResolveAll();

        foreach (var (key, resolved) in all)
        {
            var status = resolved.IsValid ? "✓" : "✗";
            var addr   = resolved.IsValid ? $"0x{resolved.Address:X}" : "NULL";
            Console.WriteLine($"  [{status}] {key,-30} → {addr,-20} {resolved.Entry.Description}");
        }

        Console.WriteLine("============================\n");
    }
}