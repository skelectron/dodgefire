using System.Runtime.CompilerServices;
using Brutal.Concurrency.Jobs;
using Crossfire.Sim.Rules;

namespace Crossfire.Sim.Bullets;

/// <summary>Moves bullets and despawns those that leave their half.</summary>
public static unsafe class BulletSystem
{
    /// <summary>Distance past the outer arena edges before a bullet despawns (the midline has none: bullets never cross).</summary>
    public static readonly Fix OuterMargin = Fix.FromInt(12);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void MoveRange(BulletPool p, int start, int end)
    {
        int* px = p.PosX, py = p.PosY, vx = p.VelX, vy = p.VelY;
        for (int i = start; i < end; i++)
        {
            px[i] += vx[i];
            py[i] += vy[i];
        }
    }

    /// <summary>Removes bullets outside their half. Single-threaded: swap-remove must be sequential to stay deterministic.</summary>
    public static void Despawn(BulletPool p, in MatchRules rules)
    {
        int mid = rules.HalfWidth.Raw;
        int m = OuterMargin.Raw;
        // Per-half x range: team 0 = [-m, mid], team 1 = [mid, W + m].
        int min0 = -m, max0 = mid;
        int min1 = mid, max1 = Fix.FromInt(rules.ArenaWidth).Raw + m;
        int minY = -m, maxY = Fix.FromInt(rules.ArenaHeight).Raw + m;

        int* px = p.PosX, py = p.PosY;
        byte* half = p.Half;
        int i = 0;
        while (i < p.Count)
        {
            int x = px[i], y = py[i];
            bool out0 = half[i] == 0 && (x < min0 || x > max0);
            bool out1 = half[i] != 0 && (x < min1 || x > max1);
            if (out0 || out1 || y < minY || y > maxY)
                p.RemoveAt(i); // re-test the bullet swapped into slot i
            else
                i++;
        }
    }
}

/// <summary>
/// Splits bullet movement across Brutal.Concurrency worker threads. Movement is per-bullet
/// independent, so the result is identical to the single-threaded path. Jobs are allocated once.
/// </summary>
public sealed class ParallelBulletMover : IDisposable
{
    private sealed class MoveJob : IJob
    {
        public BulletPool? Pool;
        public int Start, End;
        public void Run() => BulletSystem.MoveRange(Pool!, Start, End);
    }

    private readonly JobScheduler _scheduler;
    private readonly MoveJob[] _jobs;

    /// <summary>Below this many bullets the single-threaded path is used (scheduling overhead dominates).</summary>
    public int Threshold { get; set; } = 16_384;

    public int WorkerCount => _jobs.Length;

    public ParallelBulletMover(int workers)
    {
        workers = Math.Max(1, workers);
        _scheduler = new JobScheduler((uint)workers, "BulletMove", ThreadPriority.AboveNormal);
        _jobs = new MoveJob[workers];
        for (int i = 0; i < workers; i++) _jobs[i] = new MoveJob();
    }

    public void Move(BulletPool pool)
    {
        int count = pool.Count;
        if (count < Threshold || _jobs.Length == 1)
        {
            BulletSystem.MoveRange(pool, 0, count);
            return;
        }
        int per = (count + _jobs.Length - 1) / _jobs.Length;
        for (int j = 0; j < _jobs.Length; j++)
        {
            var job = _jobs[j];
            job.Pool = pool;
            job.Start = Math.Min(count, j * per);
            job.End = Math.Min(count, job.Start + per);
            _scheduler.Queue(job, job.End - job.Start);
        }
        _scheduler.ExecuteJobs();
        _scheduler.Wait();
    }

    public void Dispose() => _scheduler.AbortJobs(true);
}
