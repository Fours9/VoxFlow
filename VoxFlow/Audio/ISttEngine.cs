using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using VoxFlow.Core;

namespace VoxFlow.Audio
{
    public interface ISttEngine : IDisposable
    {
        Task WarmUp();

        Task<List<TextSegment>> TranscribeAsync(string wavPath);
    }
}

