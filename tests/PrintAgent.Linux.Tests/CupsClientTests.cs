using System.Buffers.Binary;
using System.Net;
using System.Text;
using PrintAgent.Linux;
using Xunit;

namespace PrintAgent.Linux.Tests;

public class CupsClientTests
{
    [Fact]
    public async Task Discovery_default_and_queue_use_CUPS_IPP_operations_and_report_actual_data()
    {
        var operations = new List<int>();
        using var ipp = new IppClient(handler: new IppHandler((request, payload) =>
        {
            var operation = BinaryPrimitives.ReadUInt16BigEndian(payload.AsSpan(2));
            operations.Add(operation);
            Assert.Equal("application/ipp", request.Content!.Headers.ContentType!.MediaType);
            var id = BinaryPrimitives.ReadInt32BigEndian(payload.AsSpan(4));
            var reply = new IppWire(id);
            switch (operation)
            {
                case 0x4002:
                    Assert.Equal("/", request.RequestUri!.AbsolutePath);
                    reply.Group(4).Text("printer-name", "queue").Text("device-uri", "usb://Vendor/Device");
                    break;
                case 0x000b:
                    Assert.Equal("/printers/queue", request.RequestUri!.AbsolutePath);
                    reply.Group(4).Text("printer-name", "queue").Text("printer-make-and-model", "reported-model").Integer("printer-state", 4, 0x23).Boolean("printer-is-accepting-jobs", true).Text("media-supported", "iso_a4_210x297mm");
                    break;
                case 0x4001: reply.Group(4).Text("printer-name", "queue"); break;
                case 0x000a:
                    Assert.Contains("not-completed", Encoding.UTF8.GetString(payload));
                    reply.Group(2).Integer("job-id", 42).Text("job-name", "document").Integer("job-state", 5, 0x23);
                    break;
                default: throw new Xunit.Sdk.XunitException("Unexpected IPP operation");
            }
            return reply.Bytes();
        }));
        var cups = new CupsClient(ipp, new CapturingRunner());
        var snapshot = await cups.DiscoverAsync();
        var printer = Assert.Single(snapshot.Printers);
        Assert.Equal("USB", printer.ConnectionType);
        Assert.Equal("online", printer.Status);
        Assert.Equal("reported-model", printer.DriverName);
        Assert.Equal("queue", snapshot.DefaultPrinter);
        Assert.Equal(2100, printer.Capabilities!.Papers.Single().Width);
        var job = Assert.Single(await cups.GetQueueAsync("queue"));
        Assert.Equal(42, job.Id);
        Assert.Equal(5, job.State);
        Assert.Equal(new[] { 0x4002, 0x000b, 0x4001, 0x000a }, operations);
    }

    [Fact]
    public async Task Missing_default_is_null_and_CUPS_unavailability_is_not_empty_success()
    {
        using var ipp = new IppClient(handler: new IppHandler((_, payload) =>
        {
            var operation = BinaryPrimitives.ReadUInt16BigEndian(payload.AsSpan(2));
            return new IppWire(BinaryPrimitives.ReadInt32BigEndian(payload.AsSpan(4)), operation == 0x4001 ? (ushort)0x0406 : (ushort)0).Bytes();
        }));
        var cups = new CupsClient(ipp, new CapturingRunner());
        Assert.Null((await cups.DiscoverAsync()).DefaultPrinter);
        using var offline = new IppClient(handler: new IppHandler((_, _) => throw new HttpRequestException("CUPS unavailable")));
        await Assert.ThrowsAsync<HttpRequestException>(() => new CupsClient(offline, new CapturingRunner()).DiscoverAsync());
    }

    [Fact]
    public async Task Lp_submission_uses_argument_list_correct_copies_options_and_60_second_timeout()
    {
        using var ipp = new IppClient(socketPath: null, handler: new IppHandler((_, _) => throw new InvalidOperationException()));
        var runner = new CapturingRunner();
        var cups = new CupsClient(ipp, runner);
        await cups.SubmitAsync("queue", "a \"title\"", "/cache/file.pdf", ["sides=two-sided-long-edge", "media=A4,main"], 3, false);
        Assert.Equal("/usr/bin/lp", runner.Command);
        Assert.Equal(new[] { "-d", "queue", "-t", "a \"title\"", "-n", "3", "-o", "sides=two-sided-long-edge", "-o", "media=A4,main", "--", "/cache/file.pdf" }, runner.Arguments);
        Assert.Equal(TimeSpan.FromSeconds(60), runner.Timeout);
        Assert.Equal("C", runner.Environment!["LC_ALL"]);
        Assert.Equal("localhost:631", runner.Environment["CUPS_SERVER"]);
    }

    [Fact]
    public async Task Raw_submission_uses_a_bounded_timeout()
    {
        using var ipp = new IppClient(handler: new IppHandler((_, _) => throw new InvalidOperationException()));
        var runner = new CapturingRunner();
        await new CupsClient(ipp, runner).SubmitAsync("queue", "", "/cache/file.raw", ["ignored"], 1, true);
        Assert.Equal(new[] { "-d", "queue", "-t", "Nuxprint Raw Job", "-o", "raw", "--", "/cache/file.raw" }, runner.Arguments);
        Assert.Equal(TimeSpan.FromSeconds(60), runner.Timeout);
    }

    [Fact]
    public void Collection_dimensions_decode_using_IPP_hundredths_of_mm()
    {
        var wire = new IppWire(1).Group(4).Text("media-supported", "custom-stock");
        wire.Begin("media-col-database").Member("media-size-name").Value(0x44, Encoding.UTF8.GetBytes("custom-stock"))
            .Member("media-size").Value(0x34, []).Member("x-dimension").Value(0x21, IppWire.Number(21000))
            .Member("y-dimension").Value(0x21, IppWire.Number(29700)).End().End();
        var group = IppCodec.Decode(wire.Bytes()).Groups.Single();
        var paper = CupsPrinterMapper.Map(group).Capabilities!.Papers.Single();
        Assert.Equal(2100, paper.Width);
        Assert.Equal(2970, paper.Height);
    }
}

internal sealed class IppHandler(Func<HttpRequestMessage, byte[], byte[]> respond) : HttpMessageHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        new(HttpStatusCode.OK) { Content = new ByteArrayContent(respond(request, await request.Content!.ReadAsByteArrayAsync(cancellationToken))) };
}

internal sealed class CapturingRunner : ICommandRunner
{
    public string? Command { get; private set; }
    public IReadOnlyList<string>? Arguments { get; private set; }
    public IReadOnlyDictionary<string, string>? Environment { get; private set; }
    public TimeSpan? Timeout { get; private set; }
    public Task<CommandResult> RunAsync(string command, IReadOnlyList<string> arguments, IReadOnlyDictionary<string, string> environment, TimeSpan? timeout, CancellationToken ct = default)
    { Command = command; Arguments = arguments; Environment = environment; Timeout = timeout; return Task.FromResult(new CommandResult(0, "accepted", "")); }
}

// Independent IPP response fixture writer (does not use the production request encoder).
internal sealed class IppWire
{
    private readonly MemoryStream _stream = new();
    public IppWire(int id, ushort status = 0)
    {
        _stream.Write([2, 0]); Short(status); _stream.Write(Number(id));
    }
    public static byte[] Number(int value) { var bytes = new byte[4]; BinaryPrimitives.WriteInt32BigEndian(bytes, value); return bytes; }
    private void Short(ushort value) { var bytes = new byte[2]; BinaryPrimitives.WriteUInt16BigEndian(bytes, value); _stream.Write(bytes); }
    private IppWire Attribute(byte tag, string name, byte[] value)
    { _stream.WriteByte(tag); var key = Encoding.UTF8.GetBytes(name); Short((ushort)key.Length); _stream.Write(key); Short((ushort)value.Length); _stream.Write(value); return this; }
    public IppWire Group(byte tag) { _stream.WriteByte(tag); return this; }
    public IppWire Text(string name, string value) => Attribute(0x42, name, Encoding.UTF8.GetBytes(value));
    public IppWire Integer(string name, int value, byte tag = 0x21) => Attribute(tag, name, Number(value));
    public IppWire Boolean(string name, bool value) => Attribute(0x22, name, [value ? (byte)1 : (byte)0]);
    public IppWire Begin(string name) => Attribute(0x34, name, []);
    public IppWire Member(string name) => Attribute(0x4a, "", Encoding.UTF8.GetBytes(name));
    public IppWire Value(byte tag, byte[] value) => Attribute(tag, "", value);
    public IppWire End() => Attribute(0x37, "", []);
    public byte[] Bytes() => [.. _stream.ToArray(), 3];
}
