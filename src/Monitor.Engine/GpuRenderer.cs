using System.Runtime.InteropServices;
using System.Text.Json;
using FFmpeg.AutoGen;
using Monitor.Core;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;
using Buffer = Vortice.Direct3D11.ID3D11Buffer;
using ID3D11Device = Vortice.Direct3D11.ID3D11Device;
using ID3D11DeviceContext = Vortice.Direct3D11.ID3D11DeviceContext;
using ID3D11Texture2D = Vortice.Direct3D11.ID3D11Texture2D;

namespace Monitor.Engine;
public sealed unsafe class GpuRenderer : IDisposable
{
    const int TileFloats = VisualAnalyzer.TileFloats;
    const int TileBytes = TileFloats * sizeof(float);
    readonly VisualAnalyzer visualAnalyzer = new();
    readonly GrayScreenAnalyzer grayAnalyzer = new();
    readonly AnalysisSchedule analysisSchedule = new();
    readonly object gate = new();
    readonly nint window;
    ID3D11Device device = null!;
    ID3D11DeviceContext context = null!;
    IDXGISwapChain4 swap = null!;
    ID3D11VertexShader vs = null!;
    ID3D11PixelShader convert = null!;
    ID3D11PixelShader display = null!;
    ID3D11ComputeShader compute = null!;
    ID3D11SamplerState sampler = null!;
    Buffer constants = null!;
    ID3D11Texture2D? input;
    ID3D11Texture2D? converted;
    ID3D11ShaderResourceView? yView;
    ID3D11ShaderResourceView? uvView;
    ID3D11ShaderResourceView? rgbView;
    ID3D11RenderTargetView? convertedTarget;
    readonly Queue<Readback> pending = [];
    ID3D11Texture2D? previousAnalysisImage;
    ID3D11Texture2D? referenceAnalysisImage;
    ID3D11ShaderResourceView? previousAnalysisView;
    ID3D11ShaderResourceView? referenceAnalysisView;
    bool previousAnalysisValid;
    bool referenceAnalysisValid;
    double referenceAnalysisPts = double.NegativeInfinity;
    int width;
    int height;
    int analysisGeneration = -1;
    Format inputFormat;
    double lastAnalysis = -100;
    readonly SoftwareConverter software = new();
    DetectionSettings? analysisSettings;
    public DisplayMode DisplayMode { get;
        set;
    }
    public DeinterlaceMode Deinterlace { get;
        set;
    }
    public string Status { get;
        private set;
    } = "";
    public string Adapter { get;
    } = "";

    bool disposed;
    public bool HdrOutput { get;
        private set;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    struct Hdr10
    {
        public ushort Rx;
        public ushort Ry;
        public ushort Gx;
        public ushort Gy;
        public ushort Bx;
        public ushort By;
        public ushort Wx;
        public ushort Wy;
        public uint MaxMastering;
        public uint MinMastering;
        public ushort MaxCll;
        public ushort MaxFall;
    }

    Hdr10 hdr;
    bool hasHdr;
    public event Action<VideoMetrics, FrameLease, Guid, DetectionSettings>? Analyzed;
    sealed record Readback(Buffer Buffer, ID3D11Query Query, FrameLease Frame, Guid Id, int Width, int Height, double Pixels, DetectionSettings Settings, bool HasPrevious, bool HasReference);
    [StructLayout(LayoutKind.Sequential)]
    struct Rect
    {
        public int L;
        public int T;
        public int R;
        public int B;
    }

    [DllImport("user32")]
    static extern bool GetClientRect(nint hwnd, out Rect rect);
    public GpuRenderer(nint hwnd, nint decodeDevice)
    {
        window = hwnd;
        string stage = "着色器编译";
        try
        {
            byte[] vertexCode = ShaderCompiler.Compile("VSMain", "vs_5_0");
            byte[] convertCode = ShaderCompiler.Compile("PSConvert", "ps_5_0");
            byte[] displayCode = ShaderCompiler.Compile("PSDisplay", "ps_5_0");
            byte[] computeCode = ShaderCompiler.Compile("CSMain", "cs_5_0");
            stage = "D3D11设备初始化";
            if (decodeDevice != 0)
            {
                Marshal.AddRef(decodeDevice);
                device = new(decodeDevice);
                context = device.ImmediateContext;
            }
            else
            {
                D3D11.D3D11CreateDevice(null, DriverType.Hardware, DeviceCreationFlags.BgraSupport, [FeatureLevel.Level_11_1, FeatureLevel.Level_11_0], out device, out context).CheckError();
            }

            using (var mt = context.QueryInterfaceOrNull<ID3D11Multithread>())
            {
                mt?.SetMultithreadProtected(true);
            }

            stage = "D3D11着色器与缓冲区创建";
            vs = device.CreateVertexShader(vertexCode);
            convert = device.CreatePixelShader(convertCode);
            display = device.CreatePixelShader(displayCode);
            compute = device.CreateComputeShader(computeCode);
            constants = device.CreateBuffer(80, BindFlags.ConstantBuffer);
            sampler = device.CreateSamplerState(SamplerDescription.LinearClamp);
            using var dxgi = device.QueryInterface<IDXGIDevice>();
            using var adapter = dxgi.GetAdapter();
            Adapter = adapter.Description.Description;
            using var factory = adapter.GetParent<IDXGIFactory2>();
            stage = "DXGI交换链创建";
            var description = new SwapChainDescription1
            {
                Width = 1,
                Height = 1,
                Format = Format.R10G10B10A2_UNorm,
                BufferCount = 2,
                BufferUsage = Usage.RenderTargetOutput,
                SampleDescription = new(1, 0),
                SwapEffect = SwapEffect.FlipDiscard,
                Scaling = Scaling.Stretch,
                AlphaMode = AlphaMode.Ignore
            };
            using var chain = factory.CreateSwapChainForHwnd(device, hwnd, description);
            swap = chain.QueryInterface<IDXGISwapChain4>();
            factory.MakeWindowAssociation(hwnd, WindowAssociationFlags.IgnoreAltEnter);
        }
        catch (Exception ex)
        {
            try
            {
                Dispose();
            }
            catch (Exception ex2)
            {
                throw new Exception($"释放资源失败：{ex.Message}");
            }

            throw new InvalidOperationException($"渲染初始化失败 [{stage}]，HRESULT 0x{ex.HResult:X8}：{ex.Message}", ex);
        }
    }

    void Resources(int w, int h, Format format)
    {
        if (width == w && height == h && inputFormat == format)
        {
            return;
        }

        DisposePictures();
        width = w;
        height = h;
        inputFormat = format;
        input = device.CreateTexture2D(new Texture2DDescription { Width = (uint)w, Height = (uint)h, MipLevels = 1, ArraySize = 1, Format = format, SampleDescription = new(1, 0), BindFlags = BindFlags.ShaderResource });
        var yFormat = format == Format.NV12 ? Format.R8_UNorm : format == Format.P010 ? Format.R16_UNorm : Format.R16G16B16A16_UNorm;
        yView = device.CreateShaderResourceView(input, new ShaderResourceViewDescription { Format = yFormat, ViewDimension = ShaderResourceViewDimension.Texture2D, Texture2D = new() { MipLevels = 1 } });
        if (format is Format.NV12 or Format.P010)
        {
            uvView = device.CreateShaderResourceView(input, new ShaderResourceViewDescription { Format = format == Format.NV12 ? Format.R8G8_UNorm : Format.R16G16_UNorm, ViewDimension = ShaderResourceViewDimension.Texture2D, Texture2D = new() { MipLevels = 1 } });
        }

        converted = device.CreateTexture2D(new Texture2DDescription { Width = (uint)w, Height = (uint)h, MipLevels = 1, ArraySize = 1, Format = Format.R10G10B10A2_UNorm, SampleDescription = new(1, 0), BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource });
        convertedTarget = device.CreateRenderTargetView(converted);
        rgbView = device.CreateShaderResourceView(converted);
    }

    float[] Parameters(VideoInfo info, DetectionSettings settings) => [(float)width, (float)height, inputFormat == Format.NV12 ? 1 : inputFormat == Format.P010 ? 2 : 0, info.Matrix.Contains("2020") ? 1 : info.Matrix.Contains("240") ? 3 : info.Matrix.Contains("FCC") ? 4 : info.Matrix.Contains("470") || info.Matrix.Contains("170") ? 2 : info.Matrix.Contains("709") ? 0 : info.Height <= 576 ? 2 : 0, info.Range.Contains("JPEG") ? 1 : 0, info.BitDepth, DisplayMode == DisplayMode.SdrPreview ? 1 : 0, info.IsPq ? 1 : info.IsHlg ? 2 : 0, (float)(width * settings.RoiX / 100), (float)(height * settings.RoiY / 100), (float)(width * settings.RoiWidth / 100), (float)(height * settings.RoiHeight / 100), (float)settings.HdrBlackNits, (float)(settings.BlackLevel / 255), 0, (info.Interlaced ? 1 : 0) + (info.Primaries.Contains("2020") ? 2 : 0) + info.ChromaLocation * 4, (float)(settings.FreezePixelDifference / 255), 0, 0, 0];
    void SetParameters(float[] values)
    {
        fixed (float* p = values)
        {
            context.UpdateSubresource(constants, 0, null, (nint)p, 0, 0);
        }
    }

    public void Process(FrameLease lease, VideoInfo info, DetectionSettings settings, bool present)
    {
        lock (gate)
        {
            if (analysisGeneration != lease.Generation || !ReferenceEquals(analysisSettings, settings))
            {
                foreach (var old in pending)
                {
                    old.Frame.Dispose();
                    old.Query.Dispose();
                    old.Buffer.Dispose();
                }

                pending.Clear();
                lastAnalysis = -100;
                visualAnalyzer.Reset();
                grayAnalyzer.Reset();
                analysisSchedule.Reset();
                previousAnalysisValid = referenceAnalysisValid = false;
                referenceAnalysisPts = double.NegativeInfinity;
                analysisGeneration = lease.Generation;
                analysisSettings = settings;
            }

            ReadCompleted();
            AVFrame* frame = lease.Frame;
            ReadHdr(frame, info);
            if ((AVPixelFormat)frame->format == AVPixelFormat.AV_PIX_FMT_D3D11)
            {
                var ptr = (nint)frame->data[0];
                Marshal.AddRef(ptr);
                using var texture = new ID3D11Texture2D(ptr);
                Format oneFormat = texture.Description.Format;
                if (oneFormat is not (Format.NV12 or Format.P010))
                {
                    throw new NotSupportedException($"硬解纹理{oneFormat}不支持直接显示，请关闭该输入硬解使用软件兼容路径。");
                }

                Resources(frame->width, frame->height, oneFormat);
                context.CopySubresourceRegion(input!, 0, 0, 0, 0, texture, (uint)(nuint)frame->data[1], new Box(0, 0, 0, width, height, 1));
            }
            else
            {
                Resources(frame->width, frame->height, Format.R16G16B16A16_UNorm);
                byte[] bytes = software.Convert(frame, info, true);
                fixed (byte* p = bytes)
                {
                    context.UpdateSubresource(input!, 0, null, (nint)p, (uint)(width * 8), 0);
                }
            }

            var parameters = Parameters(info, settings);
            parameters[18] = settings.VisualLocalEnabled || settings.GrayEnabled ? 1 : 0;
            SetParameters(parameters);
            context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
            context.VSSetShader(vs);
            context.PSSetShader(convert);
            context.PSSetConstantBuffer(0, constants);
            context.PSSetSampler(0, sampler);
            context.PSSetShaderResource(0, yView!);
            context.PSSetShaderResource(1, uvView!);
            context.OMSetRenderTargets(convertedTarget!);
            context.RSSetViewport(new Viewport(0, 0, width, height));
            context.Draw(3, 0);
            context.PSSetShaderResource(0, null!);
            context.PSSetShaderResource(1, null!);
            context.OMSetRenderTargets((ID3D11RenderTargetView)null!);
            if (Analyzed != null && pending.Count < 3 && analysisSchedule.Due(lease.Pts, settings.AnalysisFps))
            {
                bool motionEnabled = settings.VisualLocalEnabled || settings.GrayEnabled || settings.FreezeEnabled && settings.FreezeLocalMotionProtection;
                if (lease.Pts - lastAnalysis > Math.Max(1, 3 / settings.AnalysisFps))
                {
                    previousAnalysisValid = referenceAnalysisValid = false;
                }

                if (motionEnabled)
                {
                    EnsureMotionPictures();
                }
                else
                {
                    DisposeMotionPictures();
                }

                bool hasPrevious = motionEnabled && previousAnalysisValid;
                bool hasReference = motionEnabled && referenceAnalysisValid;
                parameters[17] = (hasPrevious ? 1 : 0) | (hasReference ? 2 : 0);
                SetParameters(parameters);
                lastAnalysis = lease.Pts;
                uint count = (uint)(((width + 15) / 16) * ((height + 15) / 16));
                using var output = device.CreateBuffer(count * TileBytes, BindFlags.UnorderedAccess, ResourceUsage.Default, CpuAccessFlags.None, ResourceOptionFlags.BufferStructured, 4);
                using var uav = device.CreateUnorderedAccessView(output, new UnorderedAccessViewDescription { Format = Format.Unknown, ViewDimension = UnorderedAccessViewDimension.Buffer, Buffer = new() { NumElements = count * TileFloats } });
                context.CSSetShader(compute);
                context.CSSetConstantBuffer(0, constants);
                context.CSSetShaderResource(0, rgbView);
                context.CSSetShaderResource(2, previousAnalysisView);
                context.CSSetShaderResource(3, referenceAnalysisView);
                context.CSSetUnorderedAccessView(0, uav);
                context.Dispatch((uint)((width + 15) / 16), (uint)((height + 15) / 16), 1);
                context.CSSetShaderResource(0, null);
                context.CSSetShaderResource(2, null);
                context.CSSetShaderResource(3, null);
                context.CSSetUnorderedAccessView(0, null);
                var staging = device.CreateBuffer(count * TileBytes, BindFlags.None, ResourceUsage.Staging, CpuAccessFlags.Read);
                context.CopyResource(staging, output);
                if (motionEnabled)
                {
                    context.CopyResource(previousAnalysisImage!, converted!);
                    previousAnalysisValid = true;
                    if (!referenceAnalysisValid || lease.Pts - referenceAnalysisPts >= settings.FreezeReferenceSeconds)
                    {
                        context.CopyResource(referenceAnalysisImage!, converted!);
                        referenceAnalysisValid = true;
                        referenceAnalysisPts = lease.Pts;
                    }
                }

                var query = device.CreateQuery(new QueryDescription(QueryType.Event));
                context.End(query);
                pending.Enqueue(new(staging, query, new FrameLease(lease.Frame, lease.Pts, lease.Duration, lease.Generation, lease.Info ?? info), Guid.NewGuid(), width, height, width * height * settings.RoiWidth * settings.RoiHeight / 10000, settings, hasPrevious, hasReference));
                context.Flush();
            }

            if (present)
            {
                Present(info, parameters, 0);
            }

            ReadCompleted();
        }
    }

    public void PresentField(VideoInfo info, DetectionSettings settings, int field)
    {
        lock (gate)
        {
            if (converted != null)
            {
                Present(info, Parameters(info, settings), field);
            }
        }
    }

    void Present(VideoInfo info, float[] parameters, int field)
    {
        GetClientRect(window, out var rect);
        uint w = (uint)Math.Max(1, rect.R - rect.L);
        uint h = (uint)Math.Max(1, rect.B - rect.T);
        var desc = swap.Description1;
        if (desc.Width != w || desc.Height != h)
        {
            context.OMSetRenderTargets((ID3D11RenderTargetView)null!);
            swap.ResizeBuffers(2, w, h, Format.R10G10B10A2_UNorm, SwapChainFlags.None).CheckError();
        }

        bool available = false;
        string monitor = "未知显示器";
        try
        {
            using var output = swap.GetContainingOutput();
            using var output6 = output.QueryInterfaceOrNull<IDXGIOutput6>();
            if (output6 != null)
            {
                var d = output6.Description1;
                available = (int)d.ColorSpace == 12 && d.BitsPerColor >= 10;
                monitor = $"{d.DeviceName} {d.BitsPerColor}bit";
            }
        }
        catch
        {
        }

        HdrOutput = info.IsPq && DisplayMode == DisplayMode.Native && available;
        bool blocked = info.Hdr && DisplayMode == DisplayMode.Native && (!available || !info.IsPq);
        Status = blocked ? "HDR原生显示不可用：需开启HDR的兼容显示器；HLG需选择兼容预览" : HdrOutput ? $"{monitor} · HDR10原生输出" : info.Hdr ? "SDR兼容预览（色调映射）" : $"{monitor} · SDR";
        swap.SetColorSpace1((ColorSpaceType)(HdrOutput ? 12 : 0));
        if (HdrOutput && hasHdr)
        {
            var data = hdr;
            swap.SetHDRMetaData(HdrMetadataType.Hdr10, (uint)sizeof(Hdr10), (nint)(&data));
        }
        else
        {
            swap.SetHDRMetaData(HdrMetadataType.None, 0, 0);
        }

        using var back = swap.GetBuffer<ID3D11Texture2D>(0);
        using var target = device.CreateRenderTargetView(back);
        context.ClearRenderTargetView(target, new Color4(0, 0, 0, 1));
        if (!blocked)
        {
            float aspectWidth = width * (float)info.SampleAspectRatio;
            float scale = Math.Min(w / aspectWidth, h / (float)height);
            float vw = aspectWidth * scale;
            float vh = height * scale;
            parameters[2] = 0;
            parameters[14] = field;
            SetParameters(parameters);
            context.OMSetRenderTargets(target);
            context.RSSetViewport(new Viewport((w - vw) / 2, (h - vh) / 2, vw, vh));
            context.PSSetShader(display);
            context.PSSetShaderResource(0, rgbView!);
            context.Draw(3, 0);
            context.PSSetShaderResource(0, null!);
        }

        context.OMSetRenderTargets((ID3D11RenderTargetView)null!);
        swap.Present(1, PresentFlags.None).CheckError();
    }

    void ReadHdr(AVFrame* frame, VideoInfo info)
    {
        var mastering = ffmpeg.av_frame_get_side_data(frame, AVFrameSideDataType.AV_FRAME_DATA_MASTERING_DISPLAY_METADATA);
        if (mastering != null)
        {
            var m = (AVMasteringDisplayMetadata*)mastering->data;
            static ushort Chroma(AVRational x) => (ushort)Math.Clamp(Math.Round(ffmpeg.av_q2d(x) * 50000), 0, 65535);
            if (m->has_primaries != 0)
            {
                hdr.Rx = Chroma(m->display_primaries[0][0]);
                hdr.Ry = Chroma(m->display_primaries[0][1]);
                hdr.Gx = Chroma(m->display_primaries[1][0]);
                hdr.Gy = Chroma(m->display_primaries[1][1]);
                hdr.Bx = Chroma(m->display_primaries[2][0]);
                hdr.By = Chroma(m->display_primaries[2][1]);
                hdr.Wx = Chroma(m->white_point[0]);
                hdr.Wy = Chroma(m->white_point[1]);
            }

            if (m->has_luminance != 0)
            {
                hdr.MaxMastering = (uint)Math.Clamp(ffmpeg.av_q2d(m->max_luminance), 0, uint.MaxValue);
                hdr.MinMastering = (uint)Math.Clamp(ffmpeg.av_q2d(m->min_luminance) * 10000, 0, uint.MaxValue);
            }

            hasHdr = true;
        }

        var light = ffmpeg.av_frame_get_side_data(frame, AVFrameSideDataType.AV_FRAME_DATA_CONTENT_LIGHT_LEVEL);
        if (light != null)
        {
            var m = (AVContentLightMetadata*)light->data;
            hdr.MaxCll = (ushort)Math.Min(m->MaxCLL, 65535u);
            hdr.MaxFall = (ushort)Math.Min(m->MaxFALL, 65535u);
        }

        if (mastering == null && !string.IsNullOrEmpty(info.HdrMetadataJson))
        {
            using var json = JsonDocument.Parse(info.HdrMetadataJson);
            var root = json.RootElement;
            if (root.TryGetProperty("Mastering", out var m) && m.ValueKind == JsonValueKind.Object)
            {
                static ushort C(JsonElement value) => (ushort)Math.Clamp(Math.Round(value.GetDouble() * 50000), 0, 65535);
                var p = m.GetProperty("Primaries");
                var white = m.GetProperty("WhitePoint");
                hdr.Rx = C(p[0][0]);
                hdr.Ry = C(p[0][1]);
                hdr.Gx = C(p[1][0]);
                hdr.Gy = C(p[1][1]);
                hdr.Bx = C(p[2][0]);
                hdr.By = C(p[2][1]);
                hdr.Wx = C(white[0]);
                hdr.Wy = C(white[1]);
                hdr.MaxMastering = (uint)Math.Clamp(m.GetProperty("MaxNits").GetDouble(), 0, uint.MaxValue);
                hdr.MinMastering = (uint)Math.Clamp(m.GetProperty("MinNits").GetDouble() * 10000, 0, uint.MaxValue);
                hasHdr = true;
            }

            if (root.TryGetProperty("ContentLight", out var l) && l.ValueKind == JsonValueKind.Object)
            {
                hdr.MaxCll = (ushort)Math.Min(l.GetProperty("MaxCLL").GetUInt32(), 65535u);
                hdr.MaxFall = (ushort)Math.Min(l.GetProperty("MaxFALL").GetUInt32(), 65535u);
            }
        }
    }

    void ReadCompleted()
    {
        while (pending.TryPeek(out var work))
        {
            int complete = 0;
            var ready = context.GetData(work.Query, (nint)(&complete), 4, AsyncGetDataFlags.DoNotFlush);
            if (ready.Failure || complete == 0)
            {
                return;
            }

            pending.Dequeue();
            try
            {
                var map = context.Map(work.Buffer, MapMode.Read);
                int count = ((work.Width + 15) / 16) * ((work.Height + 15) / 16);
                var thumb = new List<double>();
                var motion = new FreezeMotionAccumulator(work.Settings.FreezeBlockChangedPercent);
                double black = 0;
                double pixels = 0;
                double noise = 0;
                var px = new double[16];
                var py = new double[16];
                var cx = new double[16];
                var cy = new double[16];
                VisualFrameMetrics? visual = null;
                VisualEvidence? gray = null;
                try
                {
                    float* data = (float*)map.DataPointer;
                    if (work.Settings.VisualLocalEnabled)
                    {
                        visual = visualAnalyzer.Analyze(new ReadOnlySpan<float>(data, count * TileFloats), work.Width, work.Height, work.Frame.Pts, work.Settings, work.Frame.Info?.Interlaced ?? false);
                    }

                    if (work.Settings.GrayEnabled)
                    {
                        gray = grayAnalyzer.Analyze(new ReadOnlySpan<float>(data, count * TileFloats), work.Width, work.Height, work.Frame.Pts, work.Settings);
                    }

                    for (int i = 0;
                        i < count;
                        i++)
                    {
                        float* t = data + i * TileFloats;
                        motion.AddTile(t[2], t[68], t[69], t[70], t[71]);
                        if (t[2] > 0)
                        {
                            thumb.Add(t[0] / t[2]);
                        }

                        black += t[1];
                        pixels += t[2];
                        noise += t[3];
                        for (int k = 0;
                            k < 16;
                            k++)
                        {
                            px[k] += t[4 + k];
                            py[k] += t[20 + k];
                            cx[k] += t[36 + k];
                            cy[k] += t[52 + k];
                        }
                    }
                }
                finally
                {
                    context.Unmap(work.Buffer, 0);
                }

                double score = 0;
                foreach (var(phase, coverage)in new[]
                {
                    (px, cx),
                    (py, cy)
                }

                )
                {
                    double mean = phase.Sum() / 16;
                    int dominant = Array.IndexOf(phase, phase.Max());
                    double boundaries = Math.Max(1, pixels / 16);
                    if (mean > 0 && coverage[dominant] / boundaries >= .35 && phase[dominant] / boundaries * 255 > 12)
                    {
                        score = Math.Max(score, phase.Max() / mean);
                    }
                }

                Analyzed?.Invoke(new(pixels > 0 ? black / pixels * 100 : 0, 0, score, pixels > 0 ? noise / pixels * 127.5 : 0, thumb.ToArray()) { Visual = visual, Gray = gray, LocalMotion = work.Settings.FreezeEnabled && work.Settings.FreezeLocalMotionProtection ? motion.Finish(work.HasPrevious, work.HasReference) : null }, work.Frame, work.Id, work.Settings);
            }
            finally
            {
                work.Frame.Dispose();
                work.Query.Dispose();
                work.Buffer.Dispose();
            }
        }
    }

    public void DrainAnalysis()
    {
        lock (gate)
        {
            context.Flush();
            var deadline = DateTime.UtcNow.AddMilliseconds(500);
            while (pending.Count > 0 && DateTime.UtcNow < deadline)
            {
                ReadCompleted();
                if (pending.Count > 0)
                {
                    Thread.Sleep(1);
                }
            }
        }
    }

    void EnsureMotionPictures()
    {
        if (previousAnalysisImage != null)
        {
            return;
        }

        var description = new Texture2DDescription
        {
            Width = (uint)width,
            Height = (uint)height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.R10G10B10A2_UNorm,
            SampleDescription = new(1, 0),
            BindFlags = BindFlags.ShaderResource
        };
        previousAnalysisImage = device.CreateTexture2D(description);
        referenceAnalysisImage = device.CreateTexture2D(description);
        previousAnalysisView = device.CreateShaderResourceView(previousAnalysisImage);
        referenceAnalysisView = device.CreateShaderResourceView(referenceAnalysisImage);
        previousAnalysisValid = referenceAnalysisValid = false;
    }

    void DisposeMotionPictures()
    {
        context?.CSSetShaderResource(2, null);
        context?.CSSetShaderResource(3, null);
        previousAnalysisView?.Dispose();
        referenceAnalysisView?.Dispose();
        previousAnalysisImage?.Dispose();
        referenceAnalysisImage?.Dispose();
        previousAnalysisView = referenceAnalysisView = null;
        previousAnalysisImage = referenceAnalysisImage = null;
        previousAnalysisValid = referenceAnalysisValid = false;
        referenceAnalysisPts = double.NegativeInfinity;
    }

    void DisposePictures()
    {
        DisposeMotionPictures();
        context?.PSSetShaderResource(0, null!);
        context?.PSSetShaderResource(1, null!);
        yView?.Dispose();
        uvView?.Dispose();
        rgbView?.Dispose();
        convertedTarget?.Dispose();
        input?.Dispose();
        converted?.Dispose();
        yView = uvView = rgbView = null;
        convertedTarget = null;
        input = converted = null;
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            context?.ClearState();
            context?.Flush();
            foreach (var p in pending)
            {
                p.Frame.Dispose();
                p.Query.Dispose();
                p.Buffer.Dispose();
            }

            pending.Clear();
            DisposePictures();
            software.Dispose();
            constants?.Dispose();
            sampler?.Dispose();
            vs?.Dispose();
            convert?.Dispose();
            display?.Dispose();
            compute?.Dispose();
            swap?.Dispose();
            context?.Flush();
            context?.Dispose();
            device?.Dispose();
        }
    }
}
