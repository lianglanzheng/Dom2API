using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using Dom2API.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;

namespace Dom2API.Services;

internal sealed partial class ProxyService
{
    private static bool IsModelListRequest(HttpRequest request)
        => HttpMethods.IsGet(request.Method)
            && request.Path.Value?.TrimEnd('/') is "/v1/models" or "/models";

    private async Task ForwardModelsAsync(HttpContext context, TimeSpan timeout,
        OutboundClients httpClients, CancellationToken token)
    {
        string rawTarget = context.Features.Get<IHttpRequestFeature>()?.RawTarget
            ?? context.Request.Path + context.Request.QueryString;
        // 固定本次查询的启用状态和正则；同基址使用第一个启用账号的连接设置。
        var groups = routingAccounts.Select(account => (Account: account, Settings: account.Settings))
            .Where(item => item.Settings.IsEnabled).OrderBy(item => item.Settings.Order)
            .GroupBy(item => new Uri(item.Settings.UpstreamBaseUri.AbsoluteUri.TrimEnd('/')))
            .Select(group => group.ToArray()).ToArray();
        var results = await Task.WhenAll(groups.Select(async group =>
        {
            var first = group[0];
            var models = await QueryModelsAsync(context.Request, rawTarget, first.Account, first.Settings,
                timeout, httpClients.Get(first.Settings.Proxy), token).ConfigureAwait(false);
            return models.Where(model => group.Any(item => item.Settings.MatchesModel(model.GetProperty("id").GetString())));
        })).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var data = results.SelectMany(models => models)
            .Where(model => seen.Add(model.GetProperty("id").GetString()!)).ToArray();
        await context.Response.WriteAsJsonAsync(new { @object = "list", data }, token).ConfigureAwait(false);
    }

    private async Task<JsonElement[]> QueryModelsAsync(HttpRequest downstream, string rawTarget,
        AccountRuntime account, UpstreamSettings settings, TimeSpan timeout, HttpClient client, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(timeout);
        var clock = Stopwatch.StartNew();
        var health = account.Health;
        var record = new CaptureRecord
        {
            Method = downstream.Method, Path = rawTarget,
            UpstreamId = settings.Id, UpstreamName = $"{settings.Order} · {settings.DisplayName}"
        };
        record.SessionId = record.Id;
        health.Begin(record.Id);
        store.Publish(record);
        using var capture = new MemoryStream();
        string? contentType = null;
        string[] encodings = [];
        try
        {
            var target = BuildTarget(settings.UpstreamBaseUri, rawTarget);
            record.UpstreamUrl = target.AbsoluteUri;
            using var request = new HttpRequestMessage(HttpMethod.Get, target);
            var excluded = GetExcludedHeaders(downstream.Headers.Connection.ToString());
            // 聚合需要完整列表，不能复用某个上游的条件缓存或范围响应。
            excluded.UnionWith(["Host", "Authorization", "Content-Length", "Accept-Encoding", "If-Match", "If-None-Match",
                "If-Modified-Since", "If-Unmodified-Since", "Range", "If-Range"]);
            foreach (var header in downstream.Headers)
                if (!excluded.Contains(header.Key)) request.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray());
            // 模型列表由本地解码再聚合，只声明 DecodeStream 支持的压缩格式。
            request.Headers.AcceptEncoding.ParseAdd("gzip, deflate, br");
            if (settings.ApiKey.Length > 0) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.ApiKey);
            AddNetworkMessage(record, "下游 → 上游", "Request Headers", CaptureFormatting.HeadersToText(request.Headers));
            store.Publish(record);

            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
            record.StatusCode = (int)response.StatusCode;
            contentType = response.Content.Headers.ContentType?.ToString();
            encodings = response.Content.Headers.ContentEncoding.ToArray();
            AddNetworkMessage(record, "上游 → 下游", "Response Headers", $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}\n"
                + CaptureFormatting.HeadersToText(response.Headers.Concat(response.Content.Headers)));
            store.Publish(record);
            response.EnsureSuccessStatusCode();

            await using var source = await response.Content.ReadAsStreamAsync(deadline.Token).ConfigureAwait(false);
            byte[] first = new byte[1];
            int count = await source.ReadAsync(first, deadline.Token).ConfigureAwait(false);
            if (count > 0)
            {
                record.FirstByteMilliseconds = clock.Elapsed.TotalMilliseconds;
                health.FirstByte(record.Id, record.FirstByteMilliseconds.Value);
                capture.WriteByte(first[0]);
                store.Publish(record);
            }
            // 超时覆盖响应正文，已收到响应头或部分 JSON 也不能无限等待。
            await source.CopyToAsync(capture, deadline.Token).ConfigureAwait(false);
            capture.Position = 0;
            using var decoded = CaptureFormatting.DecodeStream(capture, encodings);
            using var document = await JsonDocument.ParseAsync(decoded, cancellationToken: deadline.Token).ConfigureAwait(false);
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
                throw new JsonException("上游模型列表缺少 data 数组。");
            var models = data.EnumerateArray()
                .Where(model => model.ValueKind == JsonValueKind.Object
                    && model.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String
                    && !string.IsNullOrWhiteSpace(id.GetString()))
                .Select(model => model.Clone()).ToArray();
            deadline.Token.ThrowIfCancellationRequested();
            record.RecordResponse(clock.Elapsed.TotalMilliseconds);
            health.FirstResponse(record.Id, record.FirstResponseMilliseconds!.Value);
            health.Response(record.Id, record.ResponseMilliseconds!.Value, isEventStream: false);
            return models;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            record.CloseReason = "下游连接已终止或服务已停止。";
            throw;
        }
        catch (OperationCanceledException)
        {
            record.Error = $"查询模型列表超时（{timeout.TotalSeconds:g} 秒），此上游按空列表处理。";
            return [];
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or JsonException or NotSupportedException)
        {
            record.Error = "查询模型列表失败，此上游按空列表处理：" + exception.Message;
            return [];
        }
        finally
        {
            record.DurationMilliseconds = clock.Elapsed.TotalMilliseconds;
            health.End(record.Id, record.IsFailure, record.Error);
            string body = CaptureFormatting.BodyToText(capture.ToArray(), contentType, encodings);
            if (body.Length > 0) AddNetworkMessage(record, "上游 → 下游", "Response Body", body);
            if (record.Error.Length > 0) AddNetworkMessage(record, "本地", "Model List Error", record.Error);
            if (record.IsClosed) AddNetworkMessage(record, "本地", "Connection Closed", record.CloseReason);
            record.IsCompleted = true;
            store.Publish(record);
        }
    }
}
