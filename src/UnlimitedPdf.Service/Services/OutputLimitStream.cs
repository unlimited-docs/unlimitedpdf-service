namespace UnlimitedPdf.Service.Services;

internal sealed class OutputLimitException : IOException;

internal sealed class LimitedMemoryStream(long limit) : MemoryStream
{
    private void Check(long count)
    {
        if (count > limit - Position) throw new OutputLimitException();
    }

    public override void Write(byte[] buffer, int offset, int count) { Check(count); base.Write(buffer, offset, count); }
    public override void Write(ReadOnlySpan<byte> buffer) { Check(buffer.Length); base.Write(buffer); }
    public override void WriteByte(byte value) { Check(1); base.WriteByte(value); }
    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    { Check(count); return base.WriteAsync(buffer, offset, count, cancellationToken); }
    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    { Check(buffer.Length); return base.WriteAsync(buffer, cancellationToken); }
    public override void SetLength(long value)
    {
        if (value > limit) throw new OutputLimitException();
        base.SetLength(value);
    }
}

internal sealed class OutputBudget(long limit)
{
    public void Add(int bytes)
    {
        if (bytes > limit) throw new OutputLimitException();
        limit -= bytes;
    }
}

internal sealed class BudgetStream(Stream inner, OutputBudget budget) : Stream
{
    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => inner.CanWrite;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() => inner.Flush();
    public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);
    public override void Write(byte[] buffer, int offset, int count) { budget.Add(count); inner.Write(buffer, offset, count); }
    public override void Write(ReadOnlySpan<byte> buffer) { budget.Add(buffer.Length); inner.Write(buffer); }
    public override void WriteByte(byte value) { budget.Add(1); inner.WriteByte(value); }
    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    { budget.Add(count); return inner.WriteAsync(buffer, offset, count, cancellationToken); }
    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    { budget.Add(buffer.Length); return inner.WriteAsync(buffer, cancellationToken); }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }
    public override async ValueTask DisposeAsync() { await inner.DisposeAsync(); GC.SuppressFinalize(this); }
}
