using System.Diagnostics;
using System.Text;

namespace Onboarding.Steps.Scripts;

public sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError, bool TimedOut);

/// <summary>Starts an external process with stdin input and captured stdout/stderr.</summary>
public interface IProcessRunner
{
    Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments, string standardInput, TimeSpan timeout, CancellationToken cancellationToken);
}

/// <summary>
/// Runs a process; stdin is written and closed, stdout/stderr are read concurrently. On timeout
/// or cancellation the whole process tree is killed (DECISIONS X3).
/// </summary>
public sealed class ProcessRunner : IProcessRunner
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    public async Task<ProcessResult> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        string standardInput,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        var startInfo = new ProcessStartInfo(fileName)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardInputEncoding = Utf8NoBom,
            StandardOutputEncoding = Utf8NoBom,
            StandardErrorEncoding = Utf8NoBom,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        process.Start();
        var stdout = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var stderr = process.StandardError.ReadToEndAsync(CancellationToken.None);

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        var timedOut = false;
        try
        {
            try
            {
                await process.StandardInput.WriteAsync(standardInput.AsMemory(), timeoutSource.Token).ConfigureAwait(false);
                process.StandardInput.Close();
            }
            catch (IOException)
            {
                // The process exited without reading stdin (broken pipe); its exit code tells why.
            }

            await process.WaitForExitAsync(timeoutSource.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            KillTree(process);
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            if (cancellationToken.IsCancellationRequested)
            {
                throw;
            }

            timedOut = true;
        }

        return new ProcessResult(
            timedOut ? -1 : process.ExitCode,
            await stdout.ConfigureAwait(false),
            await stderr.ConfigureAwait(false),
            timedOut);
    }

    private static void KillTree(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
            // already exited
        }
    }
}
