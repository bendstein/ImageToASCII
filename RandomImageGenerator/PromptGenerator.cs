namespace RandomImageGenerator;
public class PromptGenerator
{
    public Template[] Templates { get; set; } = [];

    public Dictionary<string, string[]> Args { get; set; } = [];

    public string GeneratePrompt(Random random)
    {
        if (Templates.Length == 0)
        {
            return string.Empty;
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

        return string.Format(template.Content, arguments);
    }
}

public class Template
{
    public string Content { get; set; } = string.Empty;

    public string[] Args { get; set; } = [];
}