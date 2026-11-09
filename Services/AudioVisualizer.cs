using System.IO;
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.Dsp;
using NAudio.Wave;

namespace MusicOverlay.Services;

/// <summary>
/// Захват системного звука (WASAPI loopback) и расчёт спектра по 40 полосам.
/// </summary>
public sealed class AudioVisualizer : IDisposable
{
    public const int BandCount = 40;

    private const int FftSize = 4096;
    private const int FftBits = 12; // log2(FftSize)
    private const double MinFrequency = 45.0;
    private const double MaxFrequency = 15500.0;
    private const double MinDb = -54.0;

    private readonly object _gate = new();
    private readonly float[] _ring = new float[FftSize];
    private readonly float[] _scratch = new float[FftSize];
    private readonly float[] _window = new float[FftSize];
    private readonly Complex[] _fft = new Complex[FftSize];
    private readonly float[] _levels = new float[BandCount];
    private readonly int[] _bandStart = new int[BandCount];
    private readonly int[] _bandEnd = new int[BandCount];

    private WasapiRecorder? _recorder;
    private int _ringWrite;
    private int _ringCount;
    private int _logCounter;
    private long _totalFrames;
    private long _silentFrames;
    private float _inputPeak;
    private double _lastDb = -120;
    private volatile bool _running;

    private static readonly string LogPath =
        Path.Combine(Path.GetTempPath(), "musicoverlay_debug.log");

    // KSDATAFORMAT_SUBTYPE_IEEE_FLOAT и KSDATAFORMAT_SUBTYPE_PCM
    private static readonly Guid IeeeFloatSubFormat = new("00000003-0000-0010-8000-00aa00389b71");
    private static readonly Guid PcmSubFormat = new("00000001-0000-0010-8000-00aa00389b71");

    private bool _skipLogged;

    public bool IsRunning => _running;

    public string? Error { get; private set; }

    public AudioVisualizer()
    {
        // окно Ханна
        for (int i = 0; i < FftSize; i++)
        {
            _window[i] = 0.5f * (1f - MathF.Cos(2f * MathF.PI * i / (FftSize - 1)));
        }

        BuildBands(48000);
    }

    public void Start()
    {
        if (_recorder is not null || _running)
        {
            return;
        }

        try
        {
            _recorder = new WasapiRecorderBuilder().WithLoopbackCapture().Build();
            _recorder.DataAvailable += OnDataAvailable;
            _recorder.StartRecording();

            WaveFormat? format = _recorder.WaveFormat;
            if (format is not null)
            {
                BuildBands(format.SampleRate);
            }

            _running = true;
            Error = null;

            var fmt = _recorder.WaveFormat;
            WriteLog($"START fmt={(fmt is null ? "?" : $"{fmt.Channels}ch {fmt.SampleRate}Hz {fmt.Encoding} {fmt.BitsPerSample}bit")} device='{_recorder.DeviceFriendlyName}' state={_recorder.CaptureState}");
        }
        catch (Exception ex)
        {
            Error = ex.Message;
            WriteLog($"START FAILED: {ex}");
            Dispose();
        }
    }

    private static void WriteLog(string line)
    {
        try
        {
            File.AppendAllText(LogPath, $"{DateTime.Now:HH:mm:ss.fff} {line}{Environment.NewLine}");
        }
        catch
        {
            // логирование не критично
        }
    }

    /// <summary>Копирует текущие уровни полос (0..1) в буфер вызывающего.</summary>
    public void CopyLevels(float[] destination)
    {
        lock (_gate)
        {
            Array.Copy(_levels, destination, Math.Min(_levels.Length, destination.Length));
        }
    }

    private void BuildBands(int sampleRate)
    {
        double nyquist = sampleRate / 2.0;
        double maxFrequency = Math.Min(MaxFrequency, nyquist * 0.95);

        for (int b = 0; b < BandCount; b++)
        {
            double f0 = MinFrequency * Math.Pow(maxFrequency / MinFrequency, (double)b / BandCount);
            double f1 = MinFrequency * Math.Pow(maxFrequency / MinFrequency, (double)(b + 1) / BandCount);

            int i0 = (int)Math.Floor(f0 / nyquist * FftSize / 2);
            int i1 = (int)Math.Ceiling(f1 / nyquist * FftSize / 2);

            if (i0 < 1)
            {
                i0 = 1;
            }

            if (i1 <= i0)
            {
                i1 = i0 + 1;
            }

            if (i1 > FftSize / 2)
            {
                i1 = FftSize / 2;
            }

            _bandStart[b] = i0;
            _bandEnd[b] = i1;
        }
    }

    private void OnDataAvailable(ReadOnlySpan<byte> buffer, AudioClientBufferFlags flags, long devicePosition, long qpcPosition)
    {
        if (!_running || _recorder is null || buffer.IsEmpty)
        {
            return;
        }

        WaveFormat format = _recorder.WaveFormat;
        int channels = format.Channels;
        int bits = format.BitsPerSample;
        if (channels < 1 || bits < 8)
        {
            return;
        }

        int blockAlign = bits / 8 * channels;
        if (blockAlign <= 0)
        {
            return;
        }

        // при флаге Silent содержимое буфера не валидно — это тишина
        if ((flags & AudioClientBufferFlags.Silent) != 0)
        {
            int silentFrames = Math.Min(buffer.Length / blockAlign, _scratch.Length);
            Array.Clear(_scratch, 0, _scratch.Length);
            _silentFrames += silentFrames;
            PushToRing(silentFrames);
            return;
        }

        float bufferPeak = 0f;
        int frames;

        if (bits == 32 && IsIeeeFloat(format))
        {
            var samples = MemoryMarshal.Cast<byte, float>(buffer);
            int available = samples.Length / channels;
            int skip = Math.Max(0, available - _scratch.Length);
            frames = Math.Min(available, _scratch.Length);

            for (int f = 0; f < frames; f++)
            {
                int baseIndex = (f + skip) * channels;
                float acc = 0f;
                for (int c = 0; c < channels; c++)
                {
                    acc += samples[baseIndex + c];
                }

                float mono = acc / channels;
                float abs = mono < 0 ? -mono : mono;
                if (abs > bufferPeak)
                {
                    bufferPeak = abs;
                }

                _scratch[f] = mono;
            }
        }
        else if (bits == 16 && IsPcm(format))
        {
            var samples = MemoryMarshal.Cast<byte, short>(buffer);
            int available = samples.Length / channels;
            int skip = Math.Max(0, available - _scratch.Length);
            frames = Math.Min(available, _scratch.Length);

            for (int f = 0; f < frames; f++)
            {
                int baseIndex = (f + skip) * channels;
                int acc = 0;
                for (int c = 0; c < channels; c++)
                {
                    acc += samples[baseIndex + c];
                }

                _scratch[f] = acc / (channels * 32768f);

                float abs = _scratch[f] < 0 ? -_scratch[f] : _scratch[f];
                if (abs > bufferPeak)
                {
                    bufferPeak = abs;
                }
            }
        }
        else
        {
            // неподдерживаемый формат — просто пропускаем
            if (!_skipLogged)
            {
                _skipLogged = true;
                WriteLog($"SKIP fmt={format.Encoding} {bits}bit sub={(format as WaveFormatExtensible)?.SubFormat}");
            }

            return;
        }

        if (bufferPeak > _inputPeak)
        {
            _inputPeak = bufferPeak;
        }

        PushToRing(frames);
    }

    private static bool IsIeeeFloat(WaveFormat format) =>
        format.Encoding == WaveFormatEncoding.IeeeFloat ||
        (format is WaveFormatExtensible ext && ext.SubFormat == IeeeFloatSubFormat);

    private static bool IsPcm(WaveFormat format) =>
        format.Encoding == WaveFormatEncoding.Pcm ||
        (format is WaveFormatExtensible ext && ext.SubFormat == PcmSubFormat);

    private void PushToRing(int frames)
    {
        if (frames <= 0)
        {
            return;
        }

        lock (_gate)
        {
            _totalFrames += frames;

            for (int i = 0; i < frames; i++)
            {
                _ring[_ringWrite] = _scratch[i];
                if (++_ringWrite >= FftSize)
                {
                    _ringWrite = 0;
                }

                if (_ringCount < FftSize)
                {
                    _ringCount++;
                }
            }

            if (_ringCount >= FftSize)
            {
                ComputeLevels();
            }
        }
    }

    private void ComputeLevels()
    {
        // кольцевой буфер целиком: от позиции записи (это и есть самая старая точка)
        int start = _ringWrite;
        for (int i = 0; i < FftSize; i++)
        {
            int index = start + i;
            if (index >= FftSize)
            {
                index -= FftSize;
            }

            _fft[i].X = _ring[index] * _window[i];
            _fft[i].Y = 0f;
        }

        FastFourierTransform.FFT(true, FftBits, _fft);

        double maxDb = -120;

        for (int b = 0; b < BandCount; b++)
        {
            float peak = 0f;
            for (int k = _bandStart[b]; k < _bandEnd[b]; k++)
            {
                float x = _fft[k].X;
                float y = _fft[k].Y;
                float magnitude = MathF.Sqrt(x * x + y * y);
                if (magnitude > peak)
                {
                    peak = magnitude;
                }
            }

            // FFT нормирована (1/N): полная синусоида даёт ~0.25 → 0 дБ
            double db = 20.0 * Math.Log10(peak / 0.25 + 1e-9);
            if (db > maxDb)
            {
                maxDb = db;
            }

            double value = (db - MinDb) / (0.0 - MinDb);
            _levels[b] = (float)(value <= 0 ? 0 : value >= 1 ? 1 : value);
        }

        _lastDb = maxDb;

        if (++_logCounter >= 25)
        {
            _logCounter = 0;
            var recorder = _recorder;
            WriteLog(
                $"frames={_totalFrames} silent={_silentFrames} inPeak={_inputPeak:F4} peakDb={_lastDb:F1} " +
                $"state={(recorder is null ? "?" : recorder.CaptureState.ToString())} err={Error ?? "-"}");
            _inputPeak = 0f;
        }
    }

    public void Dispose()
    {
        _running = false;

        WasapiRecorder? recorder = _recorder;
        _recorder = null;
        if (recorder is null)
        {
            return;
        }

        recorder.DataAvailable -= OnDataAvailable;
        try
        {
            recorder.StopRecording();
        }
        catch
        {
            // устройство могло уже исчезнуть
        }

        recorder.Dispose();
    }
}
