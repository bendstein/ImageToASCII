using Fooocus;
using Newtonsoft.Json.Linq;
using RandomImageGenerator;

Dictionary<string, string> argd = [];
System.Collections.IEnumerator arge = args.GetEnumerator();

while (arge.MoveNext())
{
    string? current = arge.Current.ToString();

    if (!string.IsNullOrWhiteSpace(current))
    {
        //First param is base url
        if (argd.Count == 0)
        {
            argd["--url"] = current;
            continue;
        }

        if (current.StartsWith("--"))
        {
            string? next = string.Empty;

            if (arge.MoveNext())
            {
                next = arge.Current.ToString();
            }

            if (!string.IsNullOrEmpty(next))
            {
                argd[current.ToLower()] = next;
            }
        }
    }
}

PromptGenerator prompt_generator = JObject.Parse(await File.ReadAllTextAsync("prompts.json"))
    .ToObject<PromptGenerator>()
    ?? throw new Exception("Failed to serialize prompts.json as prompt generator");

int n = (argd.TryGetValue("--n", out string? ns) && int.TryParse(ns, out int ni)) ? ni : 1;

using HttpClient http_client = new();
FooocusClient client = new(argd["--url"], http_client)
{
    ReadResponseAsString = true
};

//Ping the server
Console.WriteLine($"Pinging {argd["--url"]}.");

await client.PingAsync();

Console.WriteLine($"Successfully pinged {argd["--url"]}.");

//Get model and style
string default_model;
string[] default_styles;

AllModelNamesResponse all_models = await client.AllModelsAsync();
default_model = all_models.Model_filenames.FirstOrDefault() ?? string.Empty;

ICollection<string> all_styles = await client.StylesAsync();

default_styles = all_styles.Select(s => (s, Random.Shared.NextDouble()))
    .Where(s => s.Item2 > 0.95)
    .Select(s => s.s)
    .ToArray();

string out_dir = $"out/{DateTime.Now:MM-dd-yyyy-HH-mm}";
if (!Directory.Exists(out_dir))
{
    _ = Directory.CreateDirectory(out_dir);
}

//Generate n images
for (int i = 0; i < n; i++)
{
    (string prompt, string model, string[] styles) = prompt_generator.GeneratePrompt(Random.Shared);
    Console.WriteLine($"Prompt {i + 1}: {prompt}");

    IEnumerable<Response6> response = await client.TextToImageWithIpAsync(new Anonymous6(), "application/json", new()
    {
        Prompt = prompt,
        Style_selections = styles.Length == 0? default_styles : styles,
        Performance_selection = PerformanceSelection.Speed,
        Aspect_ratios_selection = "896*1152",
        Image_number = 1,
        Image_seed = -1,
        Guidance_scale = 4,
        Sharpness = 2,
        Base_model_name = string.IsNullOrWhiteSpace(model)? default_model : model,
        Refiner_model_name = "None",
        Refiner_switch = 0.5d,
        Loras = [],
    });

    int m = 0;

    foreach (Response6? item in response)
    {
        string? url = item.AdditionalProperties.TryGetValue("url", out object? u) ? u.ToString() : string.Empty;

        if (!string.IsNullOrWhiteSpace(url))
        {
            using HttpClient http = new();
            HttpResponseMessage resp = await http.SendAsync(new()
            {
                RequestUri = new Uri(url),
                Method = HttpMethod.Get
            });

            if (resp.IsSuccessStatusCode)
            {
                await using FileStream fs = new(Path.Combine(out_dir, $"image_{i}_{m}.png"), FileMode.CreateNew, FileAccess.ReadWrite);
                await using Stream body_stream = await resp.Content.ReadAsStreamAsync();
                await body_stream.CopyToAsync(fs);
            }
        }

        m++;
    }
}