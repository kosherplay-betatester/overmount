namespace Darkmount.App;

/// <summary>Daily log files in %LOCALAPPDATA%\OverMount\logs, keeping the last 7.</summary>
public static class Log
{
    static readonly object Gate = new();
    static readonly string Dir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OverMount", "logs");

    public static string Directory => Dir;

    /// <summary>Turned off by unit tests so they never write to the user's log.</summary>
    public static bool Enabled { get; set; } = true;

    public static void Write(string message)
    {
        if (!Enabled) return;
        try
        {
            lock (Gate)
            {
                System.IO.Directory.CreateDirectory(Dir);
                File.AppendAllText(Path.Combine(Dir, $"{DateTime.Now:yyyy-MM-dd}.log"), $"{DateTime.Now:HH:mm:ss.fff} {message}{Environment.NewLine}");
            }
        }
        catch (Exception) { /* logging must never crash the app (full disk, no access, out of memory…) */ }
    }

    public static void Prune()
    {
        try
        {
            foreach (var f in new DirectoryInfo(Dir).GetFiles("*.log").OrderByDescending(f => f.Name).Skip(7)) f.Delete();
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
