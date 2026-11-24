using System.Runtime.InteropServices;
using Brutal.GlfwApi;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Abstractions;

namespace Crossfire.Render;

/// <summary>
/// Owns the Vulkan instance, device, swapchain, render pass and per-frame synchronisation.
/// Usage per frame: <see cref="BeginFrame"/> (returns a command buffer inside the render pass),
/// record draws, then <see cref="EndFrame"/>.
/// </summary>
public sealed unsafe class VulkanContext : IDisposable
{
    public const int FramesInFlight = 2;

    private readonly GlfwWindow _window;
    private readonly bool _vsync;

    public Instance Instance { get; }
    public VkSurfaceKHR Surface { get; }
    public PhysicalDevice PhysicalDevice { get; }
    public DeviceEx Device { get; }
    public Queue Queue { get; }
    public int QueueFamily { get; }
    public string DeviceName { get; }
    public bool ValidationEnabled { get; }

    public VkFormat SwapchainFormat { get; private set; }
    public VkExtent2D Extent { get; private set; }
    public VkRenderPass RenderPass { get; private set; }
    public int ImageCount => _images.Length;
    public int MinImageCount { get; private set; }

    private VkSwapchainKHR _swapchain;
    private VkImage[] _images = [];
    private VkImageView[] _views = [];
    private VkFramebuffer[] _framebuffers = [];
    private VkSemaphore[] _renderFinished = [];

    private readonly VkCommandPool _commandPool;
    private readonly CommandBuffer[] _commandBuffers = new CommandBuffer[FramesInFlight];
    private readonly VkSemaphore[] _imageAvailable = new VkSemaphore[FramesInFlight];
    private readonly VkFence[] _inFlight = new VkFence[FramesInFlight];

    private int _frame;
    private int _imageIndex;
    private bool _resizeRequested;

    /// <summary>Index (0..FramesInFlight-1) of the frame currently being recorded.</summary>
    public int FrameIndex => _frame;

    public VulkanContext(GlfwWindow window, bool enableValidation, bool vsync = true)
    {
        _window = window;
        _vsync = vsync;

        // --- Instance -------------------------------------------------------------------------
        var extensions = new HashSet<string>();
        byte** required = Glfw.GetRequiredInstanceExtensions(out uint requiredCount);
        for (uint i = 0; i < requiredCount; i++) extensions.Add(Marshal.PtrToStringUTF8((nint)required[i])!);
        VulkanHelpers.AddPlatformRequiredExtensions(extensions);

        var layers = new HashSet<string>();
        ValidationEnabled = enableValidation && VulkanHelpers.Validation.IsSupported;
        if (ValidationEnabled)
        {
            VulkanHelpers.AddValidationLayers(layers);
            VulkanHelpers.AddDebugExtensions(extensions);
        }

        fixed (byte* appName = "Crossfire"u8)
        fixed (byte* engineName = "BRUTAL"u8)
        {
            Instance = Vulkan.CreateInstance(new Instance.CreateInfo
            {
                AppInfo = new VkApplicationInfo
                {
                    ApplicationName = appName,
                    ApplicationVersion = Vulkan.MakeVersion(0, 1, 0),
                    EngineName = engineName,
                    EngineVersion = Vulkan.MakeVersion(1, 0, 0),
                    ApiVersion = Vulkan.MakeApiVersion(0, 1, 3, 0),
                },
                Flags = VulkanHelpers.GetPlatformRequiredInstanceCreateFlags(),
                EnabledExtensions = extensions,
                EnabledLayers = layers,
            });
        }

        // --- Surface --------------------------------------------------------------------------
        int surfaceResult = Glfw.CreateWindowSurface(Instance.VkHandle, window, out ulong surface);
        if (surfaceResult != 0) throw new Exception($"glfwCreateWindowSurface failed: {surfaceResult}");
        Surface = new VkSurfaceKHR((nint)surface);

        // --- Physical device + queue family ---------------------------------------------------
        (PhysicalDevice, QueueFamily) = PickPhysicalDevice();
        DeviceName = VulkanDeviceInfo.GetDeviceName(PhysicalDevice);

        // --- Logical device -------------------------------------------------------------------
        float priority = 1f;
        var queueInfo = new VkDeviceQueueCreateInfo
        {
            QueueFamilyIndex = QueueFamily,
            QueueCount = 1,
            QueuePriorities = &priority,
        };
        var deviceExtensions = new HashSet<string> { VK.KHR_SWAPCHAIN_EXTENSION_NAME };
        var deviceInfo = new DeviceEx.CreateInfo
        {
            Extensions = deviceExtensions,
            QueueCreateInfos = new ReadOnlySpan<VkDeviceQueueCreateInfo>(&queueInfo, 1),
        };
        Device = PhysicalDeviceExtensions.CreateDevice(PhysicalDevice, deviceInfo);
        Queue = Device.GetQueue(QueueFamily, 0);

        // --- Command buffers + sync -----------------------------------------------------------
        var poolInfo = new VkCommandPoolCreateInfo
        {
            Flags = VkCommandPoolCreateFlags.ResetCommandBufferBit,
            QueueFamilyIndex = QueueFamily,
        };
        _commandPool = Device.CreateCommandPool(poolInfo, null);
        DeviceExtensions.AllocateCommandBuffers(Device, new VkCommandBufferAllocateInfo
        {
            CommandPool = _commandPool,
            Level = VkCommandBufferLevel.Primary,
        }, _commandBuffers);

        for (int i = 0; i < FramesInFlight; i++)
        {
            _imageAvailable[i] = Device.CreateSemaphore(new VkSemaphoreCreateInfo(), null);
            _inFlight[i] = Device.CreateFence(new VkFenceCreateInfo { Flags = VkFenceCreateFlags.SignaledBit }, null);
        }

        CreateSwapchain();
    }

    private (PhysicalDevice, int) PickPhysicalDevice()
    {
        PhysicalDevice? best = null;
        int bestFamily = -1, bestScore = -1;
        foreach (var pd in Instance.PhysicalDevices)
        {
            if (!pd.SupportsExtension(VK.KHR_SWAPCHAIN_EXTENSION_NAME)) continue;
            int count = pd.GetQueueFamilyProperties_Count();
            var families = new VkQueueFamilyProperties[count];
            pd.GetQueueFamilyProperties_Write(families);
            int family = -1;
            for (int i = 0; i < count; i++)
            {
                if ((families[i].QueueFlags & VkQueueFlags.GraphicsBit) != 0 && pd.GetSurfaceSupportKHR(i, Surface))
                {
                    family = i;
                    break;
                }
            }
            if (family < 0) continue;
            var props = pd.GetProperties();
            int score = props.DeviceType switch
            {
                VkPhysicalDeviceType.DiscreteGpu => 3,
                VkPhysicalDeviceType.IntegratedGpu => 2,
                _ => 1,
            };
            if (score > bestScore)
            {
                best = pd;
                bestFamily = family;
                bestScore = score;
            }
        }
        return best is null
            ? throw new NotSupportedException("No Vulkan device with graphics + present support was found.")
            : (best, bestFamily);
    }

    private void CreateSwapchain()
    {
        var caps = PhysicalDevice.GetSurfaceCapabilitiesKHR(Surface);

        // Format: prefer BGRA8 UNORM (colours authored in sRGB space are written as-is).
        int formatCount = PhysicalDevice.GetSurfaceFormatsKHR_Count(Surface);
        var formats = new VkSurfaceFormatKHR[formatCount];
        PhysicalDevice.GetSurfaceFormatsKHR_Write(Surface, formats);
        var chosen = formats[0];
        foreach (var f in formats)
        {
            if (f.Format is VkFormat.B8G8R8A8UNorm or VkFormat.R8G8B8A8UNorm && f.ColorSpace == VkColorSpaceKHR.SRGBNonlinearKHR)
            {
                chosen = f;
                break;
            }
        }
        bool formatChanged = SwapchainFormat != chosen.Format;
        SwapchainFormat = chosen.Format;

        // Present mode: FIFO (vsync) is always available; otherwise prefer mailbox, then immediate.
        var presentMode = VkPresentModeKHR.FifoKHR;
        if (!_vsync)
        {
            int modeCount = PhysicalDevice.GetSurfacePresentModesKHR_Count(Surface);
            var modes = new VkPresentModeKHR[modeCount];
            PhysicalDevice.GetSurfacePresentModesKHR_Write(Surface, modes);
            if (modes.Contains(VkPresentModeKHR.MailboxKHR)) presentMode = VkPresentModeKHR.MailboxKHR;
            else if (modes.Contains(VkPresentModeKHR.ImmediateKHR)) presentMode = VkPresentModeKHR.ImmediateKHR;
        }

        VkExtent2D extent = caps.CurrentExtent;
        if (extent.Width == -1) // 0xFFFFFFFF: the surface size is determined by the swapchain.
        {
            var fb = _window.FramebufferSize;
            extent = new VkExtent2D(
                Math.Clamp(fb.X, caps.MinImageExtent.Width, caps.MaxImageExtent.Width),
                Math.Clamp(fb.Y, caps.MinImageExtent.Height, caps.MaxImageExtent.Height));
        }
        Extent = extent;

        int imageCount = caps.MinImageCount + 1;
        if (caps.MaxImageCount > 0) imageCount = Math.Min(imageCount, caps.MaxImageCount);
        MinImageCount = caps.MinImageCount;

        var old = _swapchain;
        var info = new VkSwapchainCreateInfoKHR
        {
            Surface = Surface,
            MinImageCount = imageCount,
            ImageFormat = chosen.Format,
            ImageColorSpace = chosen.ColorSpace,
            ImageExtent = extent,
            ImageArrayLayers = 1,
            ImageUsage = VkImageUsageFlags.ColorAttachmentBit,
            ImageSharingMode = VkSharingMode.Exclusive,
            PreTransform = caps.CurrentTransform,
            CompositeAlpha = VkCompositeAlphaFlagsKHR.OpaqueBitKHR,
            PresentMode = presentMode,
            Clipped = new Brutal.Bool32(true),
            OldSwapchain = old,
        };
        _swapchain = Device.CreateSwapchainKHR(info, null);
        if (old.VkHandle != 0) Device.DestroySwapchainKHR(old, null);

        int count = Device.GetSwapchainImagesKHR_Count(_swapchain);
        _images = new VkImage[count];
        Device.GetSwapchainImagesKHR_Write(_swapchain, _images);

        if (RenderPass.VkHandle == 0 || formatChanged)
        {
            if (RenderPass.VkHandle != 0) Device.DestroyRenderPass(RenderPass, null);
            RenderPass = CreateRenderPass(chosen.Format);
        }

        _views = new VkImageView[count];
        _framebuffers = new VkFramebuffer[count];
        _renderFinished = new VkSemaphore[count];
        for (int i = 0; i < count; i++)
        {
            _views[i] = Device.CreateImageView(new VkImageViewCreateInfo
            {
                Image = _images[i],
                ViewType = VkImageViewType._2D,
                Format = chosen.Format,
                Components = new VkComponentMapping
                {
                    R = VkComponentSwizzle.Identity, G = VkComponentSwizzle.Identity,
                    B = VkComponentSwizzle.Identity, A = VkComponentSwizzle.Identity,
                },
                SubresourceRange = new VkImageSubresourceRange
                {
                    AspectMask = VkImageAspectFlags.ColorBit,
                    LevelCount = 1,
                    LayerCount = 1,
                },
            }, null);

            VkImageView view = _views[i];
            _framebuffers[i] = Device.CreateFramebuffer(new VkFramebufferCreateInfo
            {
                RenderPass = RenderPass,
                AttachmentCount = 1,
                Attachments = &view,
                Width = extent.Width,
                Height = extent.Height,
                Layers = 1,
            }, null);

            _renderFinished[i] = Device.CreateSemaphore(new VkSemaphoreCreateInfo(), null);
        }
    }

    private VkRenderPass CreateRenderPass(VkFormat format)
    {
        var color = new VkAttachmentDescription
        {
            Format = format,
            Samples = VkSampleCountFlags._1Bit,
            LoadOp = VkAttachmentLoadOp.Clear,
            StoreOp = VkAttachmentStoreOp.Store,
            StencilLoadOp = VkAttachmentLoadOp.DontCare,
            StencilStoreOp = VkAttachmentStoreOp.DontCare,
            InitialLayout = VkImageLayout.Undefined,
            FinalLayout = VkImageLayout.PresentSrcKHR,
        };
        var colorRef = new VkAttachmentReference { Attachment = 0, Layout = VkImageLayout.ColorAttachmentOptimal };
        var subpass = new VkSubpassDescription
        {
            PipelineBindPoint = VkPipelineBindPoint.Graphics,
            ColorAttachmentCount = 1,
            ColorAttachments = &colorRef,
        };
        var dependency = new VkSubpassDependency
        {
            SrcSubpass = VK.SUBPASS_EXTERNAL,
            DstSubpass = 0,
            SrcStageMask = VkPipelineStageFlags.ColorAttachmentOutputBit,
            DstStageMask = VkPipelineStageFlags.ColorAttachmentOutputBit,
            DstAccessMask = VkAccessFlags.ColorAttachmentWriteBit,
        };
        return Device.CreateRenderPass(new VkRenderPassCreateInfo
        {
            AttachmentCount = 1,
            Attachments = &color,
            SubpassCount = 1,
            Subpasses = &subpass,
            DependencyCount = 1,
            Dependencies = &dependency,
        }, null);
    }

    private void DestroySwapchainResources()
    {
        foreach (var fb in _framebuffers) Device.DestroyFramebuffer(fb, null);
        foreach (var v in _views) Device.DestroyImageView(v, null);
        foreach (var s in _renderFinished) Device.DestroySemaphore(s, null);
        _framebuffers = [];
        _views = [];
        _renderFinished = [];
    }

    private void RecreateSwapchain()
    {
        // Minimised: wait until the window has a non-zero size again.
        var fb = _window.FramebufferSize;
        while ((fb.X == 0 || fb.Y == 0) && !_window.ShouldClose)
        {
            Glfw.WaitEventsTimeout(0.1);
            fb = _window.FramebufferSize;
        }
        Device.WaitIdle();
        DestroySwapchainResources();
        CreateSwapchain();
        _resizeRequested = false;
    }

    public void RequestResize() => _resizeRequested = true;

    /// <summary>
    /// Waits for this frame slot, acquires a swapchain image and begins the render pass.
    /// Returns false when the frame should be skipped (swapchain was recreated).
    /// </summary>
    public bool BeginFrame(Brutal.Numerics.float4 clearColor, out CommandBuffer cmd)
    {
        cmd = _commandBuffers[_frame];
        if (_resizeRequested)
        {
            RecreateSwapchain();
            return false;
        }

        VkFence fence = _inFlight[_frame];
        Device.WaitForFences(new ReadOnlySpan<VkFence>(&fence, 1), new Brutal.Bool32(true), nint.MaxValue);

        var result = Device.AcquireNextImageKHR(_swapchain, nint.MaxValue, _imageAvailable[_frame], default, out _imageIndex);
        if (result == VkResult.ErrorOutOfDateKHR)
        {
            RecreateSwapchain();
            return false;
        }

        Device.ResetFences(new ReadOnlySpan<VkFence>(&fence, 1));
        cmd.Reset(0);
        CommandBufferExtensions.Begin(cmd, VkCommandBufferUsageFlags.OneTimeSubmitBit);

        var clear = new VkClearValue { Color = new VkClearColorValue { Float32 = clearColor } };
        var begin = new VkRenderPassBeginInfo
        {
            RenderPass = RenderPass,
            Framebuffer = _framebuffers[_imageIndex],
            RenderArea = new VkRect2D(Extent),
            ClearValueCount = 1,
            ClearValues = &clear,
        };
        cmd.BeginRenderPass(begin, VkSubpassContents.Inline);

        var viewport = new VkViewport { X = 0, Y = 0, Width = Extent.Width, Height = Extent.Height, MinDepth = 0, MaxDepth = 1 };
        var scissor = new VkRect2D(Extent);
        cmd.SetViewport(0, new ReadOnlySpan<VkViewport>(&viewport, 1));
        cmd.SetScissor(0, new ReadOnlySpan<VkRect2D>(&scissor, 1));
        return true;
    }

    public void EndFrame()
    {
        var cmd = _commandBuffers[_frame];
        cmd.EndRenderPass();
        cmd.End();

        VkSemaphore wait = _imageAvailable[_frame];
        VkSemaphore signal = _renderFinished[_imageIndex];
        VkPipelineStageFlags stage = VkPipelineStageFlags.ColorAttachmentOutputBit;
        QueueExtensions.Submit(Queue,
            new Span<VkSemaphore>(&wait, 1),
            new Span<VkPipelineStageFlags>(&stage, 1),
            new Span<CommandBuffer>(ref cmd),
            new Span<VkSemaphore>(&signal, 1),
            _inFlight[_frame]);

        VkSwapchainKHR swapchain = _swapchain;
        int imageIndex = _imageIndex;
        var result = Queue.PresentKHR(new VkPresentInfoKHR
        {
            WaitSemaphoreCount = 1,
            WaitSemaphores = &signal,
            SwapchainCount = 1,
            Swapchains = &swapchain,
            ImageIndices = &imageIndex,
        });
        if (result is VkResult.ErrorOutOfDateKHR or VkResult.SuboptimalKHR) _resizeRequested = true;

        _frame = (_frame + 1) % FramesInFlight;
    }

    /// <summary>Allocates a host-visible, coherent, persistently mapped buffer.</summary>
    public (VkBuffer Buffer, VkDeviceMemory Memory, nint Mapped) CreateMappedBuffer(long size, VkBufferUsageFlags usage)
    {
        var buffer = Device.CreateBuffer(new VkBufferCreateInfo
        {
            Size = new Brutal.ByteSize64((ulong)size),
            Usage = usage,
            SharingMode = VkSharingMode.Exclusive,
        }, null);
        var memory = DeviceExtensions.AllocateMemory(Device, PhysicalDevice, buffer, VkMemoryPropertyFlags.HostVisibleBit | VkMemoryPropertyFlags.HostCoherentBit, null);
        Device.BindBufferMemory(buffer, memory, default);
        var map = new MapInfo(memory);
        var ptr = DeviceExtensions.MapMemory(Device, in map);
        return (buffer, memory, (nint)ptr.Pointer);
    }

    public void DestroyBuffer(VkBuffer buffer, VkDeviceMemory memory)
    {
        Device.DestroyBuffer(buffer, null);
        Device.FreeMemory(memory, null);
    }

    public void Dispose()
    {
        Device.WaitIdle();
        DestroySwapchainResources();
        Device.DestroySwapchainKHR(_swapchain, null);
        Device.DestroyRenderPass(RenderPass, null);
        for (int i = 0; i < FramesInFlight; i++)
        {
            Device.DestroySemaphore(_imageAvailable[i], null);
            Device.DestroyFence(_inFlight[i], null);
        }
        Device.DestroyCommandPool(_commandPool, null);
        Device.Dispose();
        Instance.DestroySurfaceKHR(Surface, null);
        Instance.Dispose();
    }
}
