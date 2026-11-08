using System;
using System.Globalization;
using System.IO;
using System.Threading;
using Skizzy.Host.Core;
using Skizzy.Protocol.Json;
using Skizzy.Sim;

namespace Skizzy.FakeHost
{
    /// <summary>
    /// skizzy-fakehost [--dir PATH] [--port N | --port 0] [--vessels N] [--auto-answer allow|deny] [--stdin-exit] [--verbose]
    /// Prints one JSON line {url, port, root, pluginData, endpointFile, tokenFile} on stdout when ready, then runs until
    /// Ctrl+C (or stdin EOF with --stdin-exit). The token is never printed; it is in tokenFile.
    /// </summary>
    public static class Program
    {
        private sealed class ConsoleLog : IHostLog
        {
            public bool Verbose;
            public void Info(string msg) { if (Verbose) Console.Error.WriteLine("[fakehost] " + msg); }
            public void Warn(string msg) { Console.Error.WriteLine("[fakehost] WARN " + msg); }
            public void Error(string msg) { Console.Error.WriteLine("[fakehost] ERROR " + msg); }
        }

        public static int Main(string[] args)
        {
            string dir = null;
            int port = -1, vessels = 6;
            string autoAnswer = null;
            bool stdinExit = false;
            var log = new ConsoleLog();
            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i];
                string Next() { if (i + 1 >= args.Length) throw new ArgumentException(a + " needs a value"); return args[++i]; }
                try
                {
                    switch (a)
                    {
                        case "--dir": case "--ksp-root": dir = Next(); break;
                        case "--port": port = int.Parse(Next(), CultureInfo.InvariantCulture); break;
                        case "--vessels": vessels = int.Parse(Next(), CultureInfo.InvariantCulture); break;
                        case "--auto-answer": autoAnswer = Next(); break;
                        case "--stdin-exit": stdinExit = true; break;
                        case "--verbose": log.Verbose = true; break;
                        case "-h": case "--help":
                            Console.WriteLine("skizzy-fakehost [--dir PATH] [--port N|0] [--vessels N] [--auto-answer allow|deny] [--stdin-exit] [--verbose]");
                            return 0;
                        default: Console.Error.WriteLine("unknown option " + a); return 2;
                    }
                }
                catch (Exception e) when (e is FormatException || e is ArgumentException)
                {
                    Console.Error.WriteLine(e.Message);
                    return 2;
                }
            }
            dir = Path.GetFullPath(dir ?? Path.Combine(Path.GetTempPath(), "skizzy-fakehost"));
            Directory.CreateDirectory(dir);

            var world = FakeWorld.CreateDefault();
            if (vessels > 2) world.AddFleet(vessels - 2);
            var settings = new HostSettings();
            if (port >= 0) { settings.PortFirst = port; settings.PortLast = port; }
            var sim = new SimHost(dir, world, settings, log);
            if (autoAnswer == "allow") sim.Host.Prompts.AutoAnswer = PromptAnswer.AllowOnce;
            else if (autoAnswer == "deny") sim.Host.Prompts.AutoAnswer = PromptAnswer.Deny;
            try { sim.Start(); }
            catch (InvalidOperationException e)
            {
                Console.Error.WriteLine("fakehost could not start: " + e.Message);
                return 1;
            }

            var ready = JsonValue.Obj().Set("url", sim.Url).Set("port", sim.Host.Port).Set("root", dir)
                .Set("pluginData", sim.Host.Paths.PluginData).Set("endpointFile", sim.Host.Paths.EndpointFile).Set("tokenFile", sim.Host.Paths.TokenFile);
            Console.Out.WriteLine(ready.ToString());
            Console.Out.Flush();

            var quit = new ManualResetEventSlim();
            Console.CancelKeyPress += (s, e) => { e.Cancel = true; quit.Set(); };
            if (stdinExit)
            {
                var t = new Thread(() => { try { while (Console.In.ReadLine() != null) { } } catch (IOException) { } quit.Set(); }) { IsBackground = true };
                t.Start();
            }
            quit.Wait();
            sim.Dispose();
            return 0;
        }
    }
}
