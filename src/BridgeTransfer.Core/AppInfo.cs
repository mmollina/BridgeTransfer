namespace BridgeTransfer.Core;

/// <summary>Central product identity. Change <see cref="ProductName"/> to rename the application.</summary>
public static class AppInfo
{
    public const string ProductName = "BridgeTransfer";

    /// <summary>Prefix used for this application's own staging files.</summary>
    public const string StagingPrefix = ".bt-staging-";

    public const string StagingSuffix = ".part";

    public static string Version =>
        typeof(AppInfo).Assembly.GetName().Version is { } v ? $"{v.Major}.{v.Minor}.{v.Build}" : "1.0.0";
}
