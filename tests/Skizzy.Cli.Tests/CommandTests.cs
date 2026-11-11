using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Skizzy.Cli;
using Skizzy.Protocol.Json;
using Xunit;

namespace Skizzy.Cli.Tests
{
    /// <summary>These tests redirect Console.Out/Error, so they must not run in parallel with anything else.</summary>
    [CollectionDefinition("console", DisableParallelization = true)]
    public class ConsoleCollection { }

    /// <summary>watch, log, shot and doctor against a FakeKsp host (a FakeGame root), run through <c>Program.Main</c>.</summary>
    [Collection("console")]
    public class CommandTests
    {
        private sealed class Result
        {
            public int Code;
            public string Out, Err;
        }

        private static Result Run(params string[] args)
        {
            var so = Console.Out;
            var se = Console.Error;
            var ow = new StringWriter();
            var ew = new StringWriter();
            Console.SetOut(TextWriter.Synchronized(ow));
            Console.SetError(TextWriter.Synchronized(ew));
            try
            {
                int code = Program.Main(args);
                return new Result { Code = code, Out = ow.ToString(), Err = ew.ToString() };
            }
            finally
            {
                Console.SetOut(so);
                Console.SetError(se);
            }
        }

        private static string[] Ksp(FakeGame g, params string[] args)
        {
            return args.Concat(new[] { "--ksp", g.Root }).ToArray();
        }

        private static string TempFile(string ext)
        {
            return Path.Combine(Path.GetTempPath(), "skizzy-cmd-" + Guid.NewGuid().ToString("N").Substring(0, 8) + ext);
        }

        // ------------------------------------------------------------------ watch

        [Fact]
        public void Watch_prints_the_snapshot_and_samples_as_aligned_rows_and_stops_after_count()
        {
            using (var game = new FakeGame())
            {
                var r = Run(Ksp(game, "watch", "altitude", "srfSpeed", "--rate", "10", "--count", "3"));
                Assert.Equal(0, r.Code);
                var lines = r.Out.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Length > 0).ToList();
                Assert.StartsWith("ut", lines[0]);
                Assert.Contains("vessel", lines[0]);
                Assert.Contains("altitude", lines[0]);
                Assert.Contains("srfSpeed", lines[0]);
                Assert.Contains(lines, l => l.Contains("DDG-1 Kirov"));
                int perMessage = lines.Count(l => l.Contains("DDG-1 Kirov"));
                Assert.Equal(3, perMessage);   // snapshot + 2 samples
                // aligned: every line has the altitude column starting at the same offset as its header
                int col = lines[0].IndexOf("altitude", StringComparison.Ordinal);
                foreach (var l in lines.Skip(1)) Assert.True(l.Length > col && l[col - 1] == ' ', l);
            }
        }

        [Fact]
        public void Watch_json_prints_one_object_per_message_and_honours_the_vessel_selector()
        {
            using (var game = new FakeGame())
            {
                var r = Run(Ksp(game, "watch", "srfSpeed", "--vessels", "DDG-1*", "--rate", "10", "--count", "2", "--json"));
                Assert.Equal(0, r.Code);
                var lines = r.Out.Split('\n').Where(l => l.Trim().Length > 0).ToList();
                Assert.Equal(2, lines.Count);
                foreach (var l in lines)
                {
                    var o = JsonValue.Parse(l);
                    var rows = o.GetArr("rows");
                    Assert.NotNull(rows);
                    Assert.Equal(1, rows.Count);
                    Assert.Equal("DDG-1 Kirov", rows[0].GetString("vesselName"));
                    Assert.True(rows[0].Has("srfSpeed"));
                }
                Assert.True(JsonValue.Parse(lines[1]).GetLong("seq") > JsonValue.Parse(lines[0]).GetLong("seq"));
            }
        }

        [Fact]
        public void Watch_duration_stops_and_unsubscribes()
        {
            using (var game = new FakeGame())
            {
                var r = Run(Ksp(game, "watch", "altitude", "--rate", "5", "--duration", "1"));
                Assert.Equal(0, r.Code);
                Assert.Contains("DDG-1 Kirov", r.Out);
                // a clean exit leaves no stream behind
                Assert.True(SpinWait.SpinUntil(() => game.Sim.Host.Streams.Count == 0, 3000));
            }
        }

        [Fact]
        public void Watch_events_prints_compact_lines()
        {
            using (var game = new FakeGame())
            {
                var task = Task.Run(() => Run(Ksp(game, "watch", "events", "test.*", "--count", "1", "--duration", "20")));
                var end = DateTime.UtcNow.AddSeconds(15);
                while (!task.IsCompleted && DateTime.UtcNow < end)
                {
                    game.Sim.Host.Events.Emit("test.hello", "system", "hello from the test");
                    Thread.Sleep(100);
                }
                Assert.True(task.IsCompleted);
                var r = task.Result;
                Assert.Equal(0, r.Code);
                Assert.Matches(@"^#\d+ ut=[0-9.]+ test\.hello hello from the test", r.Out);
            }
        }

        [Fact]
        public void Watch_errors_are_usage_host_and_not_connected()
        {
            Assert.Equal(2, Run("watch").Code);
            Assert.Equal(2, Run("watch", "altitude", "--rate", "0").Code);
            using (var game = new FakeGame())
            {
                var bad = Run(Ksp(game, "watch", "noSuchFieldAnywhere", "--count", "1"));
                Assert.Equal(1, bad.Code);
                Assert.Matches(@"^[A-Z_]+: ", bad.Err);
            }
            string nowhere = Path.Combine(Path.GetTempPath(), "skizzy-nogame-" + Guid.NewGuid().ToString("N"));
            var off = Run("watch", "altitude", "--count", "1", "--ksp", nowhere);
            Assert.Equal(3, off.Code);
            Assert.StartsWith("UNAVAILABLE: game_not_running", off.Err);
        }

        // PC test 2026-11-11, M0 checklist 10.4: watch exited on a KSP restart instead of resuming.
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Watch_resyncs_after_the_game_restarts(bool events)
        {
            var game = new FakeGame();
            string root = game.Root;
            try
            {
                var args = events
                    ? Ksp(game, "watch", "events", "test.*", "--duration", "12")
                    : Ksp(game, "watch", "altitude", "--rate", "10", "--duration", "12");
                var task = Task.Run(() => Run(args));
                var end = DateTime.UtcNow.AddSeconds(3);
                while (DateTime.UtcNow < end)
                {
                    if (events) game.Sim.Host.Events.Emit("test.before", "system", "before the restart");
                    Thread.Sleep(150);
                }
                if (!events) Assert.Equal(1, game.Sim.Host.Streams.Count);
                game.Sim.Dispose(); // "KSP closes"
                game.Sim = null;
                Thread.Sleep(1500);
                using (var again = new FakeGame(root: root)) // "KSP is back": same folder, new port and endpoint.json
                {
                    while (!task.IsCompleted)
                    {
                        if (events) again.Sim.Host.Events.Emit("test.after", "system", "after the restart");
                        Thread.Sleep(150);
                    }
                    var r = task.Result;
                    Assert.Equal(0, r.Code);
                    Assert.Contains("resync: disconnected", r.Err);
                    Assert.Contains("resync: reconnected", r.Err);
                    if (events)
                    {
                        Assert.Contains("test.before", r.Out);
                        Assert.Contains("test.after", r.Out);
                    }
                    else
                    {
                        // a fresh snapshot after the resync prints the header and the vessel rows again
                        Assert.True(r.Out.Split('\n').Count(l => l.StartsWith("ut ", StringComparison.Ordinal)) >= 2, r.Out);
                    }
                    Assert.True(SpinWait.SpinUntil(() => again.Sim.Host.Streams.Count == 0, 3000));
                }
            }
            finally
            {
                game.Sim?.Dispose();
                try { Directory.Delete(root, true); } catch (Exception) { }
            }
        }

        [Fact]
        public void Watch_no_reconnect_exits_3_when_the_game_goes_away()
        {
            var game = new FakeGame();
            string root = game.Root;
            try
            {
                var task = Task.Run(() => Run(Ksp(game, "watch", "altitude", "--rate", "10", "--duration", "30", "--no-reconnect")));
                Assert.True(SpinWait.SpinUntil(() => game.Sim.Host.Streams.Count == 1, 10000));
                game.Sim.Dispose();
                game.Sim = null;
                Assert.True(task.Wait(10000));
                Assert.Equal(3, task.Result.Code);
                Assert.StartsWith("UNAVAILABLE: disconnected", task.Result.Err);
            }
            finally
            {
                try { Directory.Delete(root, true); } catch (Exception) { }
            }
        }

        // ------------------------------------------------------------------ log

        [Fact]
        public void Log_start_list_tail_get_stop_round_trip()
        {
            using (var game = new FakeGame())
            {
                var start = Run(Ksp(game, "log", "start", "climb", "--fields", "altitude,srfSpeed", "--rate", "20"));
                Assert.Equal(0, start.Code);
                Assert.Matches(@"^log:\d+ started: Skizzy/logs/[^ ]+climb\.csv ", start.Out);

                var dup = Run(Ksp(game, "log", "start", "climb", "--fields", "altitude"));
                Assert.Equal(1, dup.Code);
                Assert.StartsWith("CONFLICT: ", dup.Err);

                Thread.Sleep(700);
                var list = Run(Ksp(game, "log", "list"));
                Assert.Equal(0, list.Code);
                Assert.Contains("climb", list.Out);
                Assert.Contains("running", list.Out);
                Assert.StartsWith("LOG", list.Out);

                var tail = Run(Ksp(game, "log", "tail", "climb", "--lines", "10"));
                Assert.Equal(0, tail.Code);
                Assert.Contains("#log log:", tail.Out);
                Assert.Contains("DDG-1 Kirov", tail.Out);

                string outFile = TempFile(".csv");
                try
                {
                    var get = Run(Ksp(game, "log", "get", "climb", "--out", outFile));
                    Assert.Equal(0, get.Code);
                    Assert.Contains("wrote " + outFile, get.Out);
                    string text = File.ReadAllText(outFile);
                    Assert.Contains("ut,frame,wallUtc,vessel,vesselName,altitude,srfSpeed", text);
                    Assert.Contains("DDG-1 Kirov", text);
                }
                finally { try { File.Delete(outFile); } catch (Exception) { } }

                var stop = Run(Ksp(game, "log", "stop", "climb"));
                Assert.Equal(0, stop.Code);
                Assert.Matches(@"^log:\d+ stopped: Skizzy/logs/.+ \(\d+ rows, \d+ bytes\)", stop.Out);
                Assert.True(File.Exists(Path.Combine(game.Root, stop.Out.Substring(stop.Out.IndexOf(": ", StringComparison.Ordinal) + 2,
                    stop.Out.IndexOf(" (", StringComparison.Ordinal) - stop.Out.IndexOf(": ", StringComparison.Ordinal) - 2))));
                Assert.Contains("stopped", Run(Ksp(game, "log", "list")).Out);
            }
        }

        [Fact]
        public void Log_errors_are_not_found_usage_and_not_connected()
        {
            using (var game = new FakeGame())
            {
                var stop = Run(Ksp(game, "log", "stop", "no-such-log"));
                Assert.Equal(1, stop.Code);
                Assert.StartsWith("NOT_FOUND: ", stop.Err);
                var get = Run(Ksp(game, "log", "get", "log:99", "--out", TempFile(".csv")));
                Assert.Equal(1, get.Code);
                Assert.StartsWith("NOT_FOUND: ", get.Err);
                Assert.Equal(2, Run(Ksp(game, "log", "start", "x")).Code);                         // no --fields
                Assert.Equal(2, Run(Ksp(game, "log", "start", "x", "--fields", "altitude", "--format", "xml")).Code);
                Assert.Equal(2, Run(Ksp(game, "log", "frobnicate")).Code);
                Assert.Equal(2, Run("log").Code);
            }
            string nowhere = Path.Combine(Path.GetTempPath(), "skizzy-nogame-" + Guid.NewGuid().ToString("N"));
            Assert.Equal(3, Run("log", "list", "--ksp", nowhere).Code);
        }

        // ------------------------------------------------------------------ shot

        [Fact]
        public void Shot_writes_the_image_and_save_asks_the_host_to_write_it()
        {
            using (var game = new FakeGame())
            {
                string png = TempFile(".png");
                try
                {
                    var r = Run(Ksp(game, "shot", "--out", png, "--width", "320", "--format", "png", "--target", "DDG-1 Kirov", "--mode", "chase"));
                    Assert.Equal(0, r.Code);
                    Assert.Contains("wrote " + png, r.Out);
                    var bytes = File.ReadAllBytes(png);
                    Assert.Equal(0x89, bytes[0]);
                    Assert.Equal((byte)'P', bytes[1]);
                }
                finally { try { File.Delete(png); } catch (Exception) { } }

                var saved = Run(Ksp(game, "shot", "--save", "bow", "--width", "320", "--format", "png"));
                Assert.Equal(0, saved.Code);
                Assert.Matches(@"^saved Skizzy/screenshots/[^ ]+bow\.png ", saved.Out);
                string rel = saved.Out.Substring("saved ".Length, saved.Out.IndexOf(" (", StringComparison.Ordinal) - "saved ".Length);
                Assert.True(File.Exists(Path.Combine(game.Root, rel)), rel);
            }
        }

        [Fact]
        public void Shot_default_file_name_and_error_paths()
        {
            using (var game = new FakeGame())
            {
                string dir = Path.Combine(Path.GetTempPath(), "skizzy-shotdir-" + Guid.NewGuid().ToString("N").Substring(0, 8));
                Directory.CreateDirectory(dir);
                string cwd = Directory.GetCurrentDirectory();
                try
                {
                    Directory.SetCurrentDirectory(dir);
                    var r = Run(Ksp(game, "shot", "--width", "320"));
                    Assert.Equal(0, r.Code);
                    var files = Directory.GetFiles(dir, "shot-*.*");
                    Assert.Single(files);
                    Assert.Matches(@"shot-\d{8}-\d{6}\.(jpg|png)$", files[0]);
                }
                finally
                {
                    Directory.SetCurrentDirectory(cwd);
                    try { Directory.Delete(dir, true); } catch (Exception) { }
                }
                Assert.Equal(2, Run(Ksp(game, "shot", "--mode", "look_at")).Code);
                Assert.Equal(2, Run(Ksp(game, "shot", "--format", "gif")).Code);
                var missing = Run(Ksp(game, "shot", "--target", "no-such-ship", "--out", TempFile(".jpg")));
                Assert.Equal(1, missing.Code);
                Assert.StartsWith("NOT_FOUND: ", missing.Err);
            }
        }

        // ------------------------------------------------------------------ doctor

        [Fact]
        public void Doctor_on_a_bare_fake_root_fails_the_install_but_passes_token_endpoint_and_connect()
        {
            using (var game = new FakeGame())
            {
                var r = Run(Ksp(game, "doctor"));
                Assert.Equal(1, r.Code);
                var lines = r.Out.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Length > 0).ToList();
                Assert.All(lines, l => Assert.Matches(@"^(PASS|WARN|FAIL|INFO)  \S.* - ", l));
                Assert.Contains(lines, l => l.StartsWith("PASS  KSP folder - ", StringComparison.Ordinal));
                Assert.Contains(lines, l => l.StartsWith("FAIL  KSP_x64.exe - ", StringComparison.Ordinal));
                Assert.Contains(lines, l => l.StartsWith("FAIL  install Skizzy.Host.dll - ", StringComparison.Ordinal));
                Assert.Contains(lines, l => l.StartsWith("FAIL  install Skizzy.Protocol.dll - ", StringComparison.Ordinal));
                Assert.Contains(lines, l => l.StartsWith("PASS  auth.token - present, ", StringComparison.Ordinal));
                Assert.Contains(lines, l => l.StartsWith("PASS  endpoint.json - ", StringComparison.Ordinal) && l.Contains("is running"));
                Assert.Contains(lines, l => l.StartsWith("PASS  port - ", StringComparison.Ordinal));
                Assert.Contains(lines, l => l.StartsWith("PASS  connect - hello in ", StringComparison.Ordinal) && l.Contains(" ms"));
                Assert.Contains(lines, l => l.StartsWith("PASS  host version - 0.1.0", StringComparison.Ordinal));
                Assert.Contains(lines, l => l.StartsWith("PASS  protocol version - ", StringComparison.Ordinal));
                Assert.Contains(lines, l => l.StartsWith("PASS  host.host - ", StringComparison.Ordinal));
                Assert.Contains(lines, l => l.StartsWith("WARN  host.CommNet - ", StringComparison.Ordinal));
                Assert.Contains(lines, l => l.StartsWith("WARN  KSPCommunityFixes - not installed", StringComparison.Ordinal));
                Assert.Contains(lines, l => l.StartsWith("INFO  BDArmory - not installed", StringComparison.Ordinal));
                Assert.Contains(lines, l => l.Contains("host.selfcheck - "));
                Assert.Contains(lines, l => l.StartsWith("PASS  KSP.log - ", StringComparison.Ordinal));
                // the token is never printed
                Assert.DoesNotContain(game.Sim.Token, r.Out + r.Err);
            }
        }

        [Fact]
        public void Doctor_passes_a_complete_install_and_reports_skizzy_errors_in_the_log()
        {
            using (var game = new FakeGame())
            {
                string plugins = Path.Combine(game.Root, "GameData", "SkizzyProtocol", "Plugins");
                Directory.CreateDirectory(plugins);
                File.WriteAllText(Path.Combine(plugins, "Skizzy.Host.dll"), "x");
                File.WriteAllText(Path.Combine(plugins, "Skizzy.Protocol.dll"), "x");
                File.WriteAllText(Path.Combine(game.Root, "KSP_x64.exe"), "x");
                Directory.CreateDirectory(Path.Combine(game.Root, "GameData", "KSPCommunityFixes"));
                Directory.CreateDirectory(Path.Combine(game.Root, "GameData", "BDArmory"));
                Directory.CreateDirectory(Path.Combine(game.Root, "GameData", "Other"));
                File.WriteAllText(Path.Combine(game.Root, "GameData", "Other", "Skizzy.Host.dll"), "x");
                File.AppendAllText(Path.Combine(game.Root, "KSP.log"),
                    "[ERR 12:00:03.000] Skizzy.Host exploded\n[EXC 12:00:04.000] NullReferenceException\n  at Skizzy.Host.Core.Thing.Do ()\n[ERR 12:00:05.000] unrelated\n");

                var r = Run(Ksp(game, "doctor"));
                var lines = r.Out.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Length > 0).ToList();
                Assert.Equal(0, r.Code);
                Assert.Contains(lines, l => l.StartsWith("PASS  KSP_x64.exe - ", StringComparison.Ordinal));
                Assert.Contains(lines, l => l.StartsWith("PASS  install Skizzy.Host.dll - ", StringComparison.Ordinal));
                Assert.Contains(lines, l => l.StartsWith("PASS  install Skizzy.Protocol.dll - ", StringComparison.Ordinal));
                Assert.Contains(lines, l => l.StartsWith("WARN  install copies - 1 other copies of Skizzy.Host.dll", StringComparison.Ordinal) && l.Contains("GameData/Other/Skizzy.Host.dll"));
                Assert.Contains(lines, l => l.StartsWith("PASS  KSPCommunityFixes - ", StringComparison.Ordinal));
                Assert.Contains(lines, l => l.StartsWith("PASS  BDArmory - ", StringComparison.Ordinal));
                Assert.Contains(lines, l => l.StartsWith("WARN  KSP.log - 2 [ERR/[EXC entries mention Skizzy", StringComparison.Ordinal));
                Assert.Contains(lines, l => l.StartsWith("INFO  KSP.log - [ERR 12:00:03.000] Skizzy.Host exploded", StringComparison.Ordinal));
                Assert.Contains(lines, l => l.StartsWith("INFO  KSP.log - [EXC 12:00:04.000] NullReferenceException", StringComparison.Ordinal));
                Assert.DoesNotContain(lines, l => l.Contains("unrelated"));
            }
        }

        [Fact]
        public void Doctor_json_and_fix_and_a_missing_game()
        {
            using (var game = new FakeGame())
            {
                var r = Run(Ksp(game, "doctor", "--json", "--fix"));
                Assert.Equal(1, r.Code);
                var o = JsonValue.Parse(r.Out);
                Assert.False(o.GetBool("ok"));
                var checks = o.GetArr("checks");
                bool found = false;
                for (int i = 0; i < checks.Count; i++)
                {
                    if (checks[i].GetString("check") == "fix") { Assert.Equal("not available in doctor v0", checks[i].GetString("detail")); found = true; }
                    Assert.Contains(checks[i].GetString("status"), new[] { "PASS", "WARN", "FAIL", "INFO" });
                }
                Assert.True(found);
            }
            string nowhere = Path.Combine(Path.GetTempPath(), "skizzy-nogame-" + Guid.NewGuid().ToString("N"));
            var off = Run("doctor", "--ksp", nowhere);
            Assert.Equal(1, off.Code);
            Assert.Contains("FAIL  KSP folder - ", off.Out);
            Assert.Contains("FAIL  connect - ", off.Out);
        }
    }
}
