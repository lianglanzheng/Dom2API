using System.IO;

namespace Dom2API.Services;

/// <summary>将已探测的首字节放回读取流，保持压缩和 SSE 解析输入完整。</summary>
internal sealed class PrefixReadStream(Stream source, ReadOnlyMemory<byte> prefix) : Stream
{
    private ReadOnlyMemory<byte> remaining = prefix;
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (!remaining.IsEmpty)
        {
            int count = Math.Min(buffer.Length, remaining.Length);
            remaining[..count].CopyTo(buffer);
            remaining = remaining[count..];
            return count;
        }
        return await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
    }
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override void Flush() => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

/// <summary>明确标记下游写入错误，防止将下游关闭计为上游故障。</summary>
internal sealed class DownstreamWriteException(Exception inner) : IOException("下游连接写入失败。", inner);
