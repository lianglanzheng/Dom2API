using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Dom2API.Models;

namespace Dom2API.Services;

/// <summary>将全部请求快照及完整网络消息导出为 JSON。</summary>
internal static class NetworkRecordExporter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        NewLine = "\r\n",
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    /// <summary>流式写入同目录临时文件，完整写入后才替换目标文件。</summary>
    public static async Task ExportAsync(string filePath, IReadOnlyList<CaptureRecord> records, DateTimeOffset exportedAt)
    {
        var document = new
        {
            Format = "Dom2API.NetworkRecords",
            Version = 1,
            ExportedAt = exportedAt,
            RequestCount = records.Count,
            NetworkMessageCount = records.Sum(record => (long)record.NetworkMessages.Count),
            Records = records.Select(record => new
            {
                record.Id,
                record.SessionId,
                record.ThreadId,
                record.StartedAt,
                record.Method,
                record.Path,
                record.UpstreamUrl,
                record.UpstreamId,
                record.UpstreamName,
                record.Model,
                record.StatusCode,
                record.IsCompleted,
                record.DurationMilliseconds,
                record.FirstByteMilliseconds,
                record.FirstResponseMilliseconds,
                record.ResponseMilliseconds,
                record.IsEventStream,
                record.CloseReason,
                record.Error,
                record.AnalysisNote,
                record.Usage,
                NetworkMessages = record.NetworkMessages.Select(message => new
                {
                    message.Sequence,
                    message.Timestamp,
                    message.Direction,
                    message.Kind,
                    message.Content
                })
            })
        };
        string temporaryPath = filePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await stream.WriteAsync(Encoding.UTF8.GetPreamble()).ConfigureAwait(false);
                await JsonSerializer.SerializeAsync(stream, document, JsonOptions).ConfigureAwait(false);
                await stream.WriteAsync("\r\n"u8.ToArray()).ConfigureAwait(false);
            }
            File.Move(temporaryPath, filePath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }
}
