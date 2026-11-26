using Brutal.StbApi;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Abstractions;

namespace Crossfire.Render;

/// <summary>
/// One RGBA8 texture on the GPU (device-local, linear clamped sampler), uploaded once from CPU pixels.
/// Used by <see cref="TextureAtlas"/> and for standalone images such as the BRUTAL logo.
/// </summary>
public sealed unsafe class GpuTexture : IDisposable
{
    private readonly VulkanContext _vk;

    public int Width { get; }
    public int Height { get; }
    public VkImage Image { get; }
    public VkDeviceMemory Memory { get; }
    public VkImageView View { get; }
    public VkSampler Sampler { get; }

    public GpuTexture(VulkanContext vk, ReadOnlySpan<byte> rgba, int width, int height)
    {
        if (rgba.Length != width * height * 4) throw new ArgumentException("pixel buffer size does not match width x height x 4");
        _vk = vk;
        Width = width;
        Height = height;
        var device = vk.Device;
        Image = device.CreateImage(new VkImageCreateInfo
        {
            ImageType = VkImageType._2D,
            Format = VkFormat.R8G8B8A8UNorm,
            Extent = new VkExtent3D(width, height, 1),
            MipLevels = 1,
            ArrayLayers = 1,
            Samples = VkSampleCountFlags._1Bit,
            Tiling = VkImageTiling.Optimal,
            Usage = VkImageUsageFlags.SampledBit | VkImageUsageFlags.TransferDstBit,
            SharingMode = VkSharingMode.Exclusive,
            InitialLayout = VkImageLayout.Undefined,
        }, null);
        Memory = DeviceExtensions.AllocateMemory(device, vk.PhysicalDevice, Image, VkMemoryPropertyFlags.DeviceLocalBit, null);
        device.BindImageMemory(Image, Memory, default);

        // Upload via a staging buffer.
        var (staging, stagingMem, mapped) = vk.CreateMappedBuffer(rgba.Length, VkBufferUsageFlags.TransferSrcBit);
        rgba.CopyTo(new Span<byte>((void*)mapped, rgba.Length));
        var image = Image;
        int w = width, h = height;
        vk.ImmediateSubmit(cmd =>
        {
            var range = new VkImageSubresourceRange { AspectMask = VkImageAspectFlags.ColorBit, LevelCount = 1, LayerCount = 1 };
            var toTransfer = new VkImageMemoryBarrier
            {
                SrcAccessMask = default,
                DstAccessMask = VkAccessFlags.TransferWriteBit,
                OldLayout = VkImageLayout.Undefined,
                NewLayout = VkImageLayout.TransferDstOptimal,
                SrcQueueFamilyIndex = VK.QUEUE_FAMILY_IGNORED,
                DstQueueFamilyIndex = VK.QUEUE_FAMILY_IGNORED,
                Image = image,
                SubresourceRange = range,
            };
            cmd.PipelineBarrier(VkPipelineStageFlags.TopOfPipeBit, VkPipelineStageFlags.TransferBit, default,
                ReadOnlySpan<VkMemoryBarrier>.Empty, ReadOnlySpan<VkBufferMemoryBarrier>.Empty, new ReadOnlySpan<VkImageMemoryBarrier>(&toTransfer, 1));

            var copy = new VkBufferImageCopy
            {
                ImageSubresource = new VkImageSubresourceLayers { AspectMask = VkImageAspectFlags.ColorBit, LayerCount = 1 },
                ImageExtent = new VkExtent3D(w, h, 1),
            };
            cmd.CopyBufferToImage(staging, image, VkImageLayout.TransferDstOptimal, new ReadOnlySpan<VkBufferImageCopy>(&copy, 1));

            var toShader = toTransfer with
            {
                SrcAccessMask = VkAccessFlags.TransferWriteBit,
                DstAccessMask = VkAccessFlags.ShaderReadBit,
                OldLayout = VkImageLayout.TransferDstOptimal,
                NewLayout = VkImageLayout.ShaderReadOnlyOptimal,
            };
            cmd.PipelineBarrier(VkPipelineStageFlags.TransferBit, VkPipelineStageFlags.FragmentShaderBit, default,
                ReadOnlySpan<VkMemoryBarrier>.Empty, ReadOnlySpan<VkBufferMemoryBarrier>.Empty, new ReadOnlySpan<VkImageMemoryBarrier>(&toShader, 1));
        });
        vk.DestroyBuffer(staging, stagingMem);

        View = device.CreateImageView(new VkImageViewCreateInfo
        {
            Image = Image,
            ViewType = VkImageViewType._2D,
            Format = VkFormat.R8G8B8A8UNorm,
            Components = new VkComponentMapping { R = VkComponentSwizzle.Identity, G = VkComponentSwizzle.Identity, B = VkComponentSwizzle.Identity, A = VkComponentSwizzle.Identity },
            SubresourceRange = new VkImageSubresourceRange { AspectMask = VkImageAspectFlags.ColorBit, LevelCount = 1, LayerCount = 1 },
        }, null);
        Sampler = device.CreateSampler(Presets.Sampler.SamplerLinearClamped, null);
    }

    /// <summary>
    /// Loads a PNG, shrinking it by an integer box filter until it is at most <paramref name="maxWidth"/> wide
    /// (pure scaling: alpha-weighted averaging, so edges against transparency do not darken). Null if missing.
    /// </summary>
    public static GpuTexture? LoadPng(VulkanContext vk, string path, int maxWidth = int.MaxValue)
    {
        if (!File.Exists(path)) return null;
        var img = Stb.Load(path, 4);
        if (img.Data == null) return null;
        try
        {
            int w = img.Width, h = img.Height;
            int factor = 1;
            while (w / factor > maxWidth && w % (factor * 2) == 0 && h % (factor * 2) == 0) factor *= 2;
            var src = new ReadOnlySpan<byte>(img.Data, w * h * 4);
            if (factor == 1) return new GpuTexture(vk, src, w, h);
            var dst = BoxDownscale(src, w, h, factor);
            return new GpuTexture(vk, dst, w / factor, h / factor);
        }
        finally
        {
            img.Dispose();
        }
    }

    /// <summary>Averages each factor×factor block; colour is weighted by alpha (straight-alpha output).</summary>
    public static byte[] BoxDownscale(ReadOnlySpan<byte> src, int w, int h, int factor)
    {
        int dw = w / factor, dh = h / factor;
        var dst = new byte[dw * dh * 4];
        int n = factor * factor;
        for (int y = 0; y < dh; y++)
            for (int x = 0; x < dw; x++)
            {
                long r = 0, g = 0, b = 0, a = 0;
                for (int sy = 0; sy < factor; sy++)
                {
                    int row = ((y * factor + sy) * w + x * factor) * 4;
                    for (int sx = 0; sx < factor; sx++)
                    {
                        int i = row + sx * 4;
                        int pa = src[i + 3];
                        r += src[i] * pa;
                        g += src[i + 1] * pa;
                        b += src[i + 2] * pa;
                        a += pa;
                    }
                }
                int o = (y * dw + x) * 4;
                if (a > 0)
                {
                    dst[o] = (byte)((r + a / 2) / a);
                    dst[o + 1] = (byte)((g + a / 2) / a);
                    dst[o + 2] = (byte)((b + a / 2) / a);
                }
                dst[o + 3] = (byte)((a + n / 2) / n);
            }
        return dst;
    }

    public void Dispose()
    {
        var d = _vk.Device;
        d.DestroySampler(Sampler, null);
        d.DestroyImageView(View, null);
        d.DestroyImage(Image, null);
        d.FreeMemory(Memory, null);
    }
}
