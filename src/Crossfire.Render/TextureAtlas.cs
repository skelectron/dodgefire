using Brutal.StbApi;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Abstractions;

namespace Crossfire.Render;

/// <summary>A sub-image of the atlas: normalised UVs plus its size in source pixels.</summary>
public readonly record struct SpriteRegion(float U0, float V0, float U1, float V1, int Width, int Height)
{
    public bool IsValid => Width > 0;
}

/// <summary>
/// Loads PNG sprites (Brutal.Stb), shelf-packs them into one RGBA texture at startup and uploads it
/// to the GPU. Exposes a descriptor set (binding 0 = combined image sampler) for the sprite pipeline.
/// Sprites are addressed by file name without extension, e.g. "playership1_blue".
/// </summary>
public sealed unsafe class TextureAtlas : IDisposable
{
    public const int Size = 1024;
    private const int Padding = 2;

    private readonly VulkanContext _vk;
    private readonly Dictionary<string, SpriteRegion> _regions = new(StringComparer.OrdinalIgnoreCase);

    private readonly GpuTexture _texture;
    public VkImage Image => _texture.Image;
    public VkDeviceMemory Memory => _texture.Memory;
    public VkImageView View => _texture.View;
    public VkSampler Sampler => _texture.Sampler;
    public VkDescriptorSetLayout SetLayout { get; }
    public VkDescriptorPool Pool { get; }
    public VkDescriptorSet Set { get; }

    /// <summary>Region for a white 4x4 block, handy for solid quads that must share the textured path.</summary>
    public SpriteRegion White { get; }

    public TextureAtlas(VulkanContext vk, IEnumerable<string> pngPaths)
    {
        _vk = vk;
        var pixels = new byte[Size * Size * 4];

        // --- Shelf packing (tallest first gives tighter shelves) -------------------------------
        var images = new List<(string Name, StbImage8 Img)>();
        foreach (var path in pngPaths)
        {
            // Missing/broken files are skipped: the renderer falls back to SDF shapes for absent regions.
            if (!File.Exists(path)) { Console.WriteLine($"[atlas] missing sprite: {path}"); continue; }
            var img = Stb.Load(path, 4);
            if (img.Data == null) { Console.WriteLine($"[atlas] could not decode: {path}"); continue; }
            images.Add((Path.GetFileNameWithoutExtension(path), img));
        }
        images.Sort((a, b) => b.Img.Height.CompareTo(a.Img.Height));

        int x = Padding, y = Padding, shelf = 0;
        // Reserve the white block first.
        Blit(pixels, null, 4, 4, x, y);
        White = Region(x + 1, y + 1, 2, 2); // inner texels only: no bilinear bleed
        x += 4 + Padding;
        shelf = 4;

        foreach (var (name, img) in images)
        {
            if (x + img.Width + Padding > Size)
            {
                x = Padding;
                y += shelf + Padding;
                shelf = 0;
            }
            if (y + img.Height + Padding > Size) throw new InvalidOperationException($"Atlas {Size}x{Size} is full; cannot fit '{name}'.");
            Blit(pixels, img.Data, img.Width, img.Height, x, y);
            _regions[name] = Region(x, y, img.Width, img.Height);
            x += img.Width + Padding;
            shelf = Math.Max(shelf, img.Height);
            img.Dispose();
        }

        // --- GPU image -------------------------------------------------------------------------
        _texture = new GpuTexture(vk, pixels, Size, Size);
        var device = vk.Device;

        // --- Descriptor set ---------------------------------------------------------------------
        var binding = new VkDescriptorSetLayoutBinding
        {
            Binding = 0,
            DescriptorType = VkDescriptorType.CombinedImageSampler,
            DescriptorCount = 1,
            StageFlags = VkShaderStageFlags.FragmentBit,
        };
        SetLayout = device.CreateDescriptorSetLayout(new VkDescriptorSetLayoutCreateInfo { BindingCount = 1, Bindings = &binding }, null);
        var poolSize = new VkDescriptorPoolSize { Type = VkDescriptorType.CombinedImageSampler, DescriptorCount = 1 };
        Pool = device.CreateDescriptorPool(new VkDescriptorPoolCreateInfo { MaxSets = 1, PoolSizeCount = 1, PoolSizes = &poolSize }, null);
        var layout = SetLayout;
        VkDescriptorSet set = default;
        device.AllocateDescriptorSets(new VkDescriptorSetAllocateInfo { DescriptorPool = Pool, DescriptorSetCount = 1, SetLayouts = &layout }, new ReadOnlySpan<VkDescriptorSet>(&set, 1));
        Set = set;
        var imageInfo = new VkDescriptorImageInfo { Sampler = Sampler, ImageView = View, ImageLayout = VkImageLayout.ShaderReadOnlyOptimal };
        var write = new VkWriteDescriptorSet
        {
            DstSet = Set,
            DstBinding = 0,
            DescriptorCount = 1,
            DescriptorType = VkDescriptorType.CombinedImageSampler,
            ImageInfo = &imageInfo,
        };
        device.UpdateDescriptorSets(new ReadOnlySpan<VkWriteDescriptorSet>(&write, 1), ReadOnlySpan<VkCopyDescriptorSet>.Empty);
    }

    public int Count => _regions.Count;

    public SpriteRegion Get(string name) => _regions.TryGetValue(name, out var r) ? r : default;

    private static SpriteRegion Region(int x, int y, int w, int h) =>
        new(x / (float)Size, y / (float)Size, (x + w) / (float)Size, (y + h) / (float)Size, w, h);

    private static void Blit(byte[] dst, byte* src, int w, int h, int x, int y)
    {
        for (int row = 0; row < h; row++)
        {
            int d = ((y + row) * Size + x) * 4;
            if (src == null)
            {
                dst.AsSpan(d, w * 4).Fill(255);
            }
            else
            {
                new ReadOnlySpan<byte>(src + row * w * 4, w * 4).CopyTo(dst.AsSpan(d, w * 4));
            }
        }
    }

    public void Dispose()
    {
        var d = _vk.Device;
        d.DestroyDescriptorPool(Pool, null);
        d.DestroyDescriptorSetLayout(SetLayout, null);
        _texture.Dispose();
    }
}
