namespace RandomImageGenerator;
public class PromptGenerator
{
    public Template[] Templates { get; set; } = [];

    public Dictionary<string, string[]> Args { get; set; } = [];

    public Style[] Styles { get; set; } = [];

    public (string prompt, string model, string[] style) GeneratePrompt(Random random)
    {
        if (Templates.Length == 0)
        {
            return (string.Empty, string.Empty, []);
        }

        Template template = Templates[random.Next(Templates.Length)];

        string[] arguments = template.Args.Select(arg_type =>
        {
            if (Args.TryGetValue(arg_type, out string[]? arg_values))
            {
                if (arg_values.Length > 0)
                {
                    return arg_values[random.Next(arg_values.Length)];
                }
            }

            return string.Empty;
        }).ToArray();

        Style style = Styles[random.Next(Styles.Length)];

        return (string.Format(template.Content, arguments), style.Model, style.Styles);
    }
}

public class Template
{
    public string Content { get; set; } = string.Empty;

    public string[] Args { get; set; } = [];
}

public class Style
{
    public string Model { get; set; } = string.Empty;

    public string[] Styles { get; set; } = [];
}