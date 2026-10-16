using System.Runtime.InteropServices;

namespace NovaTune.Platform.Recording;

/// <summary>
/// Захват системного звука (то, что слышно в наушниках/колонках) через WASAPI loopback.
/// Минимальная обёртка над COM-интерфейсами Windows — без сторонних библиотек.
/// Отдаёт PCM 16 бит стерео с частотой устройства (обычно 48 кГц).
/// </summary>
internal sealed class WasapiLoopback : IDisposable
{
    private const int ERender = 0;
    private const int EConsole = 0;
    private const int ClsctxAll = 0x17;
    private const int SharedMode = 0;
    private const int StreamFlagsLoopback = 0x00020000;
    private const int BufferFlagsSilent = 0x2;
    private const ushort WaveFormatIeeeFloat = 3;
    private const ushort WaveFormatExtensible = 0xFFFE;

    private static readonly Guid IidAudioClient = new("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2");
    private static readonly Guid IidAudioCaptureClient = new("C8ADBD64-E71E-48a0-A4DE-185C395CD317");
    private static readonly Guid SubtypeFloat = new("00000003-0000-0010-8000-00aa00389b71");

    private readonly IAudioClient _client;
    private readonly IAudioCaptureClient _capture;
    private readonly int _channels;
    private readonly int _bitsPerSample;
    private readonly bool _isFloat;

    public WasapiLoopback()
    {
        var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
        Marshal.ThrowExceptionForHR(enumerator.GetDefaultAudioEndpoint(ERender, EConsole, out var device));
        var iid = IidAudioClient;
        Marshal.ThrowExceptionForHR(device.Activate(ref iid, ClsctxAll, IntPtr.Zero, out var clientObj));
        _client = (IAudioClient)clientObj;

        Marshal.ThrowExceptionForHR(_client.GetMixFormat(out var formatPtr));
        try
        {
            var tag = (ushort)Marshal.ReadInt16(formatPtr, 0);
            _channels = Marshal.ReadInt16(formatPtr, 2);
            SampleRate = Marshal.ReadInt32(formatPtr, 4);
            _bitsPerSample = Marshal.ReadInt16(formatPtr, 14);
            _isFloat = tag == WaveFormatIeeeFloat
                || (tag == WaveFormatExtensible && Marshal.PtrToStructure<Guid>(formatPtr + 24) == SubtypeFloat);

            // 100 мс буфер в общем режиме; формат — микшерный формат устройства.
            Marshal.ThrowExceptionForHR(_client.Initialize(SharedMode, StreamFlagsLoopback, 1_000_000, 0, formatPtr, IntPtr.Zero));
        }
        finally
        {
            Marshal.FreeCoTaskMem(formatPtr);
        }

        if (!(_isFloat && _bitsPerSample == 32) && _bitsPerSample != 16)
        {
            throw new NotSupportedException($"Формат звука устройства не поддерживается: {_bitsPerSample} бит.");
        }

        var cid = IidAudioCaptureClient;
        Marshal.ThrowExceptionForHR(_client.GetService(ref cid, out var captureObj));
        _capture = (IAudioCaptureClient)captureObj;
    }

    public int SampleRate { get; }

    public void Start() => Marshal.ThrowExceptionForHR(_client.Start());

    /// <summary>Забирает накопленные пакеты и возвращает их как PCM 16 бит стерео (может быть пусто).</summary>
    public byte[] Read()
    {
        using var output = new MemoryStream();
        while (_capture.GetNextPacketSize(out var packet) == 0 && packet > 0)
        {
            if (_capture.GetBuffer(out var data, out var frames, out var flags, out _, out _) != 0)
            {
                break;
            }

            var bytes = new byte[frames * 4];
            if ((flags & BufferFlagsSilent) == 0)
            {
                for (var i = 0; i < frames; i++)
                {
                    for (var ch = 0; ch < 2; ch++)
                    {
                        // Многоканальный звук сводится к первым двум каналам (левый/правый).
                        var srcCh = Math.Min(ch, _channels - 1);
                        short value;
                        if (_isFloat)
                        {
                            var f = BitConverter.Int32BitsToSingle(Marshal.ReadInt32(data, ((i * _channels) + srcCh) * 4));
                            value = (short)Math.Clamp(f * 32767f, short.MinValue, short.MaxValue);
                        }
                        else
                        {
                            value = Marshal.ReadInt16(data, ((i * _channels) + srcCh) * 2);
                        }

                        bytes[(i * 4) + (ch * 2)] = (byte)value;
                        bytes[(i * 4) + (ch * 2) + 1] = (byte)(value >> 8);
                    }
                }
            }

            output.Write(bytes);
            _capture.ReleaseBuffer(frames);
        }

        return output.ToArray();
    }

    public void Dispose()
    {
        try
        {
            _client.Stop();
        }
        catch (COMException)
        {
            // Устройство могло быть отключено — освобождаем ресурсы в любом случае.
        }

        Marshal.ReleaseComObject(_capture);
        Marshal.ReleaseComObject(_client);
    }

    [ComImport]
    [Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private class MMDeviceEnumeratorComObject;

    [ComImport]
    [Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);

        int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice endpoint);
    }

    [ComImport]
    [Guid("D666063F-1587-4E43-81F1-B948E807363F")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object instance);
    }

    [ComImport]
    [Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioClient
    {
        int Initialize(int shareMode, int streamFlags, long bufferDuration, long periodicity, IntPtr format, IntPtr sessionGuid);

        int GetBufferSize(out uint frames);

        int GetStreamLatency(out long latency);

        int GetCurrentPadding(out uint padding);

        int IsFormatSupported(int shareMode, IntPtr format, out IntPtr closest);

        int GetMixFormat(out IntPtr format);

        int GetDevicePeriod(out long defaultPeriod, out long minimumPeriod);

        int Start();

        int Stop();

        int Reset();

        int SetEventHandle(IntPtr handle);

        int GetService(ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object service);
    }

    [ComImport]
    [Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioCaptureClient
    {
        int GetBuffer(out IntPtr data, out int frames, out int flags, out long devicePosition, out long qpcPosition);

        int ReleaseBuffer(int frames);

        int GetNextPacketSize(out int frames);
    }
}
