using System;
using System.Collections.Generic;
using Skizzy.Client;
using Skizzy.Protocol;
using Skizzy.Protocol.Errors;

namespace Skizzy.Cli
{
    /// <summary>One subcommand: <c>skizzy &lt;name&gt; ...</c>.</summary>
    public sealed class CommandInfo
    {
        public string Name;
        public string Summary;
        public string Usage;
        /// <summary>Options that take no value (everything else starting with -- takes one).</summary>
        public string[] Flags = new string[0];
        public Func<CliArgs, int> Run;
    }

    public static partial class Commands
    {
        public static List<CommandInfo> All()
        {
            var list = new List<CommandInfo>
            {
                CallCommand.Info,
                McpCommand.Info,
                StatusCommand.Info,
                new CommandInfo
                {
                    Name = "version", Summary = "print the skizzy, protocol and host versions",
                    Usage = "skizzy version",
                    Run = a => { Console.Out.WriteLine("skizzy " + ProtocolInfo.HostVersion + " (protocol " + ProtocolInfo.Name + " " + ProtocolInfo.Version + ")"); return 0; },
                },
            };
            RegisterExtras(list);
            return list;
        }

        /// <summary>Further subcommands (watch, log, shot, doctor) register here from their own files.</summary>
        static partial void RegisterExtras(List<CommandInfo> list);

        public static CommandInfo Find(string name)
        {
            foreach (var c in All()) if (string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase)) return c;
            return null;
        }
    }

    public static class Program
    {
        public const int ExitOk = 0, ExitHostError = 1, ExitUsage = 2, ExitNotConnected = 3;

        public static int Main(string[] args)
        {
            if (args.Length == 0 || args[0] == "-h" || args[0] == "--help" || args[0] == "help")
            {
                if (args.Length > 1 && Commands.Find(args[1]) is CommandInfo h) { Console.Out.WriteLine(h.Usage); return ExitOk; }
                PrintUsage();
                return args.Length == 0 ? ExitUsage : ExitOk;
            }
            if (args[0] == "--version") args[0] = "version";
            var cmd = Commands.Find(args[0]);
            if (cmd == null)
            {
                Console.Error.WriteLine("skizzy: unknown command '" + args[0] + "'. Run `skizzy help`.");
                return ExitUsage;
            }
            CliArgs a;
            try { a = CliArgs.Parse(args, 1, cmd.Flags); }
            catch (UsageException e)
            {
                Console.Error.WriteLine("skizzy " + cmd.Name + ": " + e.Message);
                Console.Error.WriteLine("usage: " + cmd.Usage);
                return ExitUsage;
            }
            if (a.Flag("help")) { Console.Out.WriteLine(cmd.Usage); return ExitOk; }
            try { return cmd.Run(a); }
            catch (UsageException e)
            {
                Console.Error.WriteLine("skizzy " + cmd.Name + ": " + e.Message);
                Console.Error.WriteLine("usage: " + cmd.Usage);
                return ExitUsage;
            }
            catch (SkizzyClientException e)
            {
                Console.Error.WriteLine(Render.ClientError(e));
                return ExitNotConnected;
            }
            catch (SkizzyError e)
            {
                Console.Error.WriteLine(Render.Error(e));
                return ExitHostError;
            }
        }

        private static void PrintUsage()
        {
            Console.Out.WriteLine("skizzy " + ProtocolInfo.HostVersion + " - command line and MCP bridge for skizzy-protocol (KSP)");
            Console.Out.WriteLine();
            Console.Out.WriteLine("usage: skizzy <command> [options]");
            Console.Out.WriteLine();
            foreach (var c in Commands.All()) Console.Out.WriteLine("  " + c.Name.PadRight(10) + c.Summary);
            Console.Out.WriteLine();
            Console.Out.WriteLine("connection options (all commands): --ksp <KSP folder> | --endpoint <endpoint.json> [--token-file <file>]");
            Console.Out.WriteLine("  default: the PluginData next to this exe, then KSPDIR, then the per-user pointer the running game writes.");
            Console.Out.WriteLine("exit codes: 0 ok, 1 host error, 2 usage, 3 not connected");
        }
    }
}
