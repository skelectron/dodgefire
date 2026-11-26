using Crossfire.Sim.Bullets;

namespace Crossfire.Sim.Bots;

/// <summary>
/// Bullet indices bucketed into fixed cells (counting sort, so bullets keep pool order inside a cell), built at
/// most once per tick and shared by every bot that thinks on that tick. Bots live outside the simulation and this
/// is a pure function of the state, so bot inputs stay deterministic. One grid per thread (tests run in parallel);
/// arrays only grow, so steady state does not allocate.
/// </summary>
internal sealed unsafe class BulletGrid
{
    public const int CellSize = 32;

    [ThreadStatic] private static BulletGrid? _current;

    private SimState? _state;
    private int _tick = -1;
    private int _count = -1;

    public int Cols { get; private set; }
    public int Rows { get; private set; }
    /// <summary>Bullets of cell c are Items[Start[c] .. Start[c+1]).</summary>
    public int[] Start = [];
    public int[] Items = [];
    private int[] _cellOf = [];

    public static BulletGrid For(SimState s)
    {
        var g = _current ??= new BulletGrid();
        if (!ReferenceEquals(g._state, s) || g._tick != s.Tick || g._count != s.Bullets.Count) g.Build(s);
        return g;
    }

    public int CellX(int x) => Math.Clamp(x / CellSize, 0, Cols - 1);
    public int CellY(int y) => Math.Clamp(y / CellSize, 0, Rows - 1);

    private void Build(SimState s)
    {
        _state = s;
        _tick = s.Tick;
        BulletPool pool = s.Bullets;
        int count = pool.Count;
        _count = count;
        Cols = Math.Max(1, (s.Rules.ArenaWidth + CellSize - 1) / CellSize);
        Rows = Math.Max(1, (s.Rules.ArenaHeight + CellSize - 1) / CellSize);
        int cells = Cols * Rows;
        if (Start.Length < cells + 1) Start = new int[cells + 1];
        if (Items.Length < count) Items = new int[Math.Max(count, pool.Capacity)];
        if (_cellOf.Length < count) _cellOf = new int[Math.Max(count, pool.Capacity)];

        Array.Clear(Start, 0, cells + 1);
        for (int i = 0; i < count; i++)
        {
            int c = CellY(pool.PosY[i] >> 16) * Cols + CellX(pool.PosX[i] >> 16);
            _cellOf[i] = c;
            Start[c + 1]++;
        }
        for (int c = 0; c < cells; c++) Start[c + 1] += Start[c];
        // Fill using Start as a moving cursor, then shift back (keeps pool order within each cell).
        for (int i = 0; i < count; i++) Items[Start[_cellOf[i]]++] = i;
        for (int c = cells; c > 0; c--) Start[c] = Start[c - 1];
        Start[0] = 0;
    }
}
