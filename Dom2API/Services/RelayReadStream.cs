using System.IO;

namespace Dom2API.Services;

/// <summary>
/// 在读取上游内容时捕获原始字节，支持先缓存、胜出后再透传。
/// </summary>
internal sealed class RelayReadStream : Stream
{
    private readonly Stream source;
    private readonly Stream destination;
    private readonly Stream capture;
    private readonly Action firstByte;
    private bool received;
    private bool forwarding;

    /// <summary>
    /// 创建用于原始响应透传的读取流。
    /// </summary>
    public RelayReadStream(Stream source, Stream destination, Stream capture, Action firstByte, bool deferForwarding = false)
    {
        this.source = source;
        this.destination = destination;
        this.capture = capture;
        this.firstByte = firstByte;
        forwarding = !deferForwarding;
        if (deferForwarding && !capture.CanSeek) throw new ArgumentException("延迟转发需要可定位的捕获流。", nameof(capture));
    }

    /// <summary>胜出后原样补发已捕获字节，此后读取直接转发；由同一个读取循环串行调用。</summary>
    public async Task StartForwardingAsync(CancellationToken token)
    {
        if (forwarding) return;
        long position = capture.Position;
        try
        {
            capture.Position = 0;
            await capture.CopyToAsync(destination, token).ConfigureAwait(false);
            await destination.FlushAsync(token).ConfigureAwait(false);
            forwarding = true;
        }
        catch (Exception exception) when (exception is IOException or OperationCanceledException)
        {
            throw new DownstreamWriteException(exception);
        }
        finally { capture.Position = position; }
    }

    /// <summary>获取流是否支持读取。</summary>
    public override bool CanRead => true;

    /// <summary>获取流是否支持定位。</summary>
    public override bool CanSeek => false;

    /// <summary>获取流是否支持写入。</summary>
    public override bool CanWrite => false;

    /// <summary>获取长度；该流不支持此操作。</summary>
    public override long Length => throw new NotSupportedException();

    /// <summary>获取或设置位置；该流不支持此操作。</summary>
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    /// <summary>
    /// 读取并捕获上游字节；启用转发后立即透传，不改变编码或 SSE 分帧。
    /// </summary>
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        int count = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        if (count > 0)
        {
            if (!received)
            {
                received = true;
                firstByte();
            }

            await capture.WriteAsync(buffer[..count], CancellationToken.None).ConfigureAwait(false);
            if (!forwarding) return count;
            try
            {
                await destination.WriteAsync(buffer[..count], cancellationToken).ConfigureAwait(false);
                await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or OperationCanceledException)
            {
                throw new DownstreamWriteException(exception);
            }
        }

        return count;
    }

    /// <summary>通过旧式缓冲区接口异步读取内容。</summary>
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    /// <summary>拒绝同步读取，防止阻塞服务线程。</summary>
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    /// <summary>拒绝刷新只读接口。</summary>
    public override void Flush() => throw new NotSupportedException();

    /// <summary>拒绝对网络流执行定位。</summary>
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    /// <summary>拒绝修改网络流长度。</summary>
    public override void SetLength(long value) => throw new NotSupportedException();

    /// <summary>拒绝向只读接口写入。</summary>
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
