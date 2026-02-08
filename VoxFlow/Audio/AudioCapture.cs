using NAudio.Wave;
using System;
using System.IO;

namespace VoxFlow.Audio
{
    public class AudioCapture : IDisposable
    {
        private WasapiLoopbackCapture? _capture;
        private WaveFormat _targetFormat;
        private bool _isCapturing;
        private DateTime _startTime;
        private double _streamAbsTimeSec;
        private readonly byte[] _readBuffer;

        public event Action<byte[]>? OnAudioData;

        public double StreamAbsTimeSec => _streamAbsTimeSec;

        public AudioCapture()
        {
            // Цільовий формат: mono, 16kHz, 16-bit PCM
            _targetFormat = new WaveFormat(16000, 16, 1);
            _readBuffer = new byte[_targetFormat.AverageBytesPerSecond * 2]; // Буфер на 2 секунди
        }

        public void Start()
        {
            if (_isCapturing) return;

            try
            {
                _capture = new WasapiLoopbackCapture();
                
                _startTime = DateTime.Now;
                _streamAbsTimeSec = 0;

                _capture.DataAvailable += (sender, e) =>
                {
                    if (!_isCapturing) return;

                    // Оновити час потоку
                    _streamAbsTimeSec = (DateTime.Now - _startTime).TotalSeconds;

                    // Конвертувати дані в mono 16kHz
                    byte[] convertedData = ConvertToMono16kHz(e.Buffer, e.BytesRecorded, _capture.WaveFormat);
                    
                    if (convertedData.Length > 0)
                    {
                        OnAudioData?.Invoke(convertedData);
                    }
                    else if (e.BytesRecorded > 0)
                    {
                        // Логировать если данные были, но конвертация вернула пустой массив
                        System.Diagnostics.Debug.WriteLine($"[AudioCapture] Data received but conversion returned empty (source: {_capture.WaveFormat.SampleRate}Hz, {_capture.WaveFormat.Channels}ch, {_capture.WaveFormat.BitsPerSample}bit)");
                    }
                };

                _capture.RecordingStopped += (sender, e) =>
                {
                    // Обробка зупинки
                };

                _capture.StartRecording();
                _isCapturing = true;
                System.Diagnostics.Debug.WriteLine("[AudioCapture] Recording started successfully");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[AudioCapture] ERROR: Failed to start recording: {ex.Message}");
                System.Diagnostics.Debug.WriteLine($"[AudioCapture] StackTrace: {ex.StackTrace}");
                _isCapturing = false;
                _capture?.Dispose();
                _capture = null;
                throw; // Перебросити исключение для обработки выше
            }
        }

        public void Stop()
        {
            if (!_isCapturing) return;

            _isCapturing = false;
            _capture?.StopRecording();
        }

        /// <summary>Конвертує в mono 16 kHz 16-bit PCM little-endian (як у WAV). Float 32-bit — ручна конвертація з ресемплингом; інші формати — NAudio WaveFormatConversionStream.</summary>
        private byte[] ConvertToMono16kHz(byte[] inputBuffer, int bytesRecorded, WaveFormat sourceFormat)
        {
            // Якщо формат вже підходить, повернути як є
            if (sourceFormat.SampleRate == 16000 && sourceFormat.Channels == 1 && sourceFormat.BitsPerSample == 16)
            {
                byte[] result = new byte[bytesRecorded];
                Array.Copy(inputBuffer, result, bytesRecorded);
                return result;
            }

            // Ручна конвертація для 32-bit float формату (WasapiLoopbackCapture часто використовує float)
            if (sourceFormat.Encoding == WaveFormatEncoding.IeeeFloat && sourceFormat.BitsPerSample == 32)
            {
                return ConvertFloatToMono16kHz(inputBuffer, bytesRecorded, sourceFormat);
            }

            // Для інших форматів - спробувати WaveFormatConversionStream
            try
            {
                using (var inputStream = new RawSourceWaveStream(new MemoryStream(inputBuffer, 0, bytesRecorded), sourceFormat))
                using (var conversionStream = new WaveFormatConversionStream(_targetFormat, inputStream))
                {
                    using (var outputStream = new MemoryStream())
                    {
                        // Копіювати дані по частинах для ефективності
                        byte[] buffer = new byte[1024];
                        int bytesRead;
                        while ((bytesRead = conversionStream.Read(buffer, 0, buffer.Length)) > 0)
                        {
                            outputStream.Write(buffer, 0, bytesRead);
                        }
                        return outputStream.ToArray();
                    }
                }
            }
            catch (NAudio.MmException ex)
            {
                // Подавити NAudio.MmException (не критично для работы)
                System.Diagnostics.Debug.WriteLine($"[AudioCapture] WinMM conversion warning (suppressed): {ex.Message}");
                return new byte[0];
            }
            catch (Exception ex)
            {
                // Логировать другие исключения
                System.Diagnostics.Debug.WriteLine($"[AudioCapture] Conversion error: {ex.Message}");
                return new byte[0];
            }
        }

        private byte[] ConvertFloatToMono16kHz(byte[] inputBuffer, int bytesRecorded, WaveFormat sourceFormat)
        {
            int sourceSampleRate = sourceFormat.SampleRate;
            int sourceChannels = sourceFormat.Channels;
            int targetSampleRate = 16000;
            int targetChannels = 1;

            // Розрахувати кількість float сэмплів (32-bit float = 4 bytes)
            int floatSampleCount = bytesRecorded / 4;
            int sourceFrameCount = floatSampleCount / sourceChannels;

            if (sourceFrameCount == 0)
                return new byte[0];

            // Ресемплинг: targetSampleRate / sourceSampleRate
            double resampleRatio = (double)targetSampleRate / sourceSampleRate;
            int targetFrameCount = (int)(sourceFrameCount * resampleRatio);
            int targetByteCount = targetFrameCount * targetChannels * 2; // 16-bit = 2 bytes

            if (targetByteCount <= 0)
                return new byte[0];

            byte[] output = new byte[targetByteCount];

            // Конвертувати byte[] в float[]
            float[] inputFloats = new float[floatSampleCount];
            Buffer.BlockCopy(inputBuffer, 0, inputFloats, 0, bytesRecorded);

            for (int i = 0; i < targetFrameCount; i++)
            {
                // Знайти відповідний frame в source (з лінійною інтерполяцією)
                double sourcePos = i / resampleRatio;
                int sourceIndex = (int)sourcePos;
                double fraction = sourcePos - sourceIndex;

                if (sourceIndex >= sourceFrameCount - 1)
                {
                    sourceIndex = sourceFrameCount - 1;
                    fraction = 0;
                }

                // Конвертувати в mono: середнє значення всіх каналів
                float sample = 0;
                if (sourceChannels == 1)
                {
                    sample = inputFloats[sourceIndex];
                    if (fraction > 0 && sourceIndex + 1 < sourceFrameCount)
                    {
                        sample = (float)(sample * (1 - fraction) + inputFloats[sourceIndex + 1] * fraction);
                    }
                }
                else
                {
                    // Стерео або більше каналів - середнє значення
                    float sample1 = 0, sample2 = 0;
                    for (int ch = 0; ch < sourceChannels; ch++)
                    {
                        int idx1 = sourceIndex * sourceChannels + ch;
                        if (idx1 < inputFloats.Length)
                        {
                            sample1 += inputFloats[idx1];
                        }
                        if (fraction > 0 && sourceIndex + 1 < sourceFrameCount)
                        {
                            int idx2 = (sourceIndex + 1) * sourceChannels + ch;
                            if (idx2 < inputFloats.Length)
                            {
                                sample2 += inputFloats[idx2];
                            }
                        }
                    }
                    sample1 /= sourceChannels;
                    sample2 /= sourceChannels;
                    sample = (float)(sample1 * (1 - fraction) + sample2 * fraction);
                }

                // Кліппінг та конвертація в 16-bit PCM (little-endian, як у WAV)
                sample = Math.Max(-1.0f, Math.Min(1.0f, sample));
                short pcmSample = (short)(sample * 32767.0f);

                // Записати short (2 bytes) little-endian: спочатку молодший байт
                int outputIndex = i * 2;
                if (outputIndex + 1 < output.Length)
                {
                    output[outputIndex] = (byte)(pcmSample & 0xFF);
                    output[outputIndex + 1] = (byte)((pcmSample >> 8) & 0xFF);
                }
            }

            return output;
        }

        public void Dispose()
        {
            Stop();
            _capture?.Dispose();
        }
    }
}