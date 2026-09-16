using System.CommandLine;
using VoipNet.Cli;

var root = new RootCommand("Voip.NET tools — SIP tester and RTP analyzer. Made by Gravicode Studios, led by Kang Fadhil.");
root.Subcommands.Add(VersionCommand.Create());
root.Subcommands.Add(SipCommands.Create());
root.Subcommands.Add(RtpCommands.Create());
return await root.Parse(args).InvokeAsync();
