using System;
using System.Threading;
using VoxFlow.Core;

namespace VoxFlow.Audio
{
    public class VadDetector
    {
        private const double SilenceThreshold = 0.007; // RMS threshold для тиші (чуть чувствительнее к тихой речи)
        private const double SilenceDurationSec = 1.0; // Мінімальна тривалість тиші для паузи

        private Timer? _silenceTimer;
        private DateTime _silenceStartTime;
        private bool _isInSilence = true; // Начинаем с предположения, что это тишина, чтобы правильно определить начало речи
        private PauseController? _pauseController;

        public event Action? OnSilenceDetected;
        public event Action? OnSpeechDetected;

        public void SetPauseController(PauseController pauseController)
        {
            _pauseController = pauseController;
        }

        public void ProcessAudioData(byte[] pcmData)
        {
            double rms = CalculateRMS(pcmData);
            bool isSpeech = rms > SilenceThreshold;

            if (isSpeech)
            {
                if (_isInSilence)
                {
                    // Мова після тиші - переход из тишины в речь
                    _isInSilence = false;
                    _silenceTimer?.Dispose();
                    _silenceTimer = null;
                    System.Diagnostics.Debug.WriteLine($"[VadDetector] Speech detected: RMS={rms:F6}");
                    OnSpeechDetected?.Invoke();
                    
                    if (_pauseController != null)
                    {
                        _pauseController.ApplySpeechResume();
                    }
                }
                // Если уже в режиме речи (_isInSilence == false), ничего не делаем
                // Это предотвращает повторные вызовы OnSpeechDetected для каждого пакета
            }
            else
            {
                // Тиша
                if (!_isInSilence)
                {
                    // Переход из речи в тишину
                    _isInSilence = true;
                    _silenceStartTime = DateTime.Now;
                    System.Diagnostics.Debug.WriteLine($"[VadDetector] Silence started: RMS={rms:F6}");
                    
                    // Запустити таймер для перевірки тривалості тиші
                    _silenceTimer = new Timer(SilenceTimerCallback, null, 
                        TimeSpan.FromSeconds(SilenceDurationSec), 
                        Timeout.InfiniteTimeSpan);
                }
            }
        }

        private void SilenceTimerCallback(object? state)
        {
            if (_isInSilence && (DateTime.Now - _silenceStartTime).TotalSeconds >= SilenceDurationSec)
            {
                // Тиша >= 1 секунда
                OnSilenceDetected?.Invoke();
                
                if (_pauseController != null)
                {
                    _pauseController.ApplyAutoSilencePause();
                }
            }
        }

        private double CalculateRMS(byte[] pcmData)
        {
            if (pcmData.Length < 2) return 0.0;

            long sumSquares = 0;
            int sampleCount = pcmData.Length / 2; // 16-bit = 2 bytes per sample

            for (int i = 0; i < pcmData.Length - 1; i += 2)
            {
                short sample = (short)(pcmData[i] | (pcmData[i + 1] << 8));
                sumSquares += (long)sample * sample;
            }

            if (sampleCount == 0) return 0.0;

            double meanSquare = (double)sumSquares / sampleCount;
            return Math.Sqrt(meanSquare) / 32768.0; // Normalize to [-1, 1]
        }

        public void Dispose()
        {
            _silenceTimer?.Dispose();
        }
    }
}