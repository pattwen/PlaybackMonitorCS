using System.Runtime.InteropServices;
using System.Text;
namespace Monitor.Engine;

static unsafe class ShaderCompiler
{
    [DllImport("d3dcompiler_47.dll", CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Ansi)]
    static extern int D3DCompile(byte[] data, nuint size, string name, nint defines, nint include, string entry, string target, uint flags, uint effect, out nint code, out nint errors);
    static byte[] Bytes(nint blob)
    {
        var table = *(nint**)blob;
        var pointer = ((delegate* unmanaged[Stdcall]<nint, nint>)table[3])(blob);
        var length = ((delegate* unmanaged[Stdcall]<nint, nuint>)table[4])(blob);
        byte[] bytes = new byte[(int)length];
        Marshal.Copy(pointer, bytes, 0, bytes.Length);
        return bytes;
    }
    public static byte[] Compile(string entry, string target)
    {
        using var stream = typeof(ShaderCompiler).Assembly.GetManifestResourceStream("Monitor.Engine.Shaders.Video.hlsl") ?? throw new FileNotFoundException("Video.hlsl");
        using var reader = new StreamReader(stream);
        byte[] source = Encoding.UTF8.GetBytes((target.StartsWith("cs_", StringComparison.Ordinal) ? "#define MONITOR_COMPUTE 1\n" : "") + reader.ReadToEnd());
        nint code = 0, errors = 0;
        try
        {
            int result = D3DCompile(source, (nuint)source.Length, "Video.hlsl", 0, 0, entry, target, (1 << 15) | (1 << 11), 0, out code, out errors);
            if (result < 0)
            {
                throw new InvalidOperationException($"着色器 {entry}/{target} 编译失败（0x{result:X8}）。", new InvalidOperationException(errors == 0 ? "HLSL编译失败，无详细输出" : Encoding.UTF8.GetString(Bytes(errors))));
            }
            return Bytes(code);
        }
        finally
        {
            if (code != 0)
            {
                Marshal.Release(code);
            }
            if (errors != 0)
            {
                Marshal.Release(errors);
            }
        }
    }
}


public static class ShaderValidation
{
    public static byte[] CompileComputeForValidation() => ShaderCompiler.Compile("CSMain", "cs_5_0");
    public static IReadOnlyDictionary<string, int> CompileAll()
    {
        var results = new Dictionary<string, int>();
        foreach (var (entry, profile) in new[] { ("VSMain", "vs_5_0"), ("PSConvert", "ps_5_0"), ("PSDisplay", "ps_5_0"), ("CSMain", "cs_5_0") })
        {
            results[entry + "/" + profile] = ShaderCompiler.Compile(entry, profile).Length;
        }
        return results;
    }
}
