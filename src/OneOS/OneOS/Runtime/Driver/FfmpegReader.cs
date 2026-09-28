using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace OneOS.Runtime.Driver
{
    // A local camera (or any ffmpeg input) as a cluster-wide video device (ported from OneOS-V5B): ffmpeg
    // reads it and writes MJPEG (one JPEG per frame, back to back) to stdout, which every consumer receives.
    //
    // Configuration.IO entry: { "Name": "cam0", "Driver": "ffmpeg", "Arguments": [ "<input>", "key=value", … ] }
    //   input       the device or source: /dev/video0 (Linux), "Integrated Webcam" (Windows), "0" (macOS),
    //               or, with format=lavfi, a test source such as "testsrc=size=640x480:rate=15"
    //   format      ffmpeg input format; default v4l2 (Linux), dshow (Windows), avfoundation (macOS)
    //   size        capture size, default 640x480     framerate  capture rate, default 30
    //   rate        output frames per second, default 15      bitrate  default 300k
    // Requires ffmpeg on the runtime's host.
    public sealed class FfmpegReader : IODriver
    {
        private const int BufferSize = 460800;

        private Process? _process;
        private readonly Queue<string> _stderrTail = new();

        public FfmpegReader(Runtime runtime, IOConfiguration config, ILoggerFactory loggerFactory)
            : base(runtime, config, loggerFactory.CreateLogger<FfmpegReader>())
        {
            if (config.Arguments.Count == 0) throw new ArgumentException($"ffmpeg device '{config.Name}' needs an input (its first argument)");
        }

        public override string DeviceType => "video";

        public static string DefaultFormat =>
            RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "dshow" : RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? "avfoundation" : "v4l2";

        // ffmpeg's arguments for a device's configuration arguments.
        public static IReadOnlyList<string> Command(IReadOnlyList<string> arguments)
        {
            var input = arguments[0];
            var options = arguments.Skip(1).Select(a => a.Split('=', 2)).Where(kv => kv.Length == 2)
                .ToDictionary(kv => kv[0].Trim().ToLowerInvariant(), kv => kv[1].Trim());
            var format = options.GetValueOrDefault("format", DefaultFormat);
            var args = new List<string> { "-hide_banner", "-loglevel", "error", "-f", format };
            if (format != "lavfi")
            {
                args.AddRange(new[] { "-framerate", options.GetValueOrDefault("framerate", "30"), "-video_size", options.GetValueOrDefault("size", "640x480") });
                if (format == "dshow" && !input.StartsWith("video=", StringComparison.Ordinal)) input = "video=" + input;
            }
            args.AddRange(new[] { "-i", input, "-an", "-b:v", options.GetValueOrDefault("bitrate", "300k"), "-r", options.GetValueOrDefault("rate", "15"),
                "-f", "image2pipe", "-vcodec", "mjpeg", "pipe:1" });
            return args;
        }

        protected override Task StartDeviceAsync(CancellationToken ct)
        {
            var psi = new ProcessStartInfo("ffmpeg")
            {
                UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                WorkingDirectory = _runtime.Config.TempPath,
            };
            foreach (var a in Command(Config.Arguments)) psi.ArgumentList.Add(a);
            System.IO.Directory.CreateDirectory(psi.WorkingDirectory);
            _logger.LogInformation("{Uri}: ffmpeg {Arguments}", URI, string.Join(" ", psi.ArgumentList));
            Process process;
            try { process = Process.Start(psi)!; }
            catch (System.ComponentModel.Win32Exception ex)
            {
                _logger.LogError("{Uri}: ffmpeg could not be started ({Error}); is it installed?", URI, ex.Message);
                _ = DeviceEndedAsync();
                return Task.CompletedTask;
            }
            _process = process;
            _ = Task.Run(() => ReadAsync(process, ct));
            // Drained so ffmpeg never blocks on stderr; the tail explains a failed exit.
            _ = Task.Run(async () =>
            {
                string? line;
                while ((line = await process.StandardError.ReadLineAsync()) != null)
                    lock (_stderrTail) { _stderrTail.Enqueue(line); if (_stderrTail.Count > 20) _stderrTail.Dequeue(); }
            });
            return Task.CompletedTask;
        }

        private async Task ReadAsync(Process process, CancellationToken ct)
        {
            var buffer = new byte[BufferSize];
            try
            {
                int n;
                while ((n = await process.StandardOutput.BaseStream.ReadAsync(buffer, ct)) > 0)
                    await PublishAsync(buffer[..n]);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or System.IO.IOException) { }
            if (ct.IsCancellationRequested) return;
            try { await process.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5)); } catch (Exception) { }
            string tail;
            lock (_stderrTail) tail = string.Join(" | ", _stderrTail);
            _logger.LogWarning("{Uri}: ffmpeg ended ({Code}) {Error}", URI, process.HasExited ? process.ExitCode : -1, tail);
            await DeviceEndedAsync();
        }

        protected override Task StopDeviceAsync()
        {
            var process = _process;
            _process = null;
            if (process != null)
            {
                try { if (!process.HasExited) process.Kill(true); } catch (Exception) { }
                process.Dispose();
            }
            return Task.CompletedTask;
        }
    }
}
