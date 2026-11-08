using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using PhotoLab.UI.Contracts;

namespace PhotoLab.UI.Services;

public sealed class BackendProcessService(NamedPipeClient client) : IDisposable
{
    private Process? _ownedProcess;
    public string LastError { get; private set; } = "";
    public static string FindProjectRoot()
    {
        string? configured = Environment.GetEnvironmentVariable("PHOTOLAB_ROOT");
        if (configured is not null && File.Exists(Path.Combine(configured, "backend", "environment.yml"))) return configured;
        for (var folder = new DirectoryInfo(AppContext.BaseDirectory); folder is not null; folder = folder.Parent)
            if (File.Exists(Path.Combine(folder.FullName, "backend", "environment.yml"))) return folder.FullName;
        throw new DirectoryNotFoundException("Project root not found. Set PHOTOLAB_ROOT.");
    }

    public async Task EnsureReadyAsync()
    {
        string root = FindProjectRoot();
        string revision = ComputeSourceRevision(root);
        try
        {
            await client.ConnectAsync(500);
            var ready = await client.SendAsync(new CommandMessage { MessageType = "ping" });
            if (ready.BackendRevision == revision) return;
            // Keep external sessions intact; restore open UI documents into a fresh backend.
            await StopOwnedAsync();
            _ownedProcess?.Dispose();
            _ownedProcess = null;
            client.UsePrivateSession();
        }
        catch (Exception error) when (error is IOException or TimeoutException or OperationCanceledException) { }
        if (_ownedProcess is null || _ownedProcess.HasExited)
        {
            string python = Environment.GetEnvironmentVariable("PHOTOLAB_PYTHON")
                ?? Path.Combine(root, ".tools", "miniforge3", "envs", "photolab", "python.exe");
            if (!File.Exists(python)) throw new FileNotFoundException("Conda Python not found. Run scripts\\setup_conda.cmd, then scripts\\run_all.cmd.");
            var start = new ProcessStartInfo(python)
            {
                WorkingDirectory = Path.Combine(root, "backend"), UseShellExecute = false,
                CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true
            };
            start.ArgumentList.Add("-m"); start.ArgumentList.Add("app.main");
            start.ArgumentList.Add("--pipe-name"); start.ArgumentList.Add(client.PipeName);
            _ownedProcess = new Process { StartInfo = start };
            _ownedProcess.ErrorDataReceived += (_, e) => { if (e.Data is not null) LastError = e.Data; };
            _ownedProcess.Start();
            _ownedProcess.BeginErrorReadLine(); _ownedProcess.BeginOutputReadLine();
        }
        for (int attempt = 0; attempt < 30; attempt++)
        {
            if (_ownedProcess.HasExited) throw new IOException($"The backend stopped: {LastError}");
            try
            {
                await client.ConnectAsync(300);
                var ready = await client.SendAsync(new CommandMessage { MessageType = "ping" });
                if (ready.BackendRevision != revision)
                    throw new IOException("The backend source files changed during startup. Click Reconnect.");
                return;
            }
            catch (Exception error) when (error is IOException or TimeoutException or OperationCanceledException) { await Task.Delay(100); }
        }
        throw new TimeoutException("The backend did not respond within 12 seconds.");
    }

    public static string ComputeSourceRevision(string projectRoot)
    {
        string directory = Path.Combine(projectRoot, "backend", "app");
        string plugins = Path.Combine(projectRoot, "plugins");
        var sources = Directory.EnumerateFiles(directory, "*.py", SearchOption.AllDirectories)
            .Select(path => (Path: path, Relative: Path.GetRelativePath(directory, path).Replace('\\', '/')))
            .Concat(Directory.Exists(plugins)
                ? Directory.EnumerateFiles(plugins, "*", SearchOption.AllDirectories)
                    .Where(path => Path.GetExtension(path) is ".py" or ".yaml" or ".yml")
                    .Select(path => (Path: path, Relative: "plugins/" + Path.GetRelativePath(plugins, path).Replace('\\', '/')))
                : Enumerable.Empty<(string Path, string Relative)>())
            .OrderBy(file => file.Relative, StringComparer.Ordinal).ToArray();
        if (sources.Length == 0) throw new FileNotFoundException("Backend Python source files not found.");
        using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var file in sources)
        {
            digest.AppendData(Encoding.UTF8.GetBytes(file.Relative + "\0"));
            digest.AppendData(File.ReadAllBytes(file.Path));
            digest.AppendData(new byte[] { 0 });
        }
        return Convert.ToHexString(digest.GetHashAndReset()).ToLowerInvariant();
    }

    public async Task StopOwnedAsync()
    {
        if (_ownedProcess is null || _ownedProcess.HasExited) return;
        try
        {
            if (client.IsConnected) await client.SendAsync(new CommandMessage { MessageType = "shutdown" });
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await _ownedProcess.WaitForExitAsync(timeout.Token);
        }
        catch (Exception) { if (!_ownedProcess.HasExited) _ownedProcess.Kill(); }
    }
    public void Dispose() => _ownedProcess?.Dispose();
}
