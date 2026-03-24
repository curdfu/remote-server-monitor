namespace Monitor.Storage.Configuration;

public static class StoragePathHelper
{
    public const string DatabaseFileName = "monitor.db";

    public static string GetDatabasePath(string? baseDirectory = null)
    {
        var resolvedBaseDirectory = string.IsNullOrWhiteSpace(baseDirectory)
            ? AppContext.BaseDirectory
            : Path.GetFullPath(baseDirectory);

        Directory.CreateDirectory(resolvedBaseDirectory);
        return Path.Combine(resolvedBaseDirectory, DatabaseFileName);
    }
}
