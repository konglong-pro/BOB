using System;
using System.Diagnostics;
using System.IO;
using System.Windows.Forms;

internal static class Program
{
    [STAThread]
    private static int Main()
    {
        string repositoryRoot = AppDomain.CurrentDomain.BaseDirectory.TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar);
        string dotnetRoot = Path.Combine(repositoryRoot, ".tools", "dotnet");
        string dotnetPath = Path.Combine(dotnetRoot, "dotnet.exe");
        string releaseBase = Path.Combine(
            repositoryRoot,
            "src",
            "windows",
            "Bob.Windows",
            "bin",
            "Release",
            "net10.0-windows",
            "BOB");
        string debugBase = Path.Combine(
            repositoryRoot,
            "src",
            "windows",
            "Bob.Windows",
            "bin",
            "Debug",
            "net10.0-windows",
            "BOB");

        if (!File.Exists(dotnetPath))
        {
            return ShowError(
                "The repository's portable .NET runtime is missing.\n\nRun scripts\\setup-toolchain.ps1 "
                + "and keep BOB.exe in the repository root.\n\nMissing:\n"
                + dotnetPath);
        }

        string appPath = IsCompleteApplication(releaseBase)
            ? releaseBase + ".dll"
            : (IsCompleteApplication(debugBase) ? debugBase + ".dll" : null);
        if (appPath == null)
        {
            return ShowError(
                "A complete BOB Windows build was not found.\n\nRun "
                + "scripts\\publish-windows-launcher.ps1.\n\nChecked:\n"
                + releaseBase
                + "\n"
                + debugBase
                + "\n\nEach build must include its .dll, .deps.json, and .runtimeconfig.json files.");
        }

        try
        {
            ProcessStartInfo startInfo = new ProcessStartInfo();
            startInfo.FileName = dotnetPath;
            startInfo.Arguments = "\"" + appPath + "\"";
            startInfo.WorkingDirectory = repositoryRoot;
            startInfo.UseShellExecute = false;
            startInfo.CreateNoWindow = true;
            startInfo.WindowStyle = ProcessWindowStyle.Hidden;
            startInfo.EnvironmentVariables["DOTNET_ROOT"] = dotnetRoot;
            startInfo.EnvironmentVariables["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
            startInfo.EnvironmentVariables["DOTNET_NOLOGO"] = "1";
            startInfo.EnvironmentVariables["PATH"] = dotnetRoot
                + Path.PathSeparator
                + (Environment.GetEnvironmentVariable("PATH") ?? string.Empty);

            Process process = Process.Start(startInfo);
            if (process == null)
            {
                throw new InvalidOperationException("The system did not return the newly started process.");
            }

            if (process.WaitForExit(1000))
            {
                int exitCode = process.ExitCode;
                process.Dispose();
                if (exitCode != 0)
                {
                    return ShowError("BOB exited immediately with exit code " + exitCode + ".");
                }

                return 0;
            }

            process.Dispose();
            return 0;
        }
        catch (Exception)
        {
            return ShowError(
                "BOB could not start.\n\n"
                + "Check that the repository and portable .NET files are intact, then try again.");
        }
    }

    private static bool IsCompleteApplication(string applicationBasePath)
    {
        return File.Exists(applicationBasePath + ".dll")
            && File.Exists(applicationBasePath + ".deps.json")
            && File.Exists(applicationBasePath + ".runtimeconfig.json");
    }

    private static int ShowError(string message)
    {
        MessageBox.Show(
            message,
            "BOB launcher failed",
            MessageBoxButtons.OK,
            MessageBoxIcon.Error);
        return 1;
    }
}
