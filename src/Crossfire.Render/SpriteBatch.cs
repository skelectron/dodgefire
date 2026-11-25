using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Brutal.ShaderCApi;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Abstractions;

namespace Crossfire.Render;

public enum SpriteShape
{
    Bullet = 0,
    Ring = 1,
    Rect = 2,
    Glow = 3,
    Arc = 4,
    Diamond = 5,
    Cross = 6,
    Disc = 7,
    /// <summary>Atlas texture region (UV), multiplied by the tint colour.</summary>
    Textured = 8,
    /// <summary>Procedural animated nebula (fbm noise). P0 = time in seconds, P1 = palette (0 = blue, 1 = red).</summary>
    Nebula = 9,
    /// <summary>Darkening toward the quad's edges; colour = vignette colour, A = strength.</summary>
    Vignette = 10,
}

/// <summary>Per-instance data uploaded to the GPU (64 bytes, matches sprite.vert inputs).</summary>
[StructLayout(LayoutKind.Sequential)]
public struct SpriteInstance
{
    public float X, Y;
    public float HalfW, HalfH;
    public float R, G, B, A;
    public float Rotation, Shape, P0, P1;
    public float U0, V0, U1, V1;
}

/// <summary>Straight-alpha colour helper.</summary>
public readonly record struct Color4(float R, float G, float B, float A = 1f)
{
    public Color4 WithAlpha(float a) => this with { A = a };
    public Color4 Scale(float k) => new(R * k, G * k, B * k, A);
    public static Color4 Lerp(Color4 a, Color4 b, float t) => new(a.R + (b.R - a.R) * t, a.G + (b.G - a.G) * t, a.B + (b.B - a.B) * t, a.A + (b.A - a.A) * t);
    public uint ToImGui() => (uint)(Math.Clamp(A, 0, 1) * 255) << 24 | (uint)(Math.Clamp(B, 0, 1) * 255) << 16 | (uint)(Math.Clamp(G, 0, 1) * 255) << 8 | (uint)(Math.Clamp(R, 0, 1) * 255);
}

/// <summary>
/// Instanced SDF sprite renderer: every sprite in a frame goes into one persistently-mapped
/// instance buffer and is drawn with a single instanced draw call (6 vertices per instance).
/// </summary>
public sealed unsafe class SpriteBatch : IDisposable
{
    [StructLayout(LayoutKind.Sequential)]
    private struct PushConstants
    {
        public float ScaleX, ScaleY, OffsetX, OffsetY, PixelsPerUnit;
    }

    private readonly VulkanContext _vk;
    private readonly TextureAtlas _atlas;
    private readonly VkPipelineLayout _layout;
    private VkPipeline _pipeline;
    private readonly VkShaderModule _vert, _frag;

    private readonly VkBuffer[] _buffers = new VkBuffer[VulkanContext.FramesInFlight];
    private readonly VkDeviceMemory[] _memory = new VkDeviceMemory[VulkanContext.FramesInFlight];
    private readonly SpriteInstance*[] _mapped = new SpriteInstance*[VulkanContext.FramesInFlight];

    private SpriteInstance* _write;
    private int _frameSlot;

    public int Capacity { get; }
    public int Count { get; private set; }
    /// <summary>Instances dropped this frame because the buffer was full.</summary>
    public int Dropped { get; private set; }

    public SpriteBatch(VulkanContext vk, TextureAtlas atlas, int capacity = 131_072)
    {
        _vk = vk;
        _atlas = atlas;
        Capacity = capacity;
        for (int i = 0; i < VulkanContext.FramesInFlight; i++)
        {
            var (buf, mem, ptr) = vk.CreateMappedBuffer((long)capacity * sizeof(SpriteInstance), VkBufferUsageFlags.VertexBufferBit);
            _buffers[i] = buf;
            _memory[i] = mem;
            _mapped[i] = (SpriteInstance*)ptr;
        }

        _vert = ShaderCompiler.Load(vk.Device, "sprite.vert", ShaderKind.VertexShader);
        _frag = ShaderCompiler.Load(vk.Device, "sprite.frag", ShaderKind.FragmentShader);

        var push = new VkPushConstantRange
        {
            StageFlags = VkShaderStageFlags.VertexBit,
            Offset = new Brutal.ByteSize32(0),
            Size = new Brutal.ByteSize32((uint)sizeof(PushConstants)),
        };
        var setLayout = atlas.SetLayout;
        _layout = DeviceExtensions.CreatePipelineLayout(vk.Device, new ReadOnlySpan<VkDescriptorSetLayout>(&setLayout, 1), new ReadOnlySpan<VkPushConstantRange>(&push, 1), null);
        CreatePipeline();
    }

    /// <summary>Builds the pipeline against the context's current render pass.</summary>
    public void CreatePipeline()
    {
        if (_pipeline.VkHandle != 0) _vk.Device.DestroyPipeline(_pipeline, null);

        byte* entry = stackalloc byte[] { (byte)'m', (byte)'a', (byte)'i', (byte)'n', 0 };
        var stages = stackalloc VkPipelineShaderStageCreateInfo[2];
        stages[0] = new VkPipelineShaderStageCreateInfo { Stage = VkShaderStageFlags.VertexBit, Module = _vert, Name = entry };
        stages[1] = new VkPipelineShaderStageCreateInfo { Stage = VkShaderStageFlags.FragmentBit, Module = _frag, Name = entry };

        var binding = new VkVertexInputBindingDescription
        {
            Binding = 0,
            Stride = new Brutal.ByteSize32((uint)sizeof(SpriteInstance)),
            InputRate = VkVertexInputRate.Instance,
        };
        var attrs = stackalloc VkVertexInputAttributeDescription[5];
        attrs[0] = new VkVertexInputAttributeDescription { Location = 0, Binding = 0, Format = VkFormat.R32G32SFloat, Offset = new Brutal.ByteSize32(0) };
        attrs[1] = new VkVertexInputAttributeDescription { Location = 1, Binding = 0, Format = VkFormat.R32G32SFloat, Offset = new Brutal.ByteSize32(8) };
        attrs[2] = new VkVertexInputAttributeDescription { Location = 2, Binding = 0, Format = VkFormat.R32G32B32A32SFloat, Offset = new Brutal.ByteSize32(16) };
        attrs[3] = new VkVertexInputAttributeDescription { Location = 3, Binding = 0, Format = VkFormat.R32G32B32A32SFloat, Offset = new Brutal.ByteSize32(32) };
        attrs[4] = new VkVertexInputAttributeDescription { Location = 4, Binding = 0, Format = VkFormat.R32G32B32A32SFloat, Offset = new Brutal.ByteSize32(48) };
        var vertexInput = new VkPipelineVertexInputStateCreateInfo
        {
            VertexBindingDescriptionCount = 1,
            VertexBindingDescriptions = &binding,
            VertexAttributeDescriptionCount = 5,
            VertexAttributeDescriptions = attrs,
        };

        var viewport = new VkPipelineViewportStateCreateInfo { ViewportCount = 1, ScissorCount = 1 };
        var dynamicStates = stackalloc VkDynamicState[] { VkDynamicState.Viewport, VkDynamicState.Scissor };
        var dynamic = new VkPipelineDynamicStateCreateInfo { DynamicStateCount = 2, DynamicStates = dynamicStates };

        // Standard straight-alpha blending.
        var blendAttachment = new VkPipelineColorBlendAttachmentState
        {
            BlendEnable = new Brutal.Bool32(true),
            SrcColorBlendFactor = VkBlendFactor.SrcAlpha,
            DstColorBlendFactor = VkBlendFactor.OneMinusSrcAlpha,
            ColorBlendOp = VkBlendOp.Add,
            SrcAlphaBlendFactor = VkBlendFactor.One,
            DstAlphaBlendFactor = VkBlendFactor.OneMinusSrcAlpha,
            AlphaBlendOp = VkBlendOp.Add,
            ColorWriteMask = VkColorComponentFlags.RBit | VkColorComponentFlags.GBit | VkColorComponentFlags.BBit | VkColorComponentFlags.ABit,
        };
        var blend = new VkPipelineColorBlendStateCreateInfo { AttachmentCount = 1, Attachments = &blendAttachment };

        var info = new VkGraphicsPipelineCreateInfo
        {
            StageCount = 2,
            Stages = stages,
            VertexInputState = &vertexInput,
            InputAssemblyState = Presets.InputAssembly.TriangleList.Ptr.Pointer,
            ViewportState = &viewport,
            RasterizationState = Presets.Rasterization.Fill.CullNone.Ptr.Pointer,
            MultisampleState = Presets.Multisample.None.Ptr.Pointer,
            DepthStencilState = Presets.DepthStencil.NoDepthTest.Ptr.Pointer,
            ColorBlendState = &blend,
            DynamicState = &dynamic,
            Layout = _layout,
            RenderPass = _vk.RenderPass,
            Subpass = 0,
        };
        _pipeline = DeviceExtensions.CreateGraphicsPipeline(_vk.Device, default, info, null);
    }

    public void Begin(int frameSlot)
    {
        _frameSlot = frameSlot;
        _write = _mapped[frameSlot];
        Count = 0;
        Dropped = 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Add(float x, float y, float halfW, float halfH, Color4 c, SpriteShape shape, float p0 = 0, float p1 = 0, float rotation = 0)
    {
        if (Count >= Capacity) { Dropped++; return; }
        SpriteInstance* s = _write + Count++;
        s->X = x; s->Y = y; s->HalfW = halfW; s->HalfH = halfH;
        s->R = c.R; s->G = c.G; s->B = c.B; s->A = c.A;
        s->Rotation = rotation; s->Shape = (float)shape; s->P0 = p0; s->P1 = p1;
        s->U0 = 0; s->V0 = 0; s->U1 = 0; s->V1 = 0;
    }

    /// <summary>
    /// Draws an atlas region centred at (x, y). <paramref name="height"/> is the on-screen height in
    /// arena units; width follows the source aspect ratio. Returns false if the region is missing.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Sprite(in SpriteRegion r, float x, float y, float height, Color4 tint, float rotation = 0, float widthScale = 1f)
    {
        if (!r.IsValid) return false;
        if (Count >= Capacity) { Dropped++; return true; }
        float halfH = height * 0.5f;
        float halfW = halfH * r.Width / r.Height * widthScale;
        SpriteInstance* s = _write + Count++;
        s->X = x; s->Y = y; s->HalfW = halfW; s->HalfH = halfH;
        s->R = tint.R; s->G = tint.G; s->B = tint.B; s->A = tint.A;
        s->Rotation = rotation; s->Shape = (float)SpriteShape.Textured; s->P0 = 0; s->P1 = 0;
        s->U0 = r.U0; s->V0 = r.V0; s->U1 = r.U1; s->V1 = r.V1;
        return true;
    }

    public void Circle(float x, float y, float radius, Color4 c, SpriteShape shape = SpriteShape.Disc, float p0 = 0, float p1 = 0)
        => Add(x, y, radius, radius, c, shape, p0, p1);

    /// <summary>Axis-aligned rectangle from top-left corner and size.</summary>
    public void Rect(float x, float y, float w, float h, Color4 c)
        => Add(x + w * 0.5f, y + h * 0.5f, w * 0.5f, h * 0.5f, c, SpriteShape.Rect);

    /// <summary>Records the single instanced draw for everything added since <see cref="Begin"/>.</summary>
    public void Flush(CommandBuffer cmd, in ArenaView view)
    {
        if (Count == 0) return;
        cmd.BindPipeline(VkPipelineBindPoint.Graphics, _pipeline);
        var set = _atlas.Set;
        cmd.BindDescriptorSets(VkPipelineBindPoint.Graphics, _layout, 0, new ReadOnlySpan<VkDescriptorSet>(&set, 1), Span<Brutal.ByteSize32>.Empty);
        var pc = new PushConstants
        {
            ScaleX = view.Scale * 2f / view.FramebufferWidth,
            ScaleY = view.Scale * 2f / view.FramebufferHeight,
            OffsetX = view.OffsetX * 2f / view.FramebufferWidth - 1f,
            OffsetY = view.OffsetY * 2f / view.FramebufferHeight - 1f,
            PixelsPerUnit = view.Scale,
        };
        cmd.PushConstants(_layout, VkShaderStageFlags.VertexBit, 0, new ReadOnlySpan<byte>(&pc, sizeof(PushConstants)));
        // Clip to the arena so bullets past the outer edges never bleed into the HUD bands.
        int x0 = Math.Max(0, (int)MathF.Floor(view.OffsetX)), y0 = Math.Max(0, (int)MathF.Floor(view.OffsetY));
        int x1 = Math.Min((int)view.FramebufferWidth, (int)MathF.Ceiling(view.OffsetX + view.ArenaWidth * view.Scale));
        int y1 = Math.Min((int)view.FramebufferHeight, (int)MathF.Ceiling(view.OffsetY + view.ArenaHeight * view.Scale));
        var scissor = new VkRect2D { Offset = new VkOffset2D(x0, y0), Extent = new VkExtent2D(Math.Max(0, x1 - x0), Math.Max(0, y1 - y0)) };
        cmd.SetScissor(0, new ReadOnlySpan<VkRect2D>(&scissor, 1));
        VkBuffer buffer = _buffers[_frameSlot];
        Brutal.ByteSize64 offset = default;
        cmd.BindVertexBuffers(0, new ReadOnlySpan<VkBuffer>(&buffer, 1), new ReadOnlySpan<Brutal.ByteSize64>(&offset, 1));
        cmd.Draw(6, Count, 0, 0);
    }

    public void Dispose()
    {
        var d = _vk.Device;
        d.DestroyPipeline(_pipeline, null);
        d.DestroyPipelineLayout(_layout, null);
        d.DestroyShaderModule(_vert, null);
        d.DestroyShaderModule(_frag, null);
        for (int i = 0; i < VulkanContext.FramesInFlight; i++) _vk.DestroyBuffer(_buffers[i], _memory[i]);
    }
}

/// <summary>Maps arena units to framebuffer pixels (uniform scale, letterboxed and centred).</summary>
public readonly struct ArenaView
{
    public readonly float Scale, OffsetX, OffsetY;
    public readonly float FramebufferWidth, FramebufferHeight;
    public readonly float ArenaWidth, ArenaHeight;

    public ArenaView(float arenaW, float arenaH, float fbW, float fbH, float paddingTop = 0, float paddingBottom = 0, float shiftX = 0)
    {
        ArenaWidth = arenaW;
        ArenaHeight = arenaH;
        FramebufferWidth = fbW;
        FramebufferHeight = fbH;
        float availH = Math.Max(1, fbH - paddingTop - paddingBottom);
        Scale = Math.Min(fbW / arenaW, availH / arenaH);
        OffsetX = (fbW - arenaW * Scale) * 0.5f + shiftX;
        OffsetY = paddingTop + (availH - arenaH * Scale) * 0.5f;
    }

    private ArenaView(float scale, float ox, float oy, float fbW, float fbH, float arenaW, float arenaH, bool raw)
    {
        ArenaWidth = arenaW;
        ArenaHeight = arenaH;
        Scale = scale;
        OffsetX = ox;
        OffsetY = oy;
        FramebufferWidth = fbW;
        FramebufferHeight = fbH;
    }

    /// <summary>The same view expressed in another pixel space (e.g. framebuffer -> window units for ImGui).</summary>
    public ArenaView ScaledBy(float k) => new(Scale * k, OffsetX * k, OffsetY * k, FramebufferWidth * k, FramebufferHeight * k, ArenaWidth, ArenaHeight, raw: true);

    public float ToPixelX(float x) => OffsetX + x * Scale;
    public float ToPixelY(float y) => OffsetY + y * Scale;
    public float ToArenaX(float px) => (px - OffsetX) / Scale;
    public float ToArenaY(float py) => (py - OffsetY) / Scale;
}
