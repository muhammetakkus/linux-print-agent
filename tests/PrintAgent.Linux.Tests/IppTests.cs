using System.Buffers.Binary;
using PrintAgent.Linux;
using Xunit;

namespace PrintAgent.Linux.Tests;

public class IppTests
{
    [Fact]
    public void Decode_preserves_repeated_printer_groups_and_unknown_values()
    {
        // IPP 2.0, successful-ok, request 1, two printer groups, UTF-8 names.
        var bytes = Convert.FromHexString("02000000000000010442000C7072696E7465722D6E616D65000375736223000D7072696E7465722D73746174650004000000030442000C7072696E7465722D6E616D6500036E657403");
        var result = IppCodec.Decode(bytes);
        Assert.Equal(2, result.Groups.Count);
        Assert.Equal("usb", result.Groups[0].Text("printer-name"));
        Assert.Equal(3, result.Groups[0].Integer("printer-state"));
        Assert.Null(result.Groups[1].Integer("printer-state"));
        Assert.Equal("net", result.Groups[1].Text("printer-name"));
    }

    [Fact]
    public void Request_starts_with_correct_operation_and_mandatory_attributes()
    {
        var request = IppCodec.Encode(0x4002, 7, [IppAttribute.Text(0x44, "requested-attributes", "printer-name")]);
        Assert.Equal("020040020000000701", Convert.ToHexString(request[..9]));
        Assert.Contains("attributes-charset", System.Text.Encoding.UTF8.GetString(request));
        Assert.Contains("utf-8", System.Text.Encoding.UTF8.GetString(request));
        Assert.Equal(3, request[^1]);
    }

    [Fact]
    public void Truncated_response_is_not_interpreted_as_empty_success()
    {
        Assert.Throws<InvalidDataException>(() => IppCodec.Decode([2, 0, 0]));
        Assert.Throws<InvalidDataException>(() => IppCodec.Decode(Convert.FromHexString("02000000000000010442000C7072")));
    }
}
