using IRCBot.Host;

// Usage: IRCBotHost [controlPort] [controlPassword]
//   controlPort     default 6690 (loopback-only control port for the front end)
//   controlPassword optional; if omitted the control port requires no auth
int controlPort = args.Length > 0 && int.TryParse(args[0], out var p) ? p : 6690;
string? password = args.Length > 1 ? args[1] : null;

var host = new BotHost();
var control = new ControlInterface(host, controlPort, password);

Console.WriteLine("IRCBot host started.");
AppDomain.CurrentDomain.ProcessExit += (_, _) => host.StopAll();
await control.RunAsync();
