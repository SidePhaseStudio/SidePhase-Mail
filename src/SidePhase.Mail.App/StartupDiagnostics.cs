using System.Runtime.InteropServices;
using System.Text;
using SidePhase.Mail.Infrastructure.Storage;

namespace SidePhase.Mail.App;

/// <summary>
/// Makes start-up failures visible: writes them to %LOCALAPPDATA%\SidePhase Mail\Logs\startup-error.log and shows a
/// native message box, so the app never just silently fails to open.
/// </summary>
internal static class StartupDiagnostics
{
    private const uint MB_OK = 0x0;
    private const uint MB_ICONERROR = 0x10;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(nint hWnd, string text, string caption, uint type);

    public static string LogFile => Path.Combine(new AppDataPaths().LogsFolder, "startup-error.log");

    public static void Report(string stage, Exception exception, bool showDialog = true)
    {
        var details = new StringBuilder()
            .Append(DateTimeOffset.Now.ToString("O")).Append(" — failed during ").AppendLine(stage)
            .Append("OS: ").Append(Environment.OSVersion.VersionString)
            .Append(", ").Append(RuntimeInformation.OSArchitecture)
            .Append(", process ").AppendLine(RuntimeInformation.ProcessArchitecture.ToString())
            .AppendLine(exception.ToString())
            .AppendLine()
            .ToString();

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogFile)!);
            File.AppendAllText(LogFile, details);
        }
        catch (Exception)
        {
            // Nothing else we can do; the message box below still reports the problem.
        }

        Console.Error.WriteLine(details);

        if (showDialog && Environment.GetEnvironmentVariable("SIDEPHASE_NO_ERROR_DIALOG") != "1")
        {
            MessageBoxW(0,
                $"SidePhase Mail couldn't start ({stage}).\n\n{exception.GetType().Name}: {exception.Message}\n\nDetails were saved to:\n{LogFile}",
                "SidePhase Mail", MB_OK | MB_ICONERROR);
        }
    }
}
