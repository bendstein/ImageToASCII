namespace LibI2A.Common;

public class UnionStream(IEnumerable<Stream> inner) : Stream
{
    private readonly IEnumerable<Stream> inner = inner;

    public override bool CanRead => !inner.Any() || inner.All(s => s.CanRead);

    public override bool CanSeek => !inner.Any() || inner.All(s => s.CanSeek);

    public override bool CanWrite => !inner.Any() || inner.All(s => s.CanWrite);

    public override long Length => inner.Any() ? inner.Select(i => i.Length).Min() : 0;

    public override long Position
    {
        get => inner.FirstOrDefault()?.Position ?? 0;
        set
        {
            foreach(var stream in inner)
                stream.Position = value;
        }
    }

    public UnionStream(params Stream[] inner) : this((IEnumerable<Stream>)inner) { }

    public override void Flush()
    {
       foreach(var stream in inner)
            stream.Flush();
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        int i = 0;

        foreach(var stream in inner)
        {
            if(stream.CanRead)
                i = stream.Read(buffer, offset, count);
        }

        return i;
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        long i = 0;

        foreach(var stream in inner)
        {
            if(stream.CanSeek)
                i = stream.Seek(offset, origin);
        }

        return i;
    }

    public override void SetLength(long value)
    {
        foreach(var stream in inner)
            stream.SetLength(value);
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        foreach(var stream in inner)
        {
            if (stream.CanWrite)
                stream.Write(buffer, offset, count);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if(disposing)
        {
            foreach (var stream in inner)
                stream.Dispose();
        }

        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        await Task.WhenAll(inner.Select(async i => await i.DisposeAsync()));
        await base.DisposeAsync();
        GC.SuppressFinalize(this);
    }
}
