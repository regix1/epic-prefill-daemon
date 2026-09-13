using EpicPrefill.Api;
using EpicPrefill.Settings;
using Spectre.Console;

namespace EpicPrefill
{
    public static class Program
    {
        public static async Task<int> Main()
        {
            try
            {
                ParseHiddenFlags();

                AnsiConsole.WriteLine($"EpicPrefill daemon v{ThisAssembly.Info.InformationalVersion}");

                var tcpPortEnv = Environment.GetEnvironmentVariable("PREFILL_TCP_PORT");
                var useTcp = int.TryParse(tcpPortEnv, out var tcpPort) && tcpPort > 0;

                var responsesDir = Environment.GetEnvironmentVariable("PREFILL_RESPONSES_DIR") ?? "/responses";
                var socketPath = Environment.GetEnvironmentVariable("PREFILL_SOCKET_PATH") ??
                                Path.Combine(responsesDir, "daemon.sock");

                using var cts = new CancellationTokenSource();

                Console.CancelKeyPress += (_, e) =>
                {
                    e.Cancel = true;
                    AnsiConsole.WriteLine("\nShutdown signal received...");
#pragma warning disable AsyncFixer02 // Console signal callbacks cannot await asynchronous cancellation.
#pragma warning disable CA1849
#pragma warning disable VSTHRD103
                    cts.Cancel();
#pragma warning restore VSTHRD103
#pragma warning restore CA1849
#pragma warning restore AsyncFixer02
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
                AnsiConsole.WriteLine($"Fatal error: {e.Message}");
                if (AppConfig.DebugLogs)
                {
                    AnsiConsole.WriteLine(e.StackTrace ?? string.Empty);
                }
                return 1;
            }
        }

        private static void ParseHiddenFlags()
        {
            var args = Environment.GetCommandLineArgs().Skip(1).ToList();

            if (args.Any(e => e.Contains("--debug")))
            {
                AnsiConsole.WriteLine("Using --debug flag. Displaying debug only logging...");
                AnsiConsole.WriteLine($"Additional debugging files will be output to {AppConfig.DebugOutputDir}");
                AppConfig.DebugLogs = true;
            }

            if (args.Any(e => e.Contains("--no-download")))
            {
                AnsiConsole.WriteLine("Using --no-download flag. Will skip downloading chunks...");
                AppConfig.SkipDownloads = true;
            }

            if (args.Any(e => e.Contains("--nocache")) || args.Any(e => e.Contains("--no-cache")))
            {
                AnsiConsole.WriteLine("Using --nocache flag. Will always re-download manifests...");
                AppConfig.NoLocalCache = true;
            }

            if (AppConfig.DebugLogs || AppConfig.SkipDownloads || AppConfig.NoLocalCache)
            {
                AnsiConsole.WriteLine();
                AnsiConsole.WriteLine(new string('─', 60));
            }
        }
    }
}
