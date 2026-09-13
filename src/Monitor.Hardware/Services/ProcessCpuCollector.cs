using System.Diagnostics;
using System.ComponentModel;
using Monitor.Hardware.Abstractions;
using Monitor.Hardware.Models;

namespace Monitor.Hardware.Services;

public sealed class ProcessCpuCollector : IProcessCpuCollector
{
    private const int IdleProcessId = 0;
    private const int TopProcessCount = 5;

    private readonly object _syncRoot = new();
    private Dictionary<ProcessIdentity, ProcessSample> _previousSamples = new();
    private long? _previousTimestamp;

    public Task<ProcessCpuSnapshot> CaptureAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_syncRoot)
        {
            var sampleTime = DateTimeOffset.UtcNow;
            var timestamp = Stopwatch.GetTimestamp();
            var elapsed = _previousTimestamp is { } previousTimestamp
                ? Stopwatch.GetElapsedTime(previousTimestamp, timestamp)
                : TimeSpan.Zero;
            var currentSamples = ReadCurrentSamples(cancellationToken);

            var previousSamples = _previousSamples;
            _previousSamples = currentSamples;
            _previousTimestamp = timestamp;

            if (previousSamples.Count == 0 || elapsed <= TimeSpan.Zero)
            {
                return Task.FromResult(new ProcessCpuSnapshot
                {
                    SampleTime = sampleTime,
                    IsReady = false
                });
            }

            var processes = currentSamples
                .Where(pair => previousSamples.TryGetValue(pair.Key, out var previous) &&
                               pair.Value.CpuTime >= previous.CpuTime)
                .Select(pair =>
                {
                    var previous = previousSamples[pair.Key];
                    var cpuSeconds = (pair.Value.CpuTime - previous.CpuTime).TotalSeconds;
                    var usage = cpuSeconds / elapsed.TotalSeconds / Math.Max(1, Environment.ProcessorCount) * 100d;
                    return new ProcessCpuUsage
                    {
                        ProcessId = pair.Key.ProcessId,
                        ProcessName = pair.Value.ProcessName,
                        CpuUsagePercent = Math.Clamp(usage, 0d, 100d)
                    };
                })
                .OrderByDescending(process => process.CpuUsagePercent)
                .ThenBy(process => process.ProcessName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(process => process.ProcessId)
                .Take(TopProcessCount)
                .ToArray();

            return Task.FromResult(new ProcessCpuSnapshot
            {
                SampleTime = sampleTime,
                IsReady = true,
                Processes = processes
            });
        }
    }

    private static Dictionary<ProcessIdentity, ProcessSample> ReadCurrentSamples(
        CancellationToken cancellationToken)
    {
        var samples = new Dictionary<ProcessIdentity, ProcessSample>();
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    if (process.Id == IdleProcessId)
                    {
                        continue;
                    }

                    var processName = process.ProcessName;
                    var cpuTime = process.TotalProcessorTime;
                    var startTimeTicks = TryGetStartTimeTicks(process);
                    var identity = startTimeTicks is { } ticks
                        ? new ProcessIdentity(process.Id, ticks, null)
                        : new ProcessIdentity(process.Id, 0, processName.ToUpperInvariant());

                    samples[identity] = new ProcessSample(processName, cpuTime);
                }
                catch (Exception exception) when (
                    exception is InvalidOperationException or Win32Exception or NotSupportedException or
                    UnauthorizedAccessException)
                {
                    // Protected processes and processes exiting during enumeration are expected.
                }
            }
        }

        return samples;
    }

    private static long? TryGetStartTimeTicks(Process process)
    {
        try
        {
            return process.StartTime.ToUniversalTime().Ticks;
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or Win32Exception or NotSupportedException or
            UnauthorizedAccessException)
        {
            return null;
        }
    }

    private readonly record struct ProcessIdentity(int ProcessId, long StartTimeTicks, string? FallbackName);

    private readonly record struct ProcessSample(string ProcessName, TimeSpan CpuTime);
}
