using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace FarFarWestTool.Core;

// ─────────────────────────────────────────────────────────────────────────────
// Win32 Native API
// ─────────────────────────────────────────────────────────────────────────────

internal static class NativeMethods
{
    // Process access rights
    public const uint PROCESS_VM_READ           = 0x0010;
    public const uint PROCESS_VM_WRITE          = 0x0020;
    public const uint PROCESS_VM_OPERATION      = 0x0008;
    public const uint PROCESS_QUERY_INFORMATION = 0x0400;
    public const uint PROCESS_ALL_ACCESS        = 0x1F0FFF;

    // Memory protection
    public const uint PAGE_EXECUTE_READWRITE = 0x40;
    public const uint PAGE_READWRITE         = 0x04;
    public const uint PAGE_READONLY          = 0x02;

    // Memory allocation
    public const uint MEM_COMMIT  = 0x1000;
    public const uint MEM_RESERVE = 0x2000;
    public const uint MEM_RELEASE = 0x8000;

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr OpenProcess(
        uint dwDesiredAccess,
        bool bInheritHandle,
        int dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool ReadProcessMemory(
        IntPtr hProcess,
        IntPtr lpBaseAddress,
        byte[] lpBuffer,
        int nSize,
        out int lpNumberOfBytesRead);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool WriteProcessMemory(
        IntPtr hProcess,
        IntPtr lpBaseAddress,
        byte[] lpBuffer,
        int nSize,
        out int lpNumberOfBytesWritten);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool VirtualProtectEx(
        IntPtr hProcess,
        IntPtr lpAddress,
        int dwSize,
        uint flNewProtect,
        out uint lpflOldProtect);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr VirtualAllocEx(
        IntPtr hProcess,
        IntPtr lpAddress,
        int dwSize,
        uint flAllocationType,
        uint flProtect);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool VirtualFreeEx(
        IntPtr hProcess,
        IntPtr lpAddress,
        int dwSize,
        uint dwFreeType);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll")]
    public static extern int GetLastError();
}

// ─────────────────────────────────────────────────────────────────────────────
// Exceptions custom
// ─────────────────────────────────────────────────────────────────────────────

public class MemoryException(string message, int? win32Error = null)
    : Exception(win32Error.HasValue
        ? $"{message} (Win32 error: 0x{win32Error.Value:X8})"
        : message);

public class ProcessNotFoundException(string processName)
    : MemoryException($"Process '{processName}' not found or not running.");

public class MemoryReadException(IntPtr address, int size)
    : MemoryException($"Failed to read {size} bytes at 0x{address:X}",
        NativeMethods.GetLastError());

public class MemoryWriteException(IntPtr address, int size)
    : MemoryException($"Failed to write {size} bytes at 0x{address:X}",
        NativeMethods.GetLastError());

// ─────────────────────────────────────────────────────────────────────────────
// MemoryManager
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Manager principal pour la lecture/écriture mémoire d'un process externe.
/// Thread-safe via lock sur _handle.
/// </summary>
public sealed class MemoryManager : IDisposable
{
    // ── State ─────────────────────────────────────────────────────────────────

    private IntPtr _handle = IntPtr.Zero;
    private readonly object _lock = new();
    private bool _disposed = false;

    public bool IsAttached => _handle != IntPtr.Zero && !_disposed;
    public IntPtr ModuleBase { get; private set; } = IntPtr.Zero;
    public string ProcessName { get; private set; } = string.Empty;
    public int ProcessId { get; private set; } = 0;

    // ── Attach / Detach ───────────────────────────────────────────────────────

    /// <summary>
    /// Attache le manager au premier process correspondant au nom donné.
    /// Lance ProcessNotFoundException si le process n'existe pas.
    /// </summary>
    public void Attach(string processName)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var processes = Process.GetProcessesByName(processName);
        if (processes.Length == 0)
            throw new ProcessNotFoundException(processName);

        var process = processes[0];

        lock (_lock)
        {
            // Détache proprement si déjà attaché
            if (_handle != IntPtr.Zero)
                Detach();

            _handle = NativeMethods.OpenProcess(
                NativeMethods.PROCESS_VM_READ    |
                NativeMethods.PROCESS_VM_WRITE   |
                NativeMethods.PROCESS_VM_OPERATION |
                NativeMethods.PROCESS_QUERY_INFORMATION,
                false,
                process.Id);

            if (_handle == IntPtr.Zero)
                throw new MemoryException(
                    $"OpenProcess failed for '{processName}'",
                    NativeMethods.GetLastError());

            ModuleBase = process.MainModule?.BaseAddress
                ?? throw new MemoryException("Could not retrieve MainModule base address.");

            ProcessName = processName;
            ProcessId   = process.Id;
        }
    }

    /// <summary>
    /// Détache du process courant sans Dispose.
    /// Utile pour re-attacher après un redémarrage du jeu.
    /// </summary>
    public void Detach()
    {
        lock (_lock)
        {
            if (_handle != IntPtr.Zero)
            {
                NativeMethods.CloseHandle(_handle);
                _handle     = IntPtr.Zero;
                ModuleBase  = IntPtr.Zero;
                ProcessName = string.Empty;
                ProcessId   = 0;
            }
        }
    }

    /// <summary>
    /// Vérifie si le process est toujours en vie et re-attache si nécessaire.
    /// À appeler dans la boucle principale.
    /// </summary>
    public bool EnsureAttached(string processName)
    {
        if (IsAttached)
        {
            // Vérifie que le process n'a pas été fermé
            var procs = Process.GetProcessesByName(processName);
            if (procs.Length > 0 && procs[0].Id == ProcessId)
                return true;

            Detach();
        }

        try
        {
            Attach(processName);
            return true;
        }
        catch (ProcessNotFoundException)
        {
            return false;
        }
    }

    // ── Read ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// Lit un bloc de bytes bruts depuis l'adresse donnée.
    /// </summary>
    public byte[] ReadBytes(IntPtr address, int size)
    {
        EnsureHandle();
        var buffer = new byte[size];

        lock (_lock)
        {
            if (!NativeMethods.ReadProcessMemory(_handle, address, buffer, size, out int bytesRead)
                || bytesRead != size)
                throw new MemoryReadException(address, size);
        }

        return buffer;
    }

    /// <summary>
    /// Lit une valeur typée depuis l'adresse donnée.
    /// Supporte tous les types blittables : int, float, double, long, etc.
    /// </summary>
    public T Read<T>(IntPtr address) where T : unmanaged
    {
        var bytes = ReadBytes(address, Marshal.SizeOf<T>());
        return MemoryMarshal.Read<T>(bytes);
    }

    /// <summary>
    /// Lit une valeur typée sans lever d'exception — retourne default si échec.
    /// Utile pour les lectures dans la boucle UI (ne crashe pas l'overlay).
    /// </summary>
    public bool TryRead<T>(IntPtr address, out T value) where T : unmanaged
    {
        try
        {
            value = Read<T>(address);
            return true;
        }
        catch
        {
            value = default;
            return false;
        }
    }

    /// <summary>
    /// Lit un pointeur 64-bit (IntPtr) depuis l'adresse donnée.
    /// </summary>
    public IntPtr ReadPointer(IntPtr address)
        => new(Read<long>(address));

    /// <summary>
    /// Lit une string UTF-8 de longueur fixe.
    /// </summary>
    public string ReadString(IntPtr address, int maxLength = 256)
    {
        var bytes = ReadBytes(address, maxLength);
        var end = Array.IndexOf(bytes, (byte)0);
        return Encoding.UTF8.GetString(bytes, 0, end >= 0 ? end : maxLength);
    }

    /// <summary>
    /// Lit une string Unicode (UTF-16) de longueur fixe.
    /// Utile pour les FString d'Unreal Engine.
    /// </summary>
    public string ReadUnicodeString(IntPtr address, int maxChars = 128)
    {
        var bytes = ReadBytes(address, maxChars * 2);
        var str = Encoding.Unicode.GetString(bytes);
        var end = str.IndexOf('\0');
        return end >= 0 ? str[..end] : str;
    }

    // ── Write ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Écrit un bloc de bytes bruts à l'adresse donnée.
    /// </summary>
    public void WriteBytes(IntPtr address, byte[] bytes)
    {
        EnsureHandle();

        lock (_lock)
        {
            if (!NativeMethods.WriteProcessMemory(_handle, address, bytes, bytes.Length, out int written)
                || written != bytes.Length)
                throw new MemoryWriteException(address, bytes.Length);
        }
    }

    /// <summary>
    /// Écrit une valeur typée à l'adresse donnée.
    /// </summary>
    public void Write<T>(IntPtr address, T value) where T : unmanaged
    {
        var bytes = new byte[Marshal.SizeOf<T>()];
        MemoryMarshal.Write(bytes, value);
        WriteBytes(address, bytes);
    }

    /// <summary>
    /// Écrit sans lever d'exception — retourne false si échec.
    /// </summary>
    public bool TryWrite<T>(IntPtr address, T value) where T : unmanaged
    {
        try
        {
            Write(address, value);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Écrit une valeur en levant temporairement la protection mémoire
    /// si nécessaire (pour les zones en read-only).
    /// </summary>
    public void WriteProtected<T>(IntPtr address, T value) where T : unmanaged
    {
        EnsureHandle();

        int size = Marshal.SizeOf<T>();
        NativeMethods.VirtualProtectEx(
            _handle, address, size,
            NativeMethods.PAGE_EXECUTE_READWRITE,
            out uint oldProtect);

        try
        {
            Write(address, value);
        }
        finally
        {
            // Restaure toujours la protection originale
            NativeMethods.VirtualProtectEx(
                _handle, address, size,
                oldProtect, out _);
        }
    }

    // ── Pointer Chain Resolution ──────────────────────────────────────────────

    /// <summary>
    /// Résout une pointer chain depuis la base du module.
    ///
    /// Exemple : Resolve(0x3A1F20, 0x40, 0x18, 0x08)
    ///   → lit [ModuleBase + 0x3A1F20]
    ///   → ajoute 0x40, déréférence
    ///   → ajoute 0x18, déréférence
    ///   → ajoute 0x08 (offset final, pas de déréférence)
    ///   → retourne l'adresse finale
    ///
    /// Retourne IntPtr.Zero si un maillon de la chaîne est null.
    /// </summary>
    public IntPtr Resolve(long staticOffset, params int[] offsets)
    {
        var address = ModuleBase + (nint)staticOffset;
        return ResolveFrom(address, offsets);
    }

    /// <summary>
    /// Résout une pointer chain depuis une adresse de base arbitraire.
    /// </summary>
    public IntPtr ResolveFrom(IntPtr baseAddress, params int[] offsets)
    {
        if (offsets.Length == 0)
            return baseAddress;

        var address = baseAddress;

        for (int i = 0; i < offsets.Length; i++)
        {
            if (!TryRead<long>(address, out long ptr))
                return IntPtr.Zero;

            if (ptr == 0)
                return IntPtr.Zero;

            address = new IntPtr(ptr) + offsets[i];
        }

        return address;
    }

    /// <summary>
    /// Résout et lit directement la valeur au bout de la pointer chain.
    /// </summary>
    public T ResolveRead<T>(long staticOffset, params int[] offsets) where T : unmanaged
    {
        var address = Resolve(staticOffset, offsets);
        if (address == IntPtr.Zero)
            throw new MemoryException($"Pointer chain resolved to null (offset 0x{staticOffset:X})");

        return Read<T>(address);
    }

    /// <summary>
    /// Résout et écrit directement la valeur au bout de la pointer chain.
    /// </summary>
    public void ResolveWrite<T>(T value, long staticOffset, params int[] offsets) where T : unmanaged
    {
        var address = Resolve(staticOffset, offsets);
        if (address == IntPtr.Zero)
            throw new MemoryException($"Pointer chain resolved to null (offset 0x{staticOffset:X})");

        Write(address, value);
    }

    // ── Array helpers ─────────────────────────────────────────────────────────

    /// <summary>
    /// Lit un array de T à partir d'une adresse de base.
    /// Utile pour tes weapon fragments (array d'éléments espacés de stride bytes).
    /// </summary>
    public T[] ReadArray<T>(IntPtr baseAddress, int count, int stride = -1) where T : unmanaged
    {
        int elementSize = Marshal.SizeOf<T>();
        int step = stride > 0 ? stride : elementSize;
        var result = new T[count];

        for (int i = 0; i < count; i++)
            result[i] = Read<T>(baseAddress + i * step);

        return result;
    }

    /// <summary>
    /// Écrit un array de T à partir d'une adresse de base.
    /// </summary>
    public void WriteArray<T>(IntPtr baseAddress, T[] values, int stride = -1) where T : unmanaged
    {
        int elementSize = Marshal.SizeOf<T>();
        int step = stride > 0 ? stride : elementSize;

        for (int i = 0; i < values.Length; i++)
            Write(baseAddress + i * step, values[i]);
    }

    // ── Module helpers ────────────────────────────────────────────────────────

    /// <summary>
    /// Retourne l'adresse absolue d'un offset dans le module principal.
    /// </summary>
    public IntPtr GetAddress(long offset)
        => ModuleBase + (nint)offset;

    /// <summary>
    /// Calcule l'offset d'une adresse absolue par rapport à la base du module.
    /// Utile pour convertir les adresses vues dans CE en offsets stables.
    /// </summary>
    public long ToOffset(IntPtr absoluteAddress)
        => absoluteAddress.ToInt64() - ModuleBase.ToInt64();

    // ── Diagnostics ──────────────────────────────────────────────────────────

    /// <summary>
    /// Dump hexadécimal d'une zone mémoire — utile pour le debug.
    /// </summary>
    public string HexDump(IntPtr address, int size = 256)
    {
        var bytes = ReadBytes(address, size);
        var sb = new StringBuilder();

        for (int i = 0; i < bytes.Length; i += 16)
        {
            sb.Append($"0x{(address + i).ToInt64():X16}  ");

            for (int j = 0; j < 16 && i + j < bytes.Length; j++)
                sb.Append($"{bytes[i + j]:X2} ");

            sb.Append(" | ");

            for (int j = 0; j < 16 && i + j < bytes.Length; j++)
            {
                char c = (char)bytes[i + j];
                sb.Append(char.IsControl(c) ? '.' : c);
            }

            sb.AppendLine();
        }

        return sb.ToString();
    }

    // ── Internal ──────────────────────────────────────────────────────────────

    private void EnsureHandle()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_handle == IntPtr.Zero)
            throw new InvalidOperationException(
                "MemoryManager is not attached to any process. Call Attach() first.");
    }

    // ── IDisposable ───────────────────────────────────────────────────────────

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Detach();
    }
}