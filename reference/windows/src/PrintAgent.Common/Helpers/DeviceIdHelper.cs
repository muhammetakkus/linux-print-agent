namespace PrintAgent.Common.Helpers;

public static class DeviceIdHelper
{
    private static string FilePath => Path.Combine(UserDataDirectory.Ensure(), "device_id");

    public static string GetOrCreate()
    {
        if (File.Exists(FilePath))
        {
            var id = File.ReadAllText(FilePath).Trim();
            if (!string.IsNullOrEmpty(id)) return id;
        }

        var newId = Guid.NewGuid().ToString();
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, newId);
        return newId;
    }
}
