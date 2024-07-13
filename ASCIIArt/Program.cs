MathNet.Numerics.Control.UseMultiThreading();
MathNet.Numerics.Control.UseBestProviders();

new Spectre.Console.Cli
    .CommandApp<I2A.App>()
    .Run(args);
