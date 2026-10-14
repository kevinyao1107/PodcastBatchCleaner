using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PodcastBatchCleaner.Core.Services;

public sealed record PortableTone(bool EqEnabled, double LowEqDb, double MidEqDb, double HighEqDb, bool DeEsserEnabled, double DeEsserAmount);
public sealed record PortableCore(double GainDb, bool LoudnessEnabled, int LoudnessTarget,
    bool SilenceTrimEnabled, double SilenceThresholdDb, double MinimumSilenceSeconds,
    double SilencePaddingSeconds, string ChannelMode, PortableTone? Tone = null);
public sealed record PortablePreset(string Name, PortableCore Core, Dictionary<string, JsonElement>? Extensions = null);
public sealed record PortablePresetDocument(string Format, int SchemaVersion, List<PortablePreset> Presets);

public static class PortablePresetFile
{
    public const int SchemaVersion = 2;
    public const string Format = "audio-studio-presets";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip
    };
    public static void Validate(PortableCore c)
    {
        static bool InRange(double n, double min, double max) => double.IsFinite(n) && n >= min && n <= max;
        if (!InRange(c.GainDb, -12, 12) || !new[] { -14, -16, -18, -23 }.Contains(c.LoudnessTarget)
            || !InRange(c.SilenceThresholdDb, -70, -25) || !InRange(c.MinimumSilenceSeconds, .1, 10)
            || !InRange(c.SilencePaddingSeconds, 0, .5) || !new[] { "source", "mono", "left", "right", "swap" }.Contains(c.ChannelMode))
            throw new InvalidDataException("共用預設核心參數超出範圍。");
        if (c.Tone is { } t && (!InRange(t.LowEqDb, -12, 12) || !InRange(t.MidEqDb, -12, 12) || !InRange(t.HighEqDb, -12, 12) || !InRange(t.DeEsserAmount, 0, 100)))
            throw new InvalidDataException("共用 EQ／齒音參數超出範圍。");
    }
    public static List<PortablePreset> Parse(string json)
    {
        if (System.Text.Encoding.UTF8.GetByteCount(json) > 1024 * 1024) throw new InvalidDataException("預設檔案不可超過 1 MB。");
        var doc = JsonSerializer.Deserialize<PortablePresetDocument>(json, JsonOptions);
        if (doc is null || doc.Format != Format || doc.SchemaVersion is not (1 or 2) || doc.Presets is null || doc.Presets.Count is < 1 or > 100)
            throw new InvalidDataException("共用預設格式、版本或數量無效。");
        foreach (var p in doc.Presets)
        {
            if (p is null || string.IsNullOrWhiteSpace(p.Name) || p.Name.Length > 40 || p.Name.Any(char.IsControl) || p.Core is null)
                throw new InvalidDataException("預設名稱或核心設定無效。");
            // Require every field, including booleans, rather than accepting missing JSON as false/zero.
            Validate(p.Core);
            if (doc.SchemaVersion == 2 && p.Core.Tone is null) throw new InvalidDataException("第二版共用預設缺少 EQ／齒音設定。");
            if (p.Extensions?.TryGetValue("desktop", out var native) == true)
            {
                if (native.ValueKind != JsonValueKind.Object) throw new InvalidDataException("桌面效果設定必須是物件。");
                foreach (var key in new[] { "EnableDenoise", "ReduceRoomTone", "ReduceReverb", "EnhanceVoiceEq", "EnableLimiter" })
                    if (native.TryGetProperty(key, out var v) && v.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw new InvalidDataException($"桌面開關無效：{key}");
                if (native.TryGetProperty("DenoiseStrengthIndex", out var strength) && (!strength.TryGetInt32(out var n) || n is < 0 or > 2)) throw new InvalidDataException("降噪強度無效。");
                if (native.TryGetProperty("ReverbReductionAmount", out var reverb) && (!reverb.TryGetDouble(out var amount) || !double.IsFinite(amount) || amount is < .2 or > 1)) throw new InvalidDataException("空間感強度無效。");
            }
        }
        using var raw = JsonDocument.Parse(json);
        foreach (var p in raw.RootElement.GetProperty("presets").EnumerateArray())
        {
            var core = p.GetProperty("core");
            foreach (var key in new[] { "gainDb", "loudnessEnabled", "loudnessTarget", "silenceTrimEnabled", "silenceThresholdDb", "minimumSilenceSeconds", "silencePaddingSeconds", "channelMode" })
                if (!core.TryGetProperty(key, out _)) throw new InvalidDataException($"共用預設缺少參數：{key}");
            if (doc.SchemaVersion == 2)
            {
                var tone = core.GetProperty("tone");
                foreach (var key in new[] { "eqEnabled", "lowEqDb", "midEqDb", "highEqDb", "deEsserEnabled", "deEsserAmount" })
                    if (!tone.TryGetProperty(key, out _)) throw new InvalidDataException($"第二版預設缺少參數：{key}");
            }
        }
        if (doc.SchemaVersion == 1) return doc.Presets.Select(p => p with { Core = p.Core with { Tone = null } }).ToList();
        return doc.Presets;
    }
    private static PortableTone UpgradeTone(PortablePreset preset)
    {
        if (preset.Extensions?.TryGetValue("soundlab", out var n) == true && n.ValueKind == JsonValueKind.Object)
        {
            bool Flag(string key, bool fallback = false) => n.TryGetProperty(key, out var v) ? v.ValueKind == JsonValueKind.True : fallback;
            double Value(string key, double fallback = 0) => n.TryGetProperty(key, out var v) ? v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var number) ? number : throw new InvalidDataException($"舊版 EQ／齒音設定無效：{key}") : fallback;
            return new PortableTone(Flag("threeBandEqEnabled", Flag("eqEnabled", true)), Value("lowEq"), Value("midEq"), Value("highEq"), Flag("deEsserEnabled"), Value("deEsserAmount", 45));
        }
        return new PortableTone(false, 0, 0, 0, false, 45);
    }
    public static string Serialize(IEnumerable<PortablePreset> presets)
    {
        var json = JsonSerializer.Serialize(new PortablePresetDocument(Format, SchemaVersion, presets.Select(p => p with { Core = p.Core with { Tone = p.Core.Tone ?? UpgradeTone(p) } }).ToList()), JsonOptions);
        Parse(json);
        return json;
    }
}
