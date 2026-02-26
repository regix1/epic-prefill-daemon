using EpicPrefill.Api;
using EpicPrefill.Settings;

namespace EpicPrefill
{
    public static class Program
    {
        public static async Task<int> Main()
        {
            try
            {
                ParseHiddenFlags();

                Console.WriteLine($"""
                    ╔═══════════════════════════════════════════════════════════╗
                    ║              EpicPrefill Daemon                           ║
                    ║                  v{ThisAssembly.Info.InformationalVersion,-20}             ║
                    ╚═══════════════════════════════════════════════════════════╝

                    """);

                var tcpPortEnv = Environment.GetEnvironmentVariable("PREFILL_TCP_PORT");
                var useTcp = int.TryParse(tcpPortEnv, out var tcpPort) && tcpPort > 0;

                if (!useTcp)
                {
                    Console.WriteLine("Using Unix Domain Socket for reliable, low-latency IPC.");
                    Console.WriteLine();
                }

                var responsesDir = Environment.GetEnvironmentVariable("PREFILL_RESPONSES_DIR") ?? "/responses";
                var socketPath = Environment.GetEnvironmentVariable("PREFILL_SOCKET_PATH") ??
                                Path.Combine(responsesDir, "daemon.sock");

                using var cts = new CancellationTokenSource();

                Console.CancelKeyPress += (_, e) =>
                {
                    e.Cancel = true;
                    Console.WriteLine("\nShutdown signal received...");
                    cts.Cancel();
                };

                if (useTcp)
                {
                    await DaemonMode.RunTcpAsync(tcpPort, cts.Token);
                }
                else
                {
                    await DaemonMode.RunAsync(socketPath, cts.Token);
                }

                return 0;
            }
            catch (OperationCanceledException)
            {
                return 0;
            }
            catch (Exception e)
            {
                Console.WriteLine($"Fatal error: {e.Message}");
                if (AppConfig.DebugLogs)
                {
                    Console.WriteLine(e.StackTrace);
                }
                return 1;
            }
        }

        private static void ParseHiddenFlags()
        {
            var args = Environment.GetCommandLineArgs().Skip(1).ToList();

            if (args.Any(e => e.Contains("--debug")))
            {
                Console.WriteLine($"Using --debug flag. Displaying debug only logging...");
                Console.WriteLine($"Additional debugging files will be output to {AppConfig.DebugOutputDir}");
                AppConfig.DebugLogs = true;
            }

            if (args.Any(e => e.Contains("--no-download")))
            {
                Console.WriteLine($"Using --no-download flag. Will skip downloading chunks...");
                AppConfig.SkipDownloads = true;
            }

            if (args.Any(e => e.Contains("--nocache")) || args.Any(e => e.Contains("--no-cache")))
            {
                Console.WriteLine($"Using --nocache flag. Will always re-download manifests...");
                AppConfig.NoLocalCache = true;
            }

            if (AppConfig.DebugLogs || AppConfig.SkipDownloads || AppConfig.NoLocalCache)
            {
                Console.WriteLine();
                Console.WriteLine(new string('─', 60));
            }
        }
    }
}
