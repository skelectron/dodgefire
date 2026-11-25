using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Brutal;
using Brutal.Memory;
using Crossfire.Sim.Patterns;

namespace Crossfire.Sim.Bullets;

[Flags]
public enum BulletFlags : byte
{
    None = 0,
    /// <summary>Already counted as a graze (each bullet grazes at most once).</summary>
    Grazed = 1 << 0,
    /// <summary>Steers toward the nearest Dodger while <see cref="BulletPool.Timer"/> runs, then flies straight.</summary>
    Homing = 1 << 1,
    /// <summary>Drifts until <see cref="BulletPool.Timer"/> reaches 0, then bursts into a ring.</summary>
    Mine = 1 << 2,
    /// <summary>Bullets that need the per-tick special pass.</summary>
    Special = Homing | Mine,
}

/// <summary>
/// Fixed-capacity structure-of-arrays bullet storage in unmanaged memory (Brutal.Memory).
/// Allocated once; spawning and removal never allocate. Removal is swap-with-last, so order is
/// not stable — but it is deterministic, which is all the simulation needs.
/// Positions/velocities are stored as raw Q16.16 ints (<see cref="Fix.Raw"/>).
/// </summary>
public sealed unsafe class BulletPool : IDisposable
{
    public const int DefaultCapacity = 32_768;

    public readonly int Capacity;
    public int Count;

    public int* PosX;
    public int* PosY;
    public int* VelX;
    public int* VelY;
    /// <summary>The team whose half the bullet lives in (and whose Dodgers it can hit).</summary>
    public byte* Half;
    public byte* Type;
    public byte* Flags;
    /// <summary>Player slot that placed the pattern this bullet came from (hit credit).</summary>
    public byte* Owner;
    /// <summary>Ticks left for timed behaviour (homing, mine fuse).</summary>
    public ushort* Timer;
    /// <summary>Live bullets with a <see cref="BulletFlags.Special"/> flag (skip the special pass when 0).</summary>
    public int SpecialCount;

    public BulletPool(int capacity = DefaultCapacity)
    {
        Capacity = capacity;
        PosX = Alloc<int>(capacity);
        PosY = Alloc<int>(capacity);
        VelX = Alloc<int>(capacity);
        VelY = Alloc<int>(capacity);
        Half = Alloc<byte>(capacity);
        Type = Alloc<byte>(capacity);
        Flags = Alloc<byte>(capacity);
        Owner = Alloc<byte>(capacity);
        Timer = Alloc<ushort>(capacity);
    }

    private static T* Alloc<T>(int count) where T : unmanaged
    {
        Span<T> span = Mem.AllocZeroed<T>(ElementCount.Of(count));
        return (T*)Unsafe.AsPointer(ref MemoryMarshal.GetReference(span));
    }

    private static void Free<T>(ref T* ptr, int count) where T : unmanaged
    {
        if (ptr == null) return;
        Mem.Free(new Span<T>(ptr, count));
        ptr = null;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TrySpawn(FixVec2 pos, FixVec2 vel, int half, BulletType type, int owner = 0, BulletFlags flags = BulletFlags.None, int timer = 0)
    {
        int i = Count;
        if (i >= Capacity) return false; // Pool full: drop the bullet (deterministically).
        PosX[i] = pos.X.Raw;
        PosY[i] = pos.Y.Raw;
        VelX[i] = vel.X.Raw;
        VelY[i] = vel.Y.Raw;
        Half[i] = (byte)half;
        Type[i] = (byte)type;
        Flags[i] = (byte)flags;
        Owner[i] = (byte)owner;
        Timer[i] = (ushort)timer;
        if ((flags & BulletFlags.Special) != 0) SpecialCount++;
        Count = i + 1;
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void RemoveAt(int i)
    {
        if ((Flags[i] & (byte)BulletFlags.Special) != 0) SpecialCount--;
        int last = --Count;
        if (i == last) return;
        PosX[i] = PosX[last];
        PosY[i] = PosY[last];
        VelX[i] = VelX[last];
        VelY[i] = VelY[last];
        Half[i] = Half[last];
        Type[i] = Type[last];
        Flags[i] = Flags[last];
        Owner[i] = Owner[last];
        Timer[i] = Timer[last];
    }

    public void Clear()
    {
        Count = 0;
        SpecialCount = 0;
    }

    /// <summary>Snapshot copy (for rollback / interpolation). Copies only live bullets.</summary>
    public void CopyFrom(BulletPool other)
    {
        if (other.Count > Capacity) throw new ArgumentOutOfRangeException(nameof(other), other.Count, "Source pool has more bullets than this pool's capacity.");
        Count = other.Count;
        long n4 = (long)Count * sizeof(int);
        Buffer.MemoryCopy(other.PosX, PosX, n4, n4);
        Buffer.MemoryCopy(other.PosY, PosY, n4, n4);
        Buffer.MemoryCopy(other.VelX, VelX, n4, n4);
        Buffer.MemoryCopy(other.VelY, VelY, n4, n4);
        Buffer.MemoryCopy(other.Half, Half, Count, Count);
        Buffer.MemoryCopy(other.Type, Type, Count, Count);
        Buffer.MemoryCopy(other.Flags, Flags, Count, Count);
        Buffer.MemoryCopy(other.Owner, Owner, Count, Count);
        Buffer.MemoryCopy(other.Timer, Timer, (long)Count * sizeof(ushort), (long)Count * sizeof(ushort));
        SpecialCount = other.SpecialCount;
    }

    public void Dispose()
    {
        Free(ref PosX, Capacity);
        Free(ref PosY, Capacity);
        Free(ref VelX, Capacity);
        Free(ref VelY, Capacity);
        Free(ref Half, Capacity);
        Free(ref Type, Capacity);
        Free(ref Flags, Capacity);
        Free(ref Owner, Capacity);
        Free(ref Timer, Capacity);
        GC.SuppressFinalize(this);
    }

    ~BulletPool() => Dispose();
}
