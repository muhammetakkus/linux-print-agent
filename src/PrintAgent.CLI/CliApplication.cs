using System.Text;
using System.Text.Json;
using PrintAgent.Common.Local;

namespace PrintAgent.CLI;

public static class CliApplication
{
    public static async Task<int> RunAsync(string[] args, ILocalAgentClient client, TextReader input, TextWriter output, TextWriter error)
    {
        if (args.Length == 0 || args[0] is "help" or "--help" or "-h")
        {
            await output.WriteLineAsync("nuxprint-agent status|printers|connection|history|diagnostics|login EMAIL|logout\nlogin: password is read interactively; use --password-stdin with redirected input. No password command-line argument is accepted.");
            return 0;
        }
        var command = args[0];
        if (command is not ("status" or "printers" or "connection" or "history" or "diagnostics" or "login" or "logout"))
        { await error.WriteLineAsync("Unknown command. Use --help."); return 2; }
        var request = new LocalRequest { Method = command };
        if (command == "login")
        {
            if (args.Length is < 2 or > 3 || (args.Length == 3 && args[2] != "--password-stdin"))
            { await error.WriteLineAsync("Usage: nuxprint-agent login EMAIL [--password-stdin]"); return 2; }
            request.Email = args[1];
            if (args.Length == 3) request.Password = await input.ReadLineAsync();
            else
            {
                if (Console.IsInputRedirected) { await error.WriteLineAsync("Use --password-stdin for redirected input."); return 2; }
                await error.WriteAsync("Password: ");
                var password = new StringBuilder();
                while (true)
                {
                    var key = Console.ReadKey(intercept: true);
                    if (key.Key == ConsoleKey.Enter) break;
                    if (key.Key == ConsoleKey.Backspace) { if (password.Length > 0) password.Length--; }
                    else if (!char.IsControl(key.KeyChar)) password.Append(key.KeyChar);
                }
                await error.WriteLineAsync();
                request.Password = password.ToString();
            }
        }
        else if (args.Length != 1) { await error.WriteLineAsync("This command takes no arguments."); return 2; }
        try
        {
            var response = await client.SendAsync(request);
            request.Password = null;
            if (!response.Success) { await error.WriteLineAsync(response.Error); return 1; }
            var state = response.State!;
            object value = command switch
            {
                "printers" => state.Printers,
                "history" => state.Jobs,
                "connection" => new { state.IsAuthenticated, state.IsConnected },
                "diagnostics" => new { state.Running, state.DeviceId, state.DeviceName, state.Version, state.DefaultPrinter, PrinterCount = state.Printers.Count, Logs = state.Logs },
                _ => new { state.Running, state.IsAuthenticated, state.IsConnected, state.DeviceId, state.DeviceName, state.Version }
            };
            await output.WriteLineAsync(JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }
        catch (Exception ex) when (ex is IOException or System.Net.Sockets.SocketException or OperationCanceledException)
        { await error.WriteLineAsync($"Agent unavailable or local access denied: {ex.Message}"); return 2; }
        finally { request.Password = null; }
    }
}
