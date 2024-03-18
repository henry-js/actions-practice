using System.Text.Json;
using Microsoft.Extensions.Options;
using actions_practice.Lib.Services;
using Spectre.Console;
using Spectre.Console.Cli;

namespace actions_practice.Cli.Commands;

public class HelloCommand(IAnsiConsole ansiConsole, IOptions<NestedSettings> nestedSetttings) : AsyncCommand<HelloCommandSettings>
{
    private readonly IAnsiConsole _console = ansiConsole;
    private readonly NestedSettings _nestedSettings = nestedSetttings.Value;

    public override async Task<int> ExecuteAsync(CommandContext context, HelloCommandSettings settings)
    {
        _console.MarkupLine("[green]Hello[/] [magenta]there![/]");

        _console.MarkupLine($"In appsettings.json value of NestedSettings:");
        _console.WriteLine(JsonSerializer.Serialize(_nestedSettings, new JsonSerializerOptions { WriteIndented = true }));

        return await Task.FromResult(0);
    }
}

public class HelloCommandSettings : CommandSettings
{
}

public class OtherCommand(IAnsiConsole ansiConsole, ISampleService service) : AsyncCommand
{
    private readonly IAnsiConsole _console = ansiConsole;
    private readonly ISampleService _service = service;

    public override async Task<int> ExecuteAsync(CommandContext context)
    {
        _console.MarkupLine("[red]I am the other command[/] [blue]that has no options or flags.[/]");
        _console.MarkupLine("[yellow]But I do use the SampleService[/]");

        _service.DoWork();
        return await Task.FromResult(0);
    }
}