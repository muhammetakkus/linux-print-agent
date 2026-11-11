using PrintAgent.CLI;
using PrintAgent.Common.Local;

if (!OperatingSystem.IsLinux()) { Console.Error.WriteLine("NuxPrint CLI supports Linux only."); return 2; }
return await CliApplication.RunAsync(args, new LocalAgentClient(), Console.In, Console.Out, Console.Error);
