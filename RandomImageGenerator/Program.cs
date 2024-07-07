using Fooocus;
using Newtonsoft.Json.Linq;
using RandomImageGenerator;

Dictionary<string, string> argd = [];
var arge = args.GetEnumerator();

while (arge.MoveNext())
{
    var current = arge.Current.ToString();

    if(!string.IsNullOrWhiteSpace(current))
    {
        //First param is base url
        if(argd.Count == 0)
        {
            argd["--url"] = current;
            continue;
        }

        if (current.StartsWith("--"))
        {
            var next = string.Empty;

            if (arge.MoveNext())
                next = arge.Current.ToString();

            if (!string.IsNullOrEmpty(next))
                argd[current.ToLower()] = next;
        }
    }
}

var prompt_generator = JObject.Parse(await File.ReadAllTextAsync("prompts.json"))
    .ToObject<PromptGenerator>()
    ?? throw new Exception("Failed to serialize prompts.json as prompt generator");

int n = (argd.TryGetValue("--n", out var ns) && int.TryParse(ns, out var ni)) ? ni : 1;

using var http_client = new HttpClient();
var client = new FooocusClient(argd["--url"], http_client)
{
    ReadResponseAsString = true
};

//Ping the server
Console.WriteLine($"Pinging {argd["--url"]}.");

await client.PingAsync();

Console.WriteLine($"Successfully pinged {argd["--url"]}.");

//Get model and style
string model = argd.TryGetValue("--model", out var mdl) ? mdl : string.Empty;
string[] styles = (argd.TryGetValue("--styles", out var sty) ? sty : string.Empty)
    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

if(string.IsNullOrWhiteSpace(model))
{
    var all_models = await client.AllModelsAsync();
    model = all_models.Model_filenames.FirstOrDefault()?? string.Empty;
}

if(styles.Length == 0)
{
    var all_styles = await client.StylesAsync();

    styles = all_styles.Select(s => (s, Random.Shared.NextDouble()))
        .Where(s => s.Item2 > 0.95)
        .Select(s => s.s)
        .ToArray();
}

string out_dir = $"out/{DateTime.Now:MM-dd-yyyy-HH-mm}";
if (!Directory.Exists(out_dir))
    Directory.CreateDirectory(out_dir);

//Generate n images
for (int i = 0; i < n; i++)
{
    var prompt = prompt_generator.GeneratePrompt(Random.Shared);
    Console.WriteLine($"Prompt {i + 1}: {prompt}");

    var response = await client.TextToImageWithIpAsync(new Anonymous6(), "application/json", new()
    {
        Prompt = prompt,
        Style_selections = styles,
        Performance_selection = PerformanceSelection.Speed,
        Aspect_ratios_selection = "896*1152",
        Image_number = 1,
        Image_seed = -1,
        Guidance_scale = 4,
        Sharpness = 2,
        Base_model_name = model,
        Refiner_model_name = "None",
        Refiner_switch = 0.5d,
        Loras = [],
    });

    int m = 0;

    foreach(var item in response)
    {
        var url = item.AdditionalProperties.TryGetValue("url", out var u) ? u.ToString() : string.Empty;

        if(!string.IsNullOrWhiteSpace(url))
        {
            using HttpClient http = new();
            var resp = await http.SendAsync(new()
            {
                RequestUri = new Uri(url),
                Method = HttpMethod.Get
            });

            if(resp.IsSuccessStatusCode)
            {
                await using var fs = new FileStream(Path.Combine(out_dir, $"image_{i}_{m}.png"), FileMode.CreateNew, FileAccess.ReadWrite);
                await using var body_stream = await resp.Content.ReadAsStreamAsync();
                await body_stream.CopyToAsync(fs);
            }
        }

        m++;
    }
}