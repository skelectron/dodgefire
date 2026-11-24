using Brutal.ImGuiApi;

namespace Crossfire.Render;

/// <summary>
/// Kenney "Space Shooter Remastered" sprites (CC0) used by the game, plus the Kenney Future font.
/// Any missing file leaves an invalid region, and the renderer falls back to SDF shapes.
/// </summary>
public sealed class GameArt
{
    public const string SpriteDir = "assets/kenney/space-shooter";
    public const string FontPath = "assets/kenney/fonts/kenney-future.ttf";

    // [team, variant]: team 0 = blue/green ships, team 1 = red/orange ships.
    private static readonly string[,] ShipNames = { { "playership1_blue", "playership1_green" }, { "playership1_red", "playership1_orange" } };
    private static readonly string[,] LifeNames = { { "playerlife1_blue", "playerlife1_green" }, { "playerlife1_red", "playerlife1_orange" } };

    public static IEnumerable<string> SpriteFiles(string baseDir)
    {
        string dir = Path.Combine(baseDir, SpriteDir);
        foreach (var n in ShipNames) yield return Path.Combine(dir, n + ".png");
        foreach (var n in LifeNames) yield return Path.Combine(dir, n + ".png");
        foreach (var n in new[] { "shield1", "star1", "star2", "star3", "fire00", "fire01", "fire02", "fire03", "darkpurple" })
            yield return Path.Combine(dir, n + ".png");
    }

    public readonly SpriteRegion[,] Ship = new SpriteRegion[2, 2];
    public readonly SpriteRegion[,] Life = new SpriteRegion[2, 2];
    public readonly SpriteRegion Shield, Background;
    public readonly SpriteRegion[] Stars = new SpriteRegion[3];
    public readonly SpriteRegion[] Fire = new SpriteRegion[4];

    /// <summary>The atlas registered with ImGui (for HUD icons). Default when unavailable.</summary>
    public ImTextureRef AtlasTexture { get; set; }
    public bool HasAtlasTexture { get; set; }
    /// <summary>Kenney Future font for HUD/banners/menu; null pointer means use the default font.</summary>
    public ImFontPtr HudFont { get; set; }

    public GameArt(TextureAtlas atlas)
    {
        for (int t = 0; t < 2; t++)
        for (int v = 0; v < 2; v++)
        {
            Ship[t, v] = atlas.Get(ShipNames[t, v]);
            Life[t, v] = atlas.Get(LifeNames[t, v]);
        }
        Shield = atlas.Get("shield1");
        Background = atlas.Get("darkpurple");
        for (int i = 0; i < 3; i++) Stars[i] = atlas.Get($"star{i + 1}");
        for (int i = 0; i < 4; i++) Fire[i] = atlas.Get($"fire0{i}");
    }

    public ImFontPtr Font => HudFont.IsNull() ? ImGui.GetFont() : HudFont;
}
