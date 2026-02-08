using System;
using System.IO;

namespace WhisperService;

// Читает WAV файл и конвертирует в PCM float32 16kHz mono для Whisper
internal static class WavReader
{
    private const int TargetSampleRate = 16000;
    private const int TargetChannels = 1; // mono

    // Читает WAV файл и возвращает массив PCM float32 (16kHz, mono)
    public static float[] ReadWavToFloat32(string wavPath)
    {
        using var fileStream = new FileStream(wavPath, FileMode.Open, FileAccess.Read);
        using var reader = new BinaryReader(fileStream);

        // Читаем WAV заголовок
        string riff = ReadString(reader, 4);
        if (riff != "RIFF")
            throw new InvalidDataException("Invalid WAV file: missing RIFF header");

        int fileSize = reader.ReadInt32();
        string wave = ReadString(reader, 4);
        if (wave != "WAVE")
            throw new InvalidDataException("Invalid WAV file: missing WAVE header");

        // Ищем fmt chunk
        int sampleRate = 16000;
        int channels = 1;
        int bitsPerSample = 16;
        int blockAlign = 2;

        while (fileStream.Position < fileStream.Length)
        {
            string chunkId = ReadString(reader, 4);
            int chunkSize = reader.ReadInt32();

            if (chunkId == "fmt ")
            {
                int audioFormat = reader.ReadInt16();
                channels = reader.ReadInt16();
                sampleRate = reader.ReadInt32();
                int byteRate = reader.ReadInt32();
                blockAlign = reader.ReadInt16();
                bitsPerSample = reader.ReadInt16();

                // Пропускаем оставшиеся байты chunk (если есть)
                if (chunkSize > 16)
                {
                    reader.ReadBytes(chunkSize - 16);
                }
            }
            else if (chunkId == "data")
            {
                // Найден data chunk - читаем аудио данные
                return ReadAudioData(reader, chunkSize, sampleRate, channels, bitsPerSample);
            }
            else
            {
                // Пропускаем неизвестный chunk
                if (chunkSize > 0)
                {
                    reader.ReadBytes(chunkSize);
                }
            }
        }

        throw new InvalidDataException("WAV file does not contain data chunk");
    }

    private static string ReadString(BinaryReader reader, int length)
    {
        byte[] bytes = reader.ReadBytes(length);
        return System.Text.Encoding.ASCII.GetString(bytes);
    }

    private static float[] ReadAudioData(BinaryReader reader, int dataSize, int sampleRate, int channels, int bitsPerSample)
    {
        int numSamples = dataSize / (bitsPerSample / 8) / channels;
        int totalSamples = numSamples * channels;

        // Читаем сырые данные
        byte[] rawData = reader.ReadBytes(dataSize);

        // Конвертируем в float32
        float[] floatData;
        if (bitsPerSample == 16)
        {
            floatData = ConvertInt16ToFloat32(rawData, totalSamples);
        }
        else if (bitsPerSample == 32)
        {
            floatData = ConvertInt32ToFloat32(rawData, totalSamples);
        }
        else
        {
            throw new NotSupportedException($"Unsupported bits per sample: {bitsPerSample}");
        }

        // Конвертируем в mono (если нужно)
        float[] monoData;
        if (channels > 1)
        {
            monoData = ConvertToMono(floatData, channels, numSamples);
        }
        else
        {
            monoData = floatData;
        }

        // Ресемплинг до 16kHz (если нужно)
        if (sampleRate != TargetSampleRate)
        {
            monoData = Resample(monoData, sampleRate, TargetSampleRate);
        }

        return monoData;
    }

    private static float[] ConvertInt16ToFloat32(byte[] rawData, int sampleCount)
    {
        float[] result = new float[sampleCount];
        for (int i = 0; i < sampleCount; i++)
        {
            short sample = BitConverter.ToInt16(rawData, i * 2);
            result[i] = sample / 32768.0f;
        }
        return result;
    }

    private static float[] ConvertInt32ToFloat32(byte[] rawData, int sampleCount)
    {
        float[] result = new float[sampleCount];
        for (int i = 0; i < sampleCount; i++)
        {
            int sample = BitConverter.ToInt32(rawData, i * 4);
            result[i] = sample / 2147483648.0f;
        }
        return result;
    }

    private static float[] ConvertToMono(float[] stereoData, int channels, int samplesPerChannel)
    {
        float[] monoData = new float[samplesPerChannel];
        for (int i = 0; i < samplesPerChannel; i++)
        {
            float sum = 0;
            for (int ch = 0; ch < channels; ch++)
            {
                sum += stereoData[i * channels + ch];
            }
            monoData[i] = sum / channels;
        }
        return monoData;
    }

    // Простой линейный ресемплинг (для более точного нужен более сложный алгоритм)
    private static float[] Resample(float[] data, int fromRate, int toRate)
    {
        if (fromRate == toRate)
            return data;

        int newLength = (int)((long)data.Length * toRate / fromRate);
        float[] result = new float[newLength];

        double ratio = (double)fromRate / toRate;
        for (int i = 0; i < newLength; i++)
        {
            double sourceIndex = i * ratio;
            int index1 = (int)sourceIndex;
            int index2 = Math.Min(index1 + 1, data.Length - 1);
            double fraction = sourceIndex - index1;

            result[i] = (float)(data[index1] * (1.0 - fraction) + data[index2] * fraction);
        }

        return result;
    }
}