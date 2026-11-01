namespace MagSid.Playback;

/// <summary>
/// What went wrong, for a report by someone who could not say how it happened: appended to
/// crash.log beside the editor's settings. Writing it never fails the caller, who is already
/// failing, and a log grown large starts over rather than keeping every crash for good.
/// </summary>
public static class CrashLog
{
    private const long MaxBytes = 256 * 1024;

    public static string FilePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MagSid.Editor", "crash.log");

    public static void Write(string where, Exception? exception)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            if (File.Exists(FilePath) && new FileInfo(FilePath).Length > MaxBytes)
                File.Delete(FilePath);

            string version = System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? "?";
            File.AppendAllText(FilePath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} MagSID {version}, {where}\n{exception}\n\n");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Nowhere to write it: the failure it would have recorded is the one that matters.
        }
    }
}
