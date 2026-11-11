namespace PrintAgent.Core;

public static class DeviceIdentity
{
    // No OS-specific default path is selected in stage 3.1.
    public static string GetOrCreate(string filePath)
    {
        if (File.Exists(filePath))
        {
            var id = File.ReadAllText(filePath).Trim();
            if (!string.IsNullOrEmpty(id)) return id;
        }

        var newId = Guid.NewGuid().ToString();
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
        File.WriteAllText(filePath, newId);
        return newId;
    }
}
