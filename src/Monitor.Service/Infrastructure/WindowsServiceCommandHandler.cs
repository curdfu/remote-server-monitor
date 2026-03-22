using System.Diagnostics;

namespace Monitor.Service.Infrastructure;

public static class WindowsServiceCommandHandler
{
    private const int SuccessExitCode = 0;
    private const int ServiceAlreadyExistsExitCode = 11;
    private const int GenericFailureExitCode = 1;
    private const int ServiceControlTimeoutMilliseconds = 30000;
    private const int ServiceControlPollIntervalMilliseconds = 1000;

    public static bool TryHandle(string[] args, out int exitCode)
    {
        exitCode = SuccessExitCode;

        if (args.Length == 0)
        {
            return false;
        }

        var command = args[0].Trim().ToLowerInvariant();
        switch (command)
        {
            case "install":
                exitCode = HandleInstall();
                return true;

            case "uninstall":
                exitCode = HandleUninstall();
                return true;

            default:
                return false;
        }
    }

    private static int HandleInstall()
    {
        if (!OperatingSystem.IsWindows())
        {
            Console.Error.WriteLine("Windows service installation is only supported on Windows.");
            return GenericFailureExitCode;
        }

        try
        {
            if (ServiceExists())
            {
                Console.WriteLine($"Service '{ServiceConstants.ServiceName}' already exists.");
                return ServiceAlreadyExistsExitCode;
            }

            var executablePath = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(executablePath))
            {
                Console.Error.WriteLine("Unable to determine current executable path.");
                return GenericFailureExitCode;
            }

            var createExitCode = RunSc(
                "create",
                ServiceConstants.ServiceName,
                $"""binPath= "{executablePath}" """,
                "start= auto",
                $"""DisplayName= "{ServiceConstants.DisplayName}" """);

            if (createExitCode != 0)
            {
                Console.Error.WriteLine($"Failed to create service '{ServiceConstants.ServiceName}'. sc.exe exit code: {createExitCode}");
                return createExitCode;
            }

            _ = RunSc(
                "description",
                ServiceConstants.ServiceName,
                ServiceConstants.DisplayName);

            Console.WriteLine($"Service '{ServiceConstants.ServiceName}' installed successfully.");
            return SuccessExitCode;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Failed to install service '{ServiceConstants.ServiceName}': {exception.Message}");
            return GenericFailureExitCode;
        }
    }

    private static int HandleUninstall()
    {
        if (!OperatingSystem.IsWindows())
        {
            Console.Error.WriteLine("Windows service uninstallation is only supported on Windows.");
            return GenericFailureExitCode;
        }

        try
        {
            if (!ServiceExists())
            {
                Console.WriteLine($"Service '{ServiceConstants.ServiceName}' does not exist.");
                return SuccessExitCode;
            }

            if (TryGetServiceState(out var state) && !state.Equals("STOPPED", StringComparison.OrdinalIgnoreCase))
            {
                var stopExitCode = RunSc("stop", ServiceConstants.ServiceName);
                if (stopExitCode != 0 && stopExitCode != 1062)
                {
                    Console.Error.WriteLine($"Failed to stop service '{ServiceConstants.ServiceName}'. sc.exe exit code: {stopExitCode}");
                    return stopExitCode;
                }

                WaitForServiceState("STOPPED", TimeSpan.FromMilliseconds(ServiceControlTimeoutMilliseconds));
            }

            var deleteExitCode = RunSc("delete", ServiceConstants.ServiceName);
            if (deleteExitCode != 0 && deleteExitCode != 1060)
            {
                Console.Error.WriteLine($"Failed to delete service '{ServiceConstants.ServiceName}'. sc.exe exit code: {deleteExitCode}");
                return deleteExitCode;
            }

            Console.WriteLine($"Service '{ServiceConstants.ServiceName}' uninstalled successfully.");
            return SuccessExitCode;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Failed to uninstall service '{ServiceConstants.ServiceName}': {exception.Message}");
            return GenericFailureExitCode;
        }
    }

    private static bool ServiceExists()
    {
        return RunSc("query", ServiceConstants.ServiceName) == 0;
    }

    private static bool TryGetServiceState(out string state)
    {
        var (exitCode, standardOutput, _) = RunProcess("sc.exe", "query", ServiceConstants.ServiceName);
        if (exitCode != 0)
        {
            state = string.Empty;
            return false;
        }

        var stateLine = standardOutput
            .Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault(line => line.Contains("STATE", StringComparison.OrdinalIgnoreCase));

        if (stateLine is null)
        {
            state = string.Empty;
            return false;
        }

        var stateTokens = stateLine
            .Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries)
            .SkipWhile(token => !token.Equals("STATE", StringComparison.OrdinalIgnoreCase))
            .Skip(2)
            .ToArray();

        state = stateTokens.FirstOrDefault() ?? string.Empty;
        return !string.IsNullOrWhiteSpace(state);
    }

    private static void WaitForServiceState(string expectedState, TimeSpan timeout)
    {
        var startedAt = DateTimeOffset.UtcNow;
        while (DateTimeOffset.UtcNow - startedAt < timeout)
        {
            if (TryGetServiceState(out var currentState) &&
                currentState.Equals(expectedState, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            Thread.Sleep(ServiceControlPollIntervalMilliseconds);
        }

        throw new TimeoutException(
            $"Timed out waiting for service '{ServiceConstants.ServiceName}' to reach state '{expectedState}'.");
    }

    private static int RunSc(params string[] arguments)
    {
        var (exitCode, _, _) = RunProcess("sc.exe", arguments);
        return exitCode;
    }

    private static (int ExitCode, string StandardOutput, string StandardError) RunProcess(string fileName, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Failed to start process '{fileName}'.");

        var standardOutput = process.StandardOutput.ReadToEnd();
        var standardError = process.StandardError.ReadToEnd();
        process.WaitForExit();

        return (process.ExitCode, standardOutput, standardError);
    }
}
