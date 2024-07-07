using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace RandomImageGenerator;
public class PromptGenerator
{
    public Template[] Templates { get; set; } = [];

    public Dictionary<string, string[]> Args { get; set; } = [];

    public string GeneratePrompt(Random random)
    {
        if (Templates.Length == 0)
            return string.Empty;

        var template = Templates[random.Next(Templates.Length)];

        var arguments = template.Args.Select(arg_type =>
        {
            if (Args.TryGetValue(arg_type, out var arg_values))
            {
                if (arg_values.Length > 0)
                    return arg_values[random.Next(arg_values.Length)];
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