using System.Text.Json.Serialization;
using LuaToolsGui.Resources;

namespace LuaToolsGui.Models;

public sealed class CanIRunItCommandOutput
{
    [JsonPropertyName("success")] public bool Success { get; set; }
    [JsonPropertyName("result")] public CanIRunItAnalysis? Result { get; set; }
    [JsonPropertyName("error")] public CanIRunItError? Error { get; set; }
}

public sealed class CanIRunItError
{
    [JsonPropertyName("code")] public string Code { get; set; } = "unknown";
    [JsonPropertyName("message")] public string Message { get; set; } = "";
}

public sealed class CanIRunItAnalysis
{
    [JsonPropertyName("app_id")] public long AppId { get; set; }
    [JsonPropertyName("game_name")] public string GameName { get; set; } = "";
    [JsonPropertyName("model")] public string Model { get; set; } = "";
    [JsonPropertyName("verdict")] public string Verdict { get; set; } = "";
    [JsonPropertyName("confidence")] public string Confidence { get; set; } = "";
    [JsonPropertyName("summary")] public string Summary { get; set; } = "";
    [JsonPropertyName("hardware")] public CanIRunItHardware Hardware { get; set; } = new();
    [JsonPropertyName("requirements")] public CanIRunItRequirements Requirements { get; set; } = new();
    [JsonPropertyName("components")] public List<CanIRunItComponent> Components { get; set; } = [];
    [JsonPropertyName("recommendations")] public List<string> Recommendations { get; set; } = [];
    [JsonPropertyName("caveats")] public List<string> Caveats { get; set; } = [];
    [JsonPropertyName("degraded")] public bool Degraded { get; set; }

    [JsonIgnore]
    public string VerdictLabel => Verdict switch
    {
        "recommended" => Strings.CanIRunIt_Verdict_Recommended,
        "minimum" => Strings.CanIRunIt_Verdict_Minimum,
        "poor" => Strings.CanIRunIt_Verdict_Poor,
        "unsupported" => Strings.CanIRunIt_Verdict_Unsupported,
        "inconclusive" => Strings.CanIRunIt_Verdict_Inconclusive,
        _ => Verdict,
    };

    [JsonIgnore]
    public string SummaryDisplay => Degraded || string.IsNullOrWhiteSpace(Summary)
        ? Strings.CanIRunIt_FallbackSummary
        : Summary;

    [JsonIgnore]
    public string ConfidenceLabel => string.Format(Strings.CanIRunIt_Confidence, Confidence switch
    {
        "high" => Strings.CanIRunIt_Confidence_High,
        "medium" => Strings.CanIRunIt_Confidence_Medium,
        _ => Strings.CanIRunIt_Confidence_Low,
    });

    [JsonIgnore] public bool HasRecommendations => Recommendations.Count > 0;
    [JsonIgnore] public bool HasCaveats => Caveats.Count > 0;
}

public sealed class CanIRunItHardware
{
    [JsonPropertyName("cpu")] public string Cpu { get; set; } = "";
    [JsonPropertyName("logical_processors")] public int LogicalProcessors { get; set; }
    [JsonPropertyName("max_clock_mhz")] public int MaxClockMhz { get; set; }
    [JsonPropertyName("memory_gb")] public double MemoryGb { get; set; }
    [JsonPropertyName("gpus")] public List<CanIRunItGpu> Gpus { get; set; } = [];
    [JsonPropertyName("os")] public string Os { get; set; } = "";
    [JsonPropertyName("system_drive_free_gb")] public double SystemDriveFreeGb { get; set; }

    [JsonIgnore] public string CpuSummary => LogicalProcessors > 0
        ? $"{Cpu} · {string.Format(Strings.CanIRunIt_LogicalProcessors, LogicalProcessors)}"
        : Cpu;
    [JsonIgnore] public string GpuSummary => Gpus.Count == 0
        ? Strings.CanIRunIt_Hardware_UnknownGpu
        : string.Join(" · ", Gpus.Select(g => g.VramGb > 0
            ? $"{g.Name} ({string.Format(Strings.CanIRunIt_GpuVram, g.VramGb)})"
            : g.Name));
    [JsonIgnore] public string MemorySummary => $"{MemoryGb:0.#} GB";
    [JsonIgnore] public string StorageSummary => $"{SystemDriveFreeGb:0.#} GB";
}

public sealed class CanIRunItGpu
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("vram_gb")] public double VramGb { get; set; }
    [JsonPropertyName("driver_version")] public string DriverVersion { get; set; } = "";
}

public sealed class CanIRunItRequirements
{
    [JsonPropertyName("minimum")] public string Minimum { get; set; } = "";
    [JsonPropertyName("recommended")] public string Recommended { get; set; } = "";
    [JsonPropertyName("source")] public string Source { get; set; } = "";

    [JsonIgnore] public string MinimumDisplay => string.IsNullOrWhiteSpace(Minimum)
        ? Strings.CanIRunIt_Requirements_NotPublished
        : Minimum;
    [JsonIgnore] public string RecommendedDisplay => string.IsNullOrWhiteSpace(Recommended)
        ? Strings.CanIRunIt_Requirements_NotPublished
        : Recommended;
}

public sealed class CanIRunItComponent
{
    [JsonPropertyName("component")] public string Component { get; set; } = "";
    [JsonPropertyName("status")] public string Status { get; set; } = "";
    [JsonPropertyName("explanation")] public string Explanation { get; set; } = "";

    [JsonIgnore]
    public string StatusLabel => Status switch
    {
        "exceeds" => Strings.CanIRunIt_Status_Exceeds,
        "meets" => Strings.CanIRunIt_Status_Meets,
        "below" => Strings.CanIRunIt_Status_Below,
        _ => Strings.CanIRunIt_Status_Unknown,
    };

    [JsonIgnore]
    public string ExplanationDisplay => string.IsNullOrWhiteSpace(Explanation)
        ? Strings.CanIRunIt_FallbackComponent
        : Explanation;
}
