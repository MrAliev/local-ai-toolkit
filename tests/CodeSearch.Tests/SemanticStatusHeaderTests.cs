using System.Text;
using CodeSearch.Core.Semantics;

namespace CodeSearch.Tests;

public sealed class SemanticStatusHeaderTests
{
    [Fact]
    public void Document_count_reads_a_fixed_prefix_without_scanning_or_closing_the_source()
    {
        using var header = Header();
        using var source = new BudgetStream(header.ToArray());

        Assert.Equal(1, SemanticIndex.TryReadDocumentCount(source));
        Assert.Equal(4096, source.BytesRead);
        Assert.False(source.WasClosed);
    }

    [Theory]
    [InlineData(1025)]
    [InlineData(int.MaxValue)]
    public void Oversized_string_prefixes_are_unknown_without_following_the_declared_length(int length)
    {
        using var header = new MemoryStream();
        using (var writer = new BinaryWriter(header, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(Encoding.ASCII.GetBytes("SIDX"));
            writer.Write(SemanticIndex.CurrentVersion);
            writer.Write7BitEncodedInt(length);
        }
        using var source = new BudgetStream(header.ToArray());

        Assert.Null(SemanticIndex.TryReadDocumentCount(source));
        Assert.Equal(4096, source.BytesRead);
    }

    private static MemoryStream Header()
    {
        var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        writer.Write(Encoding.ASCII.GetBytes("SIDX"));
        writer.Write(SemanticIndex.CurrentVersion);
        writer.Write("repository");
        writer.Write("generation");
        writer.Write("tree");
        writer.Write(false);
        writer.Write("commit");
        writer.Write(DateTime.UnixEpoch.Ticks);
        writer.Write(1);
        return stream;
    }

    // No EOF is supplied: an implementation that scans beyond its prefix fails deterministically.
    private sealed class BudgetStream(byte[] prefix) : Stream
    {
        public int BytesRead { get; private set; }
        public bool WasClosed { get; private set; }
        public override int Read(Span<byte> buffer)
        {
            if (BytesRead >= 4096) throw new InvalidOperationException("Status exceeded its read budget.");
            var count = Math.Min(buffer.Length, 4096 - BytesRead);
            buffer[..count].Clear();
            if (BytesRead < prefix.Length)
                prefix.AsSpan(BytesRead, Math.Min(count, prefix.Length - BytesRead)).CopyTo(buffer);
            BytesRead += count;
            return count;
        }
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { WasClosed = true; base.Dispose(disposing); }
    }
}
