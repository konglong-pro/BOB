namespace Bob.Windows;

public sealed record AppPaths(string RootDirectory, string IdentityDirectory)
{
    public static AppPaths ForCurrentUser()
    {
        var localAppData = Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData,
            Environment.SpecialFolderOption.Create);

        if (string.IsNullOrWhiteSpace(localAppData))
        {
            throw new InvalidOperationException("Could not locate the current user's LocalAppData directory.");
        }

        var root = Path.Combine(localAppData, "BOB");
        return new AppPaths(root, Path.Combine(root, "identity"));
    }
}
