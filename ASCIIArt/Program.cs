//Set Math.NET settings
MathNet.Numerics.Control.UseMultiThreading();
MathNet.Numerics.Control.UseBestProviders();

//Set base directory as cwd
Directory.SetCurrentDirectory(AppDomain.CurrentDomain.BaseDirectory);

//Run the app
new Spectre.Console.Cli
    .CommandApp<I2A.App>()
    .Run(args);