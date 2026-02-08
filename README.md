# VoxFlow

WPF-приложение для распознавания речи в реальном времени (Vosk, українська/російська/англійська).

## Запуск

1. Установите [.NET 8 SDK](https://dotnet.microsoft.com/download).
2. **Whisper:** положите бинарники моделей Whisper (например `ggml-base.bin`, `ggml-small.bin`) в папку `tools/whisper/models/`.
3. **Vosk:** скачайте модели Vosk в папки `tools/VoskModels/model/UK`, `tools/VoskModels/model/RU`, `tools/VoskModels/model/EN` (по необходимости).
4. Соберите и запустите: `dotnet run` из папки `VoxFlow` или откройте `VoxFlow.sln` в Visual Studio.
