using Brutal.SdlApi;
using Brutal.SdlApi.Mixer;
using Crossfire.Sim;
using Crossfire.Sim.Patterns;

namespace Crossfire.Audio;

/// <summary>Sound effects the game can play (one file each in assets/kenney/audio).</summary>
public enum Sfx
{
    Place,
    FireSmall,
    FireSmall2,
    FireLarge,
    FireSweep,
    FireHoming,
    FireMines,
    MineBurst,
    Rejected,
    Graze,
    Flip,
    FlipBlock,
    Break,
    BreakField,
    Overdrive,
    Hit,
    Death,
    RoundStart,
    RoundEnd,
    MatchEnd,
    UiMove,
    UiConfirm,
    UiBack,
}

public enum Music
{
    None,
    Menu,
    Match,
}

/// <summary>
/// Sound effects and music via SDL3_mixer. Simulation events drive the effects (the sim is never touched).
/// Everything is loaded and pre-decoded at startup; playing a sound reuses a fixed pool of tracks, so the
/// frame loop does not allocate. If audio cannot start (no device, missing files), the game stays silent.
/// </summary>
public sealed unsafe class AudioSystem : IDisposable
{
    private const uint DefaultPlaybackDevice = 0xFFFFFFFF; // SDL_AUDIO_DEVICE_DEFAULT_PLAYBACK
    private const int TrackCount = 24;
    private const float ArenaWidth = 1280f;

    private static readonly string[] SfxFiles =
    [
        "place.ogg", "fire_small.ogg", "fire_small2.ogg", "fire_large.ogg", "fire_sweep.ogg", "fire_homing.ogg", "fire_mines.ogg",
        "mine_burst.ogg", "rejected.ogg", "graze.ogg", "flip.ogg", "flip_block.ogg", "break.ogg", "break_field.ogg", "overdrive.ogg",
        "hit.ogg", "death.ogg", "round_start.ogg", "round_end.ogg", "match_end.ogg", "ui_move.ogg", "ui_confirm.ogg", "ui_back.ogg",
    ];

    /// <summary>Per-effect base volume (the mix) and minimum seconds between two plays (anti-spam).</summary>
    private static readonly (float Gain, float MinGap)[] SfxMix =
    [
        (0.35f, 0.05f), (0.22f, 0.06f), (0.22f, 0.06f), (0.3f, 0.08f), (0.28f, 0.1f), (0.3f, 0.1f), (0.3f, 0.1f),
        (0.3f, 0.05f), (0.35f, 0.15f), (0.12f, 0.06f), (0.45f, 0.05f), (0.6f, 0.05f), (0.85f, 0.2f), (0.5f, 0.2f), (0.6f, 0.2f),
        (0.6f, 0.08f), (0.85f, 0.1f), (0.5f, 0.3f), (0.55f, 0.5f), (0.6f, 0.5f), (0.25f, 0.03f), (0.4f, 0.05f), (0.35f, 0.05f),
    ];

    private readonly MixMixer _mixer;
    private readonly MixAudio[] _sfx = new MixAudio[SfxFiles.Length];
    private readonly float[] _lastPlayed = new float[SfxFiles.Length];
    private readonly MixTrack[] _tracks = new MixTrack[TrackCount];
    private int _nextTrack;
    private readonly MixAudio _menuMusic, _matchMusic;
    private readonly MixTrack _musicTrack;
    private Music _music = Music.None;
    private float _musicFade = 1f; // 0..1 fade-in progress
    private float _time;

    public bool Enabled { get; }
    public float MasterVolume { get; set; } = 0.8f;
    public float MusicVolume { get; set; } = 0.45f;
    public float SfxVolume { get; set; } = 0.9f;
    public bool Muted { get; set; }
    public int LoadedSounds { get; }

    public AudioSystem(string audioDirectory)
    {
        try
        {
            if (!Sdl.Init.InitSubSystem(SdlInitFlags.Audio)) throw new InvalidOperationException("SDL audio: " + Sdl.PInvoke.GetError());
            SdlMixer.Init();
            _mixer = SdlMixer.CreateMixerDevice(DefaultPlaybackDevice, null);
            if (_mixer.IsNull()) throw new InvalidOperationException("no audio device: " + Sdl.PInvoke.GetError());

            for (int i = 0; i < SfxFiles.Length; i++)
            {
                string path = Path.Combine(audioDirectory, SfxFiles[i]);
                if (!File.Exists(path)) continue;
                _sfx[i] = SdlMixer.LoadAudio(_mixer, path, true);
                if (!_sfx[i].IsNull()) LoadedSounds++;
                _lastPlayed[i] = -10f;
            }
            for (int t = 0; t < TrackCount; t++) _tracks[t] = SdlMixer.CreateTrack(_mixer);

            _menuMusic = LoadIfExists(Path.Combine(audioDirectory, "music_menu.ogg"), predecode: false);
            _matchMusic = LoadIfExists(Path.Combine(audioDirectory, "music_match.ogg"), predecode: false);
            _musicTrack = SdlMixer.CreateTrack(_mixer);
            Enabled = true;
            Console.WriteLine($"Audio: {SdlMixer.GetNumAudioDecoders()} decoders, {LoadedSounds}/{SfxFiles.Length} sounds loaded");
        }
        catch (Exception e)
        {
            Enabled = false;
            Console.WriteLine($"Audio disabled ({e.Message}). The game will run silent.");
        }
    }

    private MixAudio LoadIfExists(string path, bool predecode) =>
        File.Exists(path) ? SdlMixer.LoadAudio(_mixer, path, predecode) : default;

    /// <summary>Plays an effect, panned by arena x (null = centre). Gain is multiplied by the mix and volumes.</summary>
    public void Play(Sfx sfx, float? arenaX = null, float gain = 1f)
    {
        if (!Enabled || Muted) return;
        int i = (int)sfx;
        var audio = _sfx[i];
        if (audio.IsNull()) return;
        var (mix, gap) = SfxMix[i];
        if (_time - _lastPlayed[i] < gap) return;
        _lastPlayed[i] = _time;

        // Round-robin track; skip ones still playing if a free one is close by.
        var track = _tracks[_nextTrack];
        for (int k = 0; k < TrackCount && track.Playing(); k++)
        {
            _nextTrack = (_nextTrack + 1) % TrackCount;
            track = _tracks[_nextTrack];
        }
        _nextTrack = (_nextTrack + 1) % TrackCount;

        SdlMixer.SetTrackAudio(track, audio);
        track.SetGain(mix * gain * SfxVolume * MasterVolume);
        float pan = arenaX is float x ? Math.Clamp(x / ArenaWidth * 2f - 1f, -1f, 1f) * 0.7f : 0f;
        var stereo = new MixStereoGains { left = Math.Min(1f, 1f - pan), right = Math.Min(1f, 1f + pan) };
        track.SetStereo(&stereo);
        track.Play(0);
    }

    /// <summary>Switches the looping background track (fades the new one in).</summary>
    public void SetMusic(Music music)
    {
        if (!Enabled || music == _music) return;
        _music = music;
        _musicTrack.Stop(0);
        var audio = music switch { Music.Menu => _menuMusic, Music.Match => _matchMusic, _ => default };
        if (audio.IsNull()) return;
        SdlMixer.SetTrackAudio(_musicTrack, audio);
        SdlMixer.SetTrackLoops(_musicTrack, -1);
        _musicFade = 0f;
        _musicTrack.SetGain(0f);
        _musicTrack.Play(0);
    }

    /// <summary>Call once per frame: timers, music fade and volume changes.</summary>
    public void Update(float dt)
    {
        if (!Enabled) return;
        _time += dt;
        _musicFade = Math.Min(1f, _musicFade + dt / 1.5f);
        _musicTrack.SetGain(Muted ? 0f : MusicVolume * MasterVolume * _musicFade);
    }

    /// <summary>Turns this tick's simulation events into sounds.</summary>
    public void ConsumeEvents(SimState s)
    {
        if (!Enabled || Muted) return;
        const float inv = 1f / Fix.OneRaw;
        for (int i = 0; i < s.EventCount; i++)
        {
            ref readonly var e = ref s.Events[i];
            float x = e.Pos.X.Raw * inv;
            switch (e.Type)
            {
                case SimEventType.Placed: Play(Sfx.Place, x); break;
                case SimEventType.Fired:
                    Play((PatternKind)e.Other switch
                    {
                        PatternKind.Wall => Sfx.FireLarge,
                        PatternKind.Sweep => Sfx.FireSweep,
                        PatternKind.Homing => Sfx.FireHoming,
                        PatternKind.Mines => Sfx.FireMines,
                        PatternKind.Spiral => Sfx.FireSmall2,
                        _ => Sfx.FireSmall,
                    }, x);
                    break;
                case SimEventType.MineBurst: Play(Sfx.MineBurst, x); break;
                case SimEventType.Rejected: Play(Sfx.Rejected, x); break;
                case SimEventType.Graze: Play(Sfx.Graze, x); break;
                case SimEventType.Flip: Play(Sfx.Flip, x); break;
                case SimEventType.FlipBlock: Play(Sfx.FlipBlock, x); break;
                case SimEventType.Break: Play(Sfx.Break, x); Play(Sfx.BreakField, x); break;
                case SimEventType.Overdrive: Play(Sfx.Overdrive, x); break;
                case SimEventType.Hit: Play(Sfx.Hit, x); break;
                case SimEventType.Death: Play(Sfx.Death, x); break;
                case SimEventType.RoundStart: Play(Sfx.RoundStart); break;
                case SimEventType.MatchEnd: Play(Sfx.MatchEnd); break;
                case SimEventType.RoundEnd: Play(Sfx.RoundEnd); break;
            }
        }
    }

    public void Dispose()
    {
        if (!Enabled) return;
        SdlMixer.StopAllTracks(_mixer, 0);
        SdlMixer.DestroyMixer(_mixer);
        SdlMixer.Quit();
        Sdl.Init.QuitSubSystem(SdlInitFlags.Audio);
    }
}
