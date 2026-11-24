using System.Reflection;
using Brutal.ShaderCApi;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Abstractions;

namespace Crossfire.Render;

/// <summary>Compiles embedded GLSL shaders to SPIR-V at startup with shaderc.</summary>
public static unsafe class ShaderCompiler
{
    public static VkShaderModule Load(Device device, string resourceName, ShaderKind kind)
    {
        string source = ReadResource(resourceName);
        var compiler = ShaderC.CreateCompiler();
        var options = ShaderC.CreateCompilerOptions();
        try
        {
            options.SetOptimizationLevel(OptimizationLevel.Performance);
            var result = compiler.Compile(source, kind, resourceName, "main", options);
            try
            {
                if (result.GetCompilationStatus() != CompilationStatus.Success)
                    throw new Exception($"Shader '{resourceName}' failed to compile:\n{result.GetErrorMessage()}");
                var spirv = new ReadOnlySpan<byte>(result.GetBytes(), (int)result.GetLength().Value);
                return ShaderModuleExtensions.FromSpirv(device, spirv, null);
            }
            finally
            {
                result.Dispose();
            }
        }
        finally
        {
            options.Dispose();
            compiler.Dispose();
        }
    }

    private static string ReadResource(string name)
    {
        var asm = Assembly.GetExecutingAssembly();
        using var stream = asm.GetManifestResourceStream(name)
            ?? throw new FileNotFoundException($"Embedded shader '{name}' not found.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
