namespace Crossfire.Sim;

/// <summary>Seeded xorshift32 RNG. Lives inside <see cref="SimState"/> so it is snapshotted and hashed.</summary>
public struct SimRng
{
    public uint State;

    public SimRng(uint seed) => State = seed == 0 ? 0x9E3779B9u : seed;

    public uint NextUInt()
    {
        uint x = State;
        x ^= x << 13;
        x ^= x >> 17;
        x ^= x << 5;
        State = x;
        return x;
    }

    /// <summary>Uniform integer in [0, maxExclusive).</summary>
    public int NextInt(int maxExclusive) => (int)((ulong)NextUInt() * (uint)maxExclusive >> 32);
}
