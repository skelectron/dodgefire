using Brutal.GlfwApi;
using Brutal.ImGuiApi;
using Brutal.ImGuiApi.Backends;
using Brutal.ImPlotApi;
using Brutal.VulkanApi;

namespace Crossfire.Render;

/// <summary>Dear ImGui + ImPlot with the BRUTAL GLFW platform and Vulkan renderer backends.</summary>
public sealed class ImGuiLayer : IDisposable
{
    private readonly ImGuiContextPtr _context;
    private readonly ImPlotContextPtr _plotContext;
    private readonly GlfwBackend _platform;
    private readonly VulkanBackend _renderer;
    private bool _frameOpen;

    public ImGuiLayer(GlfwWindow window, VulkanContext vk)
    {
        _context = ImGui.CreateContext(default);
        _plotContext = ImPlot.CreateContext();
        ImGui.StyleColorsDark(default);

        _platform = new GlfwBackend(window, GlfwBackend.ClientApi.Vulkan);
        var info = new VulkanBackend.CreateInfo
        {
            Device = vk.Device,
            GraphicsQueue = vk.Queue,
            RenderPass = vk.RenderPass,
            SubPass = 0,
            MinImageCount = Math.Max(2, vk.MinImageCount),
            ImageCount = Math.Max(2, vk.ImageCount),
            SampleCount = VkSampleCountFlags._1Bit,
            DescriptorPoolSize = 64,
            MinAllocationSize = new Brutal.ByteSize((nuint)(1024 * 1024)),
        };
        _renderer = new VulkanBackend(in info);
    }

    public bool WantsKeyboard => ImGui.GetIO().WantCaptureKeyboard;

    public void NewFrame()
    {
        _renderer.NewFrame();
        _platform.NewFrame();
        ImGui.NewFrame();
        _frameOpen = true;
    }

    /// <summary>Finalises the ImGui frame and records its draw data into <paramref name="cmd"/>.</summary>
    public void Render(CommandBuffer cmd)
    {
        ImGui.Render();
        _frameOpen = false;
        _renderer.RenderDrawData(ImGui.GetDrawData(), cmd);
    }

    /// <summary>Closes the frame without drawing (used when a swapchain frame is skipped).</summary>
    public void DiscardFrame()
    {
        if (!_frameOpen) return;
        ImGui.Render();
        _frameOpen = false;
    }

    public void Dispose()
    {
        _renderer.Dispose();
        _platform.Dispose();
        ImPlot.DestroyContext(_plotContext);
        ImGui.DestroyContext(_context);
    }
}
