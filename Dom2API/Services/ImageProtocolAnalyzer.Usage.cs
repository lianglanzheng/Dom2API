using System.Text.Json;
using Dom2API.Models;
using Microsoft.Extensions.AI;

namespace Dom2API.Services;

internal static partial class ImageProtocolAnalyzer
{
    public static UsageDetails? ReadUsage(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("usage", out var usage)
            || usage.ValueKind != JsonValueKind.Object) return null;
        var result = new UsageDetails
        {
            InputTokenCount = ReadCount(usage, "input_tokens"),
            OutputTokenCount = ReadCount(usage, "output_tokens"),
            TotalTokenCount = ReadCount(usage, "total_tokens")
        };
        foreach (string direction in new[] { "input", "output" })
        {
            if (!usage.TryGetProperty(direction + "_tokens_details", out var details) || details.ValueKind != JsonValueKind.Object) continue;
            if (direction == "input") result.CachedInputTokenCount = ReadCount(details, "cached_tokens");
            foreach (string modality in new[] { "image", "text" })
            {
                if (ReadCount(details, modality + "_tokens") is { } count)
                {
                    result.AdditionalCounts ??= [];
                    result.AdditionalCounts[direction + "_" + modality + "_tokens"] = count;
                }
            }
        }
        return result.InputTokenCount is not null || result.OutputTokenCount is not null || result.TotalTokenCount is not null
            || result.CachedInputTokenCount is not null || result.AdditionalCounts is { Count: > 0 } ? result : null;
    }

    private static void ReadImageUsage(CaptureRecord record, JsonElement root)
    {
        if (ReadUsage(root) is { } usage)
        {
            record.Usage = usage;
            record.ImageUsage = usage;
        }
    }

    private static void ReadToolUsage(CaptureRecord record, string key, JsonElement item)
    {
        if (ReadUsage(item) is not { } usage) return;
        record.ImageToolUsages = new Dictionary<string, UsageDetails>(record.ImageToolUsages) { [key] = usage };
        var total = new UsageDetails();
        foreach (var entry in record.ImageToolUsages.Values) total.Add(entry);
        // 工具用量单独展示；Responses 顶层 usage 仍是全请求统计的唯一来源。
        record.ImageUsage = total;
    }

    private static long? ReadCount(JsonElement root, string name)
        => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt64(out long count) && count >= 0 ? count : null;
}
