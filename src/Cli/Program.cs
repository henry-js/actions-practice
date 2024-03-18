
using Community.Extensions.Spectre.Cli.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using actions_practice.Cli.Commands;
using actions_practice.Lib.Services;
using Spectre.Console.Cli;

var builder = Host.CreateApplicationBuilder(args);

// Only use configuration in appsettings.json
builder.Configuration.Sources.Clear();
builder.Configuration.AddJsonFile("appsettings.json", false);

//Disable logging
builder.Logging.ClearProviders();

// Bind configuration section to object
builder.Services.AddOptions<NestedSettings>()
    .Bind(builder.Configuration.GetSection(NestedSettings.Key));

// Add a command and optionally configure it.
builder.Services.AddCommand<HelloCommand>("hello", cmd =>
{
    cmd.WithDescription("A command that says hello");
});


// Add another command and its dependent service

builder.Services.AddCommand<OtherCommand>("other");
builder.Services.AddScoped<ISampleService, SampleService>(s => new SampleService("Other Service"));

//
// The standard call save for the commands will be pre-added & configured
//
builder.UseSpectreConsole<HelloCommand>(config =>
{
    // All commands above are passed to config.AddCommand() by this point

    config.SetApplicationName("hello");
    config.UseBasicExceptionHandler();
});

var app = builder.Build();
await app.RunAsync();

Console.ReadLine();