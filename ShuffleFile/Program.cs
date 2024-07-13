const long LIMIT = 100_000;

var path = args[0];
var output = args[1];

IEnumerator<string> files = (Directory.Exists(path)
    ? Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories)
    : File.Exists(path)
    ? [path]
    : []).GetEnumerator();

DirectoryInfo dir = Directory.CreateTempSubdirectory();

try
{
    for(var i = 0; files.MoveNext();)
    {
        var file = files.Current;

        using var fs = new FileStream(file, FileMode.Open, FileAccess.Read);
        using var sr = new StreamReader(fs);

        while(!sr.EndOfStream)
        {
            i++;

            Console.WriteLine($"File {i}.");

            List<string> lines = [];

            //Read next LIMIT lines
            while(lines.Count < LIMIT)
            {
                var line = sr.ReadLine();
                if(!string.IsNullOrEmpty(line))
                {
                    lines.Add(line);
                }

                if(sr.EndOfStream)
                {
                    break;
                }
            }

            //Shuffle lines and write to file
            using var os = new FileStream(Path.Combine(dir.FullName, $"{i}.txt"), FileMode.OpenOrCreate, FileAccess.Write);
            using var sw = new StreamWriter(os);

            foreach(var line in lines.Select(l => (l, Random.Shared.Next()))
                .OrderBy(l => l.Item2)
                .Select(l => l.l))
            {
                sw.WriteLine(line);
            }
        }
    }

    Console.WriteLine("Merging.");

    using var output_stream = new FileStream(output, FileMode.OpenOrCreate, FileAccess.Write);
    using var output_writer = new StreamWriter(output_stream);

    List<(FileStream fs, StreamReader sr)> streams = [];
    List<StreamReader> eligible = [];
    try
    {
        //Open stream for each file
        streams = Directory.EnumerateFiles(dir.FullName)
            .Select(file =>
            {
                var fs = new FileStream(file, FileMode.Open, FileAccess.Read);
                var sr = new StreamReader(fs);
                return (fs, sr);
            })
            .ToList();

        eligible = streams.Select(s => s.sr)
            .Where(sr => !sr.EndOfStream)
            .ToList();

        while(eligible.Count > 0)
        {
            //Get random stream
            StreamReader next = eligible[Random.Shared.Next(eligible.Count)];

            //Read line from stream
            var line = next.ReadLine();

            //Write line to output
            output_writer.WriteLine(line);

            eligible = streams.Select(s => s.sr)
                .Where(sr => !sr.EndOfStream)
                .ToList();
        }
    }
    finally
    {
        foreach((FileStream? fs, StreamReader? sr) in streams)
        {
            fs.Dispose();
            sr.Dispose();
        }
    }

    Console.WriteLine("Done.");
}
finally
{
    dir.Delete(true);
}