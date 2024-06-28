namespace LibI2A;

public interface IGlyphWriter
{
    public bool SeekEnabled { get; }

    public bool SeekLinearEnabled { get; }

    public void Write(string s, uint? color = null);

    public void WriteLine(string s, uint? color = null);

    public void WriteLine() => WriteLine(string.Empty);

    public void Flush();

    public void Seek(int i, int j);

    public void SeekLinear(long offset);
}