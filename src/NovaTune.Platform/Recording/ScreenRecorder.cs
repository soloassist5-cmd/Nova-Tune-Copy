using System.Diagnostics;
using System.Runtime.InteropServices;
using NovaTune.Core.Logging;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.MediaFoundation;

namespace NovaTune.Platform.Recording;

public sealed record CaptureMonitor(int AdapterIndex, int OutputIndex, string DeviceName, int Left, int Top, int Width, int Height, bool IsPrimary)
{
    public string Title => $"{DeviceName.Replace(@"\\.\", "")} — {Width}×{Height}" + (IsPrimary ? " (основной)" : "");
}

public sealed record RecordingOptions(CaptureMonitor Monitor, int Fps, double BitsPerPixel, bool CaptureAudio, string OutputPath);

public sealed record RecordingStats(TimeSpan Duration, long Bytes, long Frames, long DroppedFrames, bool HasAudio);

/// <summary>
/// Запись экрана штатными средствами Windows:
/// — изображение: DXGI Desktop Duplication (захватывает и полноэкранные игры, без внедрения в процесс игры);
/// — кодирование: Media Foundation Sink Writer, H.264 + AAC в MP4 (аппаратный кодер видеокарты, если доступен);
/// — звук: WASAPI loopback (системный звук).
/// Постоянная частота кадров: если новый кадр не пришёл, повторяется предыдущий; при отставании кадры пропускаются,
/// чтобы звук и видео не расходились.
/// </summary>
public sealed class ScreenRecorder : IDisposable
{
    // GUID-ы Media Foundation (mfapi.h). Задаются явно, чтобы не зависеть от версии обёртки.
    private static readonly Guid MajorType = new("48eba18e-f8c9-4687-bf11-0a74c9f96a8f");
    private static readonly Guid Subtype = new("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
    private static readonly Guid AvgBitrate = new("20332624-fb0d-4d9e-bd0d-cbf6786c102e");
    private static readonly Guid InterlaceMode = new("e2724bb8-e676-4806-b4b2-a8d6efb44ccd");
    private static readonly Guid FrameSize = new("1652c33d-d6b2-4012-b834-72030849a37d");
    private static readonly Guid FrameRate = new("c459a2e8-3d2c-4e44-b132-fee5156c7bb0");
    private static readonly Guid PixelAspect = new("c6376a1e-8d0a-4027-be45-6d9a0ad39bb6");
    private static readonly Guid DefaultStride = new("644b4e48-1e02-4516-b0eb-c01ca9d49ac6");
    private static readonly Guid AudioChannels = new("37e48bf5-645e-4c5b-89de-ada9e29b696a");
    private static readonly Guid AudioRate = new("5faeeae7-0290-4c31-9e8a-c534f68d9dba");
    private static readonly Guid AudioBits = new("f2deb57f-40fa-4764-aa33-ed4f2d1ff669");
    private static readonly Guid AudioAvgBytes = new("1aab75c8-cfef-451c-ab95-ac034b8e1731");
    private static readonly Guid AudioBlockAlign = new("322de230-9eeb-43bd-ab7a-ff412251541d");
    private static readonly Guid MediaTypeVideo = new("73646976-0000-0010-8000-00AA00389B71");
    private static readonly Guid MediaTypeAudio = new("73647561-0000-0010-8000-00AA00389B71");
    private static readonly Guid VideoRgb32 = new("00000016-0000-0010-8000-00AA00389B71");
    private static readonly Guid VideoH264 = new("34363248-0000-0010-8000-00AA00389B71");
    private static readonly Guid AudioAac = new("00001610-0000-0010-8000-00AA00389B71");
    private static readonly Guid AudioPcm = new("00000001-0000-0010-8000-00AA00389B71");
    private static readonly Guid EnableHardwareTransforms = new("a634a91c-822b-41b9-a494-4de4643612b0");
    private static readonly Guid DisableThrottling = new("08b845d8-2b74-4afe-9d53-be16d2d5ae4f");

    private readonly ILog _log;
    private readonly object _writerLock = new();
    private Thread? _thread;
    private volatile bool _stop;
    private readonly Stopwatch _clock = new();
    private long _bytesHint;

    public ScreenRecorder(ILog log)
    {
        _log = log;
    }

    public bool IsRecording => _thread is { IsAlive: true };

    public string? CurrentFile { get; private set; }

    public TimeSpan Elapsed => _clock.Elapsed;

    public long Frames { get; private set; }

    public long DroppedFrames { get; private set; }

    public bool HasAudio { get; private set; }

    /// <summary>Ошибка записи (сообщение для пользователя); запись при этом останавливается.</summary>
    public event EventHandler<string>? Failed;

    public event EventHandler<RecordingStats>? Finished;

    public long FileBytes
    {
        get
        {
            try
            {
                return CurrentFile is not null && File.Exists(CurrentFile) ? new FileInfo(CurrentFile).Length : _bytesHint;
            }
            catch (IOException)
            {
                return _bytesHint;
            }
        }
    }

    /// <summary>Мониторы, доступные для захвата (выходы DXGI, подключённые к рабочему столу).</summary>
    public static IReadOnlyList<CaptureMonitor> GetMonitors()
    {
        var list = new List<CaptureMonitor>();
        using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
        for (uint a = 0; factory.EnumAdapters1(a, out var adapter).Success; a++)
        {
            using (adapter)
            {
                for (uint o = 0; adapter.EnumOutputs(o, out var output).Success; o++)
                {
                    using (output)
                    {
                        var d = output.Description;
                        if (!d.AttachedToDesktop)
                        {
                            continue;
                        }

                        var r = d.DesktopCoordinates;
                        list.Add(new CaptureMonitor((int)a, (int)o, d.DeviceName, r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top, r.Left == 0 && r.Top == 0));
                    }
                }
            }
        }

        return list;
    }

    public void Start(RecordingOptions options)
    {
        if (IsRecording)
        {
            throw new InvalidOperationException("Запись уже идёт.");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(options.OutputPath)!);
        _stop = false;
        Frames = 0;
        DroppedFrames = 0;
        HasAudio = false;
        CurrentFile = options.OutputPath;
        var ready = new ManualResetEventSlim();
        Exception? startError = null;
        _thread = new Thread(() => Run(options, ready, e => startError = e))
        {
            IsBackground = true,
            Name = "NOVA TUNE recorder",
            Priority = ThreadPriority.AboveNormal,
        };
        _thread.SetApartmentState(ApartmentState.MTA);
        _thread.Start();

        // Ждём, пока запись реально начнётся (или сообщит об ошибке инициализации).
        ready.Wait(TimeSpan.FromSeconds(10));
        if (startError is not null)
        {
            _thread.Join(2000);
            throw new InvalidOperationException(startError.Message, startError);
        }
    }

    public void Stop()
    {
        _stop = true;
        _thread?.Join(TimeSpan.FromSeconds(15));
    }

    private void Run(RecordingOptions o, ManualResetEventSlim ready, Action<Exception> reportStartError)
    {
        IDXGIFactory1? factory = null;
        IDXGIAdapter1? adapter = null;
        IDXGIOutput? output = null;
        IDXGIOutput1? output1 = null;
        ID3D11Device? device = null;
        ID3D11DeviceContext? context = null;
        IDXGIOutputDuplication? duplication = null;
        ID3D11Texture2D? staging = null;
        IMFSinkWriter? writer = null;
        WasapiLoopback? audio = null;
        var started = false;
        var mfStarted = false;
        try
        {
            factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
            factory.EnumAdapters1((uint)o.Monitor.AdapterIndex, out adapter).CheckError();
            adapter.EnumOutputs((uint)o.Monitor.OutputIndex, out output).CheckError();
            output1 = output.QueryInterface<IDXGIOutput1>();

            // Устройство создаётся на той же видеокарте, к которой подключён монитор, — иначе дублирование не работает.
            D3D11.D3D11CreateDevice(adapter, DriverType.Unknown, DeviceCreationFlags.BgraSupport, null!, out device, out context).CheckError();
            duplication = output1.DuplicateOutput(device);

            // H.264 требует чётные размеры.
            var width = o.Monitor.Width & ~1;
            var height = o.Monitor.Height & ~1;
            MediaFactory.MFStartup().CheckError();
            mfStarted = true;
            using (var attributes = MediaFactory.MFCreateAttributes(2))
            {
                attributes.Set(EnableHardwareTransforms, 1u);
                attributes.Set(DisableThrottling, 1u);
                writer = MediaFactory.MFCreateSinkWriterFromURL(o.OutputPath, null!, attributes);
            }

            var bitrate = (uint)Math.Clamp(width * height * o.Fps * o.BitsPerPixel, 2_000_000, 80_000_000);
            int videoStream;
            using (var outType = MediaFactory.MFCreateMediaType())
            {
                outType.Set(MajorType, MediaTypeVideo);
                outType.Set(Subtype, VideoH264);
                outType.Set(AvgBitrate, bitrate);
                outType.Set(InterlaceMode, 2u); // progressive
                outType.Set(FrameSize, Pack((uint)width, (uint)height));
                outType.Set(FrameRate, Pack((uint)o.Fps, 1));
                outType.Set(PixelAspect, Pack(1, 1));
                videoStream = writer.AddStream(outType);
            }

            using (var inType = MediaFactory.MFCreateMediaType())
            {
                inType.Set(MajorType, MediaTypeVideo);
                inType.Set(Subtype, VideoRgb32);
                inType.Set(InterlaceMode, 2u);
                inType.Set(FrameSize, Pack((uint)width, (uint)height));
                inType.Set(FrameRate, Pack((uint)o.Fps, 1));
                inType.Set(PixelAspect, Pack(1, 1));
                inType.Set(DefaultStride, (uint)(width * 4)); // положительный шаг — строки сверху вниз
                writer.SetInputMediaType(videoStream, inType, null!);
            }

            var audioStream = -1;
            if (o.CaptureAudio)
            {
                try
                {
                    audio = new WasapiLoopback();
                    var rate = audio.SampleRate is 44100 or 48000 ? audio.SampleRate : 48000;
                    if (rate != audio.SampleRate)
                    {
                        throw new NotSupportedException($"Частота звука {audio.SampleRate} Гц не поддерживается кодером AAC.");
                    }

                    using var aOut = MediaFactory.MFCreateMediaType();
                    aOut.Set(MajorType, MediaTypeAudio);
                    aOut.Set(Subtype, AudioAac);
                    aOut.Set(AudioChannels, 2u);
                    aOut.Set(AudioRate, (uint)rate);
                    aOut.Set(AudioBits, 16u);
                    aOut.Set(AudioAvgBytes, 24000u); // 192 кбит/с
                    audioStream = writer.AddStream(aOut);

                    using var aIn = MediaFactory.MFCreateMediaType();
                    aIn.Set(MajorType, MediaTypeAudio);
                    aIn.Set(Subtype, AudioPcm);
                    aIn.Set(AudioChannels, 2u);
                    aIn.Set(AudioRate, (uint)rate);
                    aIn.Set(AudioBits, 16u);
                    aIn.Set(AudioBlockAlign, 4u);
                    aIn.Set(AudioAvgBytes, (uint)(rate * 4));
                    writer.SetInputMediaType(audioStream, aIn, null!);
                }
                catch (Exception ex) when (ex is COMException or NotSupportedException or SharpGen.Runtime.SharpGenException or InvalidCastException)
                {
                    // Без звука запись всё равно полезна — продолжаем и сообщаем в журнал.
                    _log.Warn("Запись: системный звук недоступен, запись без звука", ex);
                    audio?.Dispose();
                    audio = null;
                    audioStream = -1;
                }
            }

            writer.BeginWriting();
            audio?.Start();
            HasAudio = audio is not null;
            started = true;
            _clock.Restart();
            ready.Set();
            _log.Info($"Запись начата: {o.OutputPath}, {width}x{height}@{o.Fps}, {bitrate / 1_000_000.0:0.#} Мбит/с, звук: {HasAudio}");

            var frameDuration = 10_000_000L / o.Fps;
            uint stagingW = 0, stagingH = 0;
            var frameBytes = width * height * 4;
            var last = new byte[frameBytes];
            long frameIndex = 0;
            long audioSamplesWritten = 0;

            while (!_stop)
            {
                // ── Видео: берём новый кадр, если он есть; иначе повторяем последний.
                var timeout = (uint)Math.Max(1, 1000 / o.Fps);
                var result = duplication.AcquireNextFrame(timeout, out var info, out var resource);
                if (result.Success)
                {
                    using (resource)
                    using (var texture = resource.QueryInterface<ID3D11Texture2D>())
                    {
                        // Игра в эксклюзивном полноэкранном режиме может сменить разрешение (например, 1280×960 в CS2):
                        // промежуточная текстура пересоздаётся под размер кадра, а кадр масштабируется под размер видео.
                        var desc = texture.Description;
                        if (staging is null || stagingW != desc.Width || stagingH != desc.Height)
                        {
                            staging?.Dispose();
                            staging = device.CreateTexture2D(new Texture2DDescription
                            {
                                Width = desc.Width,
                                Height = desc.Height,
                                MipLevels = 1,
                                ArraySize = 1,
                                Format = desc.Format,
                                SampleDescription = new SampleDescription(1, 0),
                                Usage = ResourceUsage.Staging,
                                BindFlags = BindFlags.None,
                                CPUAccessFlags = CpuAccessFlags.Read,
                                MiscFlags = ResourceOptionFlags.None,
                            });
                            stagingW = desc.Width;
                            stagingH = desc.Height;
                        }

                        context.CopyResource(staging, texture);
                    }

                    duplication.ReleaseFrame();
                    context.Map(staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None, out var mapped).CheckError();
                    try
                    {
                        CopyFrame(mapped.DataPointer, (int)mapped.RowPitch, (int)stagingW, (int)stagingH, last, width, height);
                    }
                    finally
                    {
                        context.Unmap(staging, 0);
                    }
                }
                else if (result == Vortice.DXGI.ResultCode.AccessLost)
                {
                    // Смена режима экрана (например, игра перешла в полноэкранный режим) — пересоздаём дублирование.
                    duplication.Dispose();
                    Thread.Sleep(50);
                    duplication = output1.DuplicateOutput(device);
                    continue;
                }
                else if (result != Vortice.DXGI.ResultCode.WaitTimeout)
                {
                    result.CheckError();
                }

                // Сколько кадров должно быть записано к этому моменту — при отставании лишние пропускаем.
                var due = _clock.Elapsed.Ticks / frameDuration;
                if (due > frameIndex + 2)
                {
                    DroppedFrames += due - frameIndex - 1;
                    frameIndex = due - 1;
                }

                while (frameIndex <= due && !_stop)
                {
                    WriteVideo(writer, videoStream, last, frameIndex * frameDuration, frameDuration);
                    frameIndex++;
                    Frames++;
                }

                // ── Звук: всё, что накопилось; при тишине (WASAPI не присылает данных) — дописываем нули.
                if (audio is not null)
                {
                    var pcm = audio.Read();
                    if (pcm.Length > 0)
                    {
                        WriteAudio(writer, audioStream, pcm, audioSamplesWritten, audio.SampleRate);
                        audioSamplesWritten += pcm.Length / 4;
                    }

                    var expected = (long)(_clock.Elapsed.TotalSeconds * audio.SampleRate);
                    var gap = expected - audioSamplesWritten - (audio.SampleRate / 10);
                    if (gap > 0)
                    {
                        WriteAudio(writer, audioStream, new byte[gap * 4], audioSamplesWritten, audio.SampleRate);
                        audioSamplesWritten += gap;
                    }
                }

                _bytesHint = Frames * frameBytes / 200;
            }

            _clock.Stop();
            lock (_writerLock)
            {
                writer.Finalize();
            }

            _log.Info($"Запись завершена: {o.OutputPath}, кадров {Frames}, пропущено {DroppedFrames}");
            Finished?.Invoke(this, new RecordingStats(_clock.Elapsed, FileBytes, Frames, DroppedFrames, HasAudio));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _log.Error("Ошибка записи экрана", ex);
            var message = Friendly(ex);
            if (!started)
            {
                reportStartError(new InvalidOperationException(message, ex));
            }
            else
            {
                Failed?.Invoke(this, message);
            }
        }
        finally
        {
            ready.Set();
            _clock.Stop();
            audio?.Dispose();
            writer?.Dispose();
            staging?.Dispose();
            duplication?.Dispose();
            context?.Dispose();
            device?.Dispose();
            output1?.Dispose();
            output?.Dispose();
            adapter?.Dispose();
            factory?.Dispose();
            if (mfStarted)
            {
                MediaFactory.MFShutdown();
            }
        }
    }

    private void WriteVideo(IMFSinkWriter writer, int stream, byte[] frame, long time, long duration)
    {
        using var buffer = MediaFactory.MFCreateMemoryBuffer(frame.Length);
        buffer.Lock(out var ptr, out _, out _);
        Marshal.Copy(frame, 0, ptr, frame.Length);
        buffer.Unlock();
        buffer.CurrentLength = frame.Length;
        using var sample = MediaFactory.MFCreateSample();
        sample.AddBuffer(buffer);
        sample.SampleTime = time;
        sample.SampleDuration = duration;
        lock (_writerLock)
        {
            writer.WriteSample(stream, sample);
        }
    }

    private void WriteAudio(IMFSinkWriter writer, int stream, byte[] pcm, long samplesBefore, int rate)
    {
        using var buffer = MediaFactory.MFCreateMemoryBuffer(pcm.Length);
        buffer.Lock(out var ptr, out _, out _);
        Marshal.Copy(pcm, 0, ptr, pcm.Length);
        buffer.Unlock();
        buffer.CurrentLength = pcm.Length;
        using var sample = MediaFactory.MFCreateSample();
        sample.AddBuffer(buffer);
        sample.SampleTime = samplesBefore * 10_000_000L / rate;
        sample.SampleDuration = (pcm.Length / 4) * 10_000_000L / rate;
        lock (_writerLock)
        {
            writer.WriteSample(stream, sample);
        }
    }

    /// <summary>
    /// Копирует кадр в буфер видео. Совпадает размер — построчно; не совпадает — масштабирование «ближайший сосед»
    /// с сохранением пропорций (поля заполняются чёрным).
    /// </summary>
    private static unsafe void CopyFrame(nint src, int pitch, int srcW, int srcH, byte[] dst, int dstW, int dstH)
    {
        fixed (byte* d = dst)
        {
            if (srcW >= dstW && srcH >= dstH && srcW - dstW <= 1 && srcH - dstH <= 1)
            {
                for (var y = 0; y < dstH; y++)
                {
                    Buffer.MemoryCopy((byte*)src + ((long)y * pitch), d + ((long)y * dstW * 4), (long)dstW * 4, (long)dstW * 4);
                }

                return;
            }

            var scale = Math.Min((double)dstW / srcW, (double)dstH / srcH);
            var outW = Math.Max(1, (int)(srcW * scale));
            var outH = Math.Max(1, (int)(srcH * scale));
            var offX = (dstW - outW) / 2;
            var offY = (dstH - outH) / 2;
            new Span<byte>(d, dst.Length).Clear();
            var xMap = new int[outW];
            for (var x = 0; x < outW; x++)
            {
                xMap[x] = Math.Min(srcW - 1, (int)(x / scale));
            }

            for (var y = 0; y < outH; y++)
            {
                var sy = Math.Min(srcH - 1, (int)(y / scale));
                var srcRow = (uint*)((byte*)src + ((long)sy * pitch));
                var dstRow = (uint*)d + ((long)(y + offY) * dstW) + offX;
                for (var x = 0; x < outW; x++)
                {
                    dstRow[x] = srcRow[xMap[x]];
                }
            }
        }
    }

    private static ulong Pack(uint high, uint low) => ((ulong)high << 32) | low;

    private static string Friendly(Exception ex)
    {
        var hr = ex is SharpGen.Runtime.SharpGenException sg ? sg.HResult : ex.HResult;
        return hr switch
        {
            unchecked((int)0x887A0022) => "Захват экрана сейчас недоступен (например, открыт защищённый экран или UAC). Попробуйте ещё раз.",
            unchecked((int)0x887A0004) => "Видеодрайвер не поддерживает дублирование экрана на этом мониторе.",
            unchecked((int)0xC00D36D5) or unchecked((int)0xC00D5212) => "В системе нет кодека H.264 Media Foundation (например, в редакциях N без Media Feature Pack).",
            _ => ex.Message,
        };
    }

    public void Dispose() => Stop();
}
