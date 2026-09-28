using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

namespace OneOS.Runtime.Driver
{
    // Installs the JavaScript environment (oneos.js, Driver/JavaScriptEnvironment) into a runtime's temp
    // directory, where JavaScript agents run:
    //   <temp>/node_modules/oneos/*.js   the library, from this assembly (refreshed at every start)
    //   <temp>/node_modules/<deps>      the instrumenter's npm dependencies (installed at config time)
    // Instrumented programs live in <temp> too, so `require('oneos')` and the dependencies resolve there.
    public static class JavaScriptEnvironmentInstaller
    {
        // What instrument.js/Code.js need; applications install their own.
        public static readonly IReadOnlyList<string> Dependencies = new[] { "esprima@4.0.1", "escodegen@2.1.0", "js-beautify@1.14.11" };

        private const string ResourcePrefix = "oneos.js/";

        public static string ModuleDirectory(string tempPath) => Path.Combine(tempPath, "node_modules", "oneos");

        public static IEnumerable<string> LibraryFiles =>
            typeof(JavaScriptEnvironmentInstaller).Assembly.GetManifestResourceNames()
                .Where(n => n.StartsWith(ResourcePrefix, StringComparison.Ordinal)).Select(n => n[ResourcePrefix.Length..]);

        // Writes the library files (overwriting older ones).
        public static void WriteLibrary(string tempPath)
        {
            var dir = ModuleDirectory(tempPath);
            Directory.CreateDirectory(dir);
            var assembly = typeof(JavaScriptEnvironmentInstaller).Assembly;
            foreach (var file in LibraryFiles)
            {
                using var resource = assembly.GetManifestResourceStream(ResourcePrefix + file)!;
                using var output = File.Create(Path.Combine(dir, file));
                resource.CopyTo(output);
            }
        }

        public static bool DependenciesInstalled(string tempPath) =>
            Dependencies.All(d => Directory.Exists(Path.Combine(tempPath, "node_modules", d[..d.LastIndexOf('@')])));

        public static bool IsInstalled(string tempPath) =>
            File.Exists(Path.Combine(ModuleDirectory(tempPath), "index.js")) && DependenciesInstalled(tempPath);

        // `npm install` of the dependencies into <temp> (creates a private package.json first). Returns
        // whether it succeeded, and npm's output.
        public static async Task<(bool Ok, string Output)> InstallDependenciesAsync(string tempPath, CancellationToken ct = default)
        {
            Directory.CreateDirectory(tempPath);
            var packageJson = Path.Combine(tempPath, "package.json");
            if (!File.Exists(packageJson)) await File.WriteAllTextAsync(packageJson, "{ \"private\": true }\n", ct);
            var psi = new ProcessStartInfo("npm")
            {
                WorkingDirectory = tempPath, UseShellExecute = false,
                RedirectStandardOutput = true, RedirectStandardError = true,
            };
            foreach (var a in new[] { "install", "--no-audit", "--no-fund", "--save" }.Concat(Dependencies)) psi.ArgumentList.Add(a);
            try
            {
                using var npm = Process.Start(psi)!;
                var stdout = npm.StandardOutput.ReadToEndAsync(ct);
                var stderr = npm.StandardError.ReadToEndAsync(ct);
                await npm.WaitForExitAsync(ct);
                return (npm.ExitCode == 0, (await stdout) + (await stderr));
            }
            catch (System.ComponentModel.Win32Exception ex)
            {
                return (false, $"npm could not be started ({ex.Message}); is Node.js installed?");
            }
        }

        // Config time: the library, and the dependencies unless they are already there.
        public static async Task<(bool Ok, string Output)> InstallAsync(string tempPath, CancellationToken ct = default)
        {
            WriteLibrary(tempPath);
            if (DependenciesInstalled(tempPath)) return (true, "dependencies already installed");
            return await InstallDependenciesAsync(tempPath, ct);
        }
    }
}
