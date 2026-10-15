using System.IO;

namespace PodcastBatchCleaner.Core.Models;

public sealed record VoiceToneTuning(double ThicknessDb = 2, double ClarityDb = 1, bool UseCompression = true)
{
    public void Validate()
    {
        if (!double.IsFinite(ThicknessDb) || ThicknessDb < 0 || ThicknessDb > 6
            || !double.IsFinite(ClarityDb) || ClarityDb < 0 || ClarityDb > 4)
            throw new InvalidDataException("人聲音色參數超出有效範圍。");
    }
}
