using Microsoft.Data.Sqlite;
using Spectre.Console;
using System.Data.Common;
using System.Linq;
using System.Text;

const string DB_EXT = ".db";
const string EXIT = ":exit";
const string CLEAR = ":clear";

string path;

if (args.Length > 0)
    path = args[0];
else
{
    Console.Write($"Enter the path the the sqlite database file: ");
    path = Console.ReadLine()?? string.Empty;
}

try
{
    _ = new FileInfo(path);
}
catch(Exception e)
{
    throw new Exception($"Invalid file path '{path}': {e.Message}");
}

if ((Path.GetExtension(path) ?? string.Empty).ToLowerInvariant() != DB_EXT)
    throw new Exception($"File must have the extension '{DB_EXT}'.");

using var connection = new SqliteConnection($"Data Source={path}");
connection.Open();

Console.WriteLine();

while(true)
{
    Console.WriteLine($"Enter the sql statement to run, {CLEAR} to clear the screen, or {EXIT} to exit.");

    StringBuilder stmtbuilder = new();
    string? line;
    
    while((line = Console.ReadLine()) != null && line != string.Empty)
    {
        if(stmtbuilder.Length == 0)
        {
            if(line == EXIT)
            {
                Environment.Exit(0);
            }
            else if(line == CLEAR)
            {
                Console.Clear();
                break;
            }
        }

        stmtbuilder.AppendLine(line);

        if (line.Length > 0 && line.Last() == ';')
            break;
    }

    var stmt = stmtbuilder.ToString();

    if (string.IsNullOrWhiteSpace(stmt))
        continue;

    try
    {
        using var comm = connection.CreateCommand();
        comm.CommandText = stmt;

        using var reader = comm.ExecuteReader();

        var names = new string[reader.FieldCount];

        for (int i = 0; i < reader.FieldCount; i++)
        {
            names[i] = reader.GetName(i);
        }

        List<Dictionary<string, object>> rows = new();

        while (reader.Read())
        {
            Dictionary<string, object> data = new();

            for (int i = 0; i < names.Length; i++)
            {
                string name = names[i];
                object value = reader.GetValue(i);
                data[name] = value;
            }

            rows.Add(data);
        }

        Table table = new();
        table.AddColumns(names);

        foreach (var row in rows)
        {
            table.AddRow(names.Select(n => new Text(row[n]?.ToString() ?? "<null>")));
        }

        AnsiConsole.Write(table);
    }
    catch (Exception e)
    {
        Console.Error.WriteLine(e.ToString());
        continue;
    }
}