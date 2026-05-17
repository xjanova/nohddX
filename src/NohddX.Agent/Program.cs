using NohddX.Agent;
using Spectre.Console;

try
{
    AnsiConsole.Write(new FigletText("NoHddX Agent").Color(Color.Cyan1));
    AnsiConsole.MarkupLine("[grey]Version 1.0.0  -  Diskless Bootstrap Agent[/]");
    AnsiConsole.WriteLine();

    var app = new AgentApp();
    return await app.RunAsync(args);
}
catch (Exception ex)
{
    AnsiConsole.WriteException(ex);
    AnsiConsole.MarkupLine("[red]Press any key to exit...[/]");
    try { Console.ReadKey(); } catch { }
    return 1;
}
