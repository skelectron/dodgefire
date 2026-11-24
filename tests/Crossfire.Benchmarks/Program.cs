using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;
using Crossfire.Sim;
using Crossfire.Sim.Bullets;
using Crossfire.Sim.Collision;
using Crossfire.Sim.Patterns;
using Crossfire.Sim.Players;

BenchmarkSwitcher.FromAssembly(typeof(BulletBenchmarks).Assembly).Run(args);

/// <summary>
/// Phase 3 target: 20,000 bullets with sim + collision under 4 ms per tick, 0 allocations.
/// Run: dotnet run -c Release --project tests/Crossfire.Benchmarks -- --filter *
/// </summary>
[MemoryDiagnoser]
public class BulletBenchmarks
{
    private static readonly PlayerSetup[] TwoVsTwo =
    [
        new(0, PlayerRole.Dodger), new(0, PlayerRole.Gunner),
        new(1, PlayerRole.Dodger), new(1, PlayerRole.Gunner),
    ];

    [Params(20_000, 30_000)]
    public int Bullets;

    private Simulation _serial = null!;
    private Simulation _parallel = null!;
    private SimState _template = null!;
    private SimState _state = null!;

    [GlobalSetup]
    public void Setup()
    {
        _serial = new Simulation();
        _parallel = new Simulation(Math.Max(2, Environment.ProcessorCount / 2));
        _parallel.Mover!.Threshold = 1;

        _template = SimState.CreateMatch(TwoVsTwo, 1);
        while (_template.Phase == MatchPhase.Countdown) _serial.Step(_template, default);
        // Keep the Dodgers alive for the whole benchmark: huge invulnerability, but measure collision separately below.
        var rng = new SimRng(3);
        for (int i = 0; i < Bullets; i++)
        {
            int half = i & 1;
            var pos = new FixVec2(Fix.FromInt(20 + rng.NextInt(600) + half * 640), Fix.FromInt(20 + rng.NextInt(680)));
            // Very slow bullets so the population stays roughly constant across iterations.
            _template.Bullets.TrySpawn(pos, FixMath.FromAngle(rng.NextInt(4096), Fix.Ratio(1, 64)), half, (BulletType)(i % 3));
        }
        _state = new SimState();
    }

    [IterationSetup]
    public void Reset() => _state.CopyFrom(_template);

    [Benchmark(Description = "Move (serial)")]
    public void MoveSerial() => BulletSystem.MoveRange(_state.Bullets, 0, _state.Bullets.Count);

    [Benchmark(Description = "Move (parallel)")]
    public void MoveParallel() => _parallel.Mover!.Move(_state.Bullets);

    [Benchmark(Description = "Despawn")]
    public void Despawn() => BulletSystem.Despawn(_state.Bullets, in _state.Rules);

    [Benchmark(Description = "Collision + graze")]
    public void Collide()
    {
        // Ensure Dodgers are vulnerable so the narrow phase runs.
        for (int i = 0; i < _state.PlayerCount; i++) _state.Players[i].InvulnTicks = 0;
        BulletCollision.Run(_state);
    }

    [Benchmark(Description = "Full Simulation.Step")]
    public void FullStep() => _serial.Step(_state, default);

    [Benchmark(Description = "State snapshot (CopyFrom)")]
    public void Snapshot() => _state.CopyFrom(_template);

    [GlobalCleanup]
    public void Cleanup()
    {
        _serial.Dispose();
        _parallel.Dispose();
        _template.Dispose();
        _state.Dispose();
    }
}
