using Xunit;

namespace PrintAgent.Core.Tests;

public class DeviceIdentityTests
{
    [Fact]
    public void New_identity_is_a_persisted_Guid_and_reused_on_restart()
    {
        var directory = Path.Combine(Path.GetTempPath(), "nuxprint-identity-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "device_id");
        try
        {
            var id = DeviceIdentity.GetOrCreate(path);
            Assert.True(Guid.TryParseExact(id, "D", out _));
            Assert.Equal(id, DeviceIdentity.GetOrCreate(path));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Theory]
    [InlineData("  previous-device-id\n", "previous-device-id")]
    [InlineData("", null)]
    [InlineData(" \r\n", null)]
    public void Existing_nonempty_identity_is_trimmed_but_not_revalidated(string stored, string? expected)
    {
        var directory = Path.Combine(Path.GetTempPath(), "nuxprint-identity-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "device_id");
        try
        {
            File.WriteAllText(path, stored);
            var id = DeviceIdentity.GetOrCreate(path);
            if (expected == null) Assert.True(Guid.TryParseExact(id, "D", out _));
            else Assert.Equal(expected, id);
        }
        finally { Directory.Delete(directory, true); }
    }
}
