using PrintAgent.Common.Models;
using PrintAgent.Core;
using PrintAgent.Linux;

namespace PrintAgent.Service;

public sealed class AgentLogBuffer : IDisposable
{
    private readonly CloudSession _session;
    private readonly CloudConnection _connection;
    private readonly LinuxPrintEngine _printing;
    private readonly object _gate = new();
    private readonly List<LogEntry> _logs = [];
    public AgentLogBuffer(CloudSession session, CloudConnection connection, LinuxPrintEngine printing)
    {
        _session = session; _connection = connection; _printing = printing;
        session.LogAdded += Add; connection.LogAdded += Add; printing.LogAdded += Add;
    }
    private void Add(LogEntry entry)
    {
        var safe = new LogEntry { Timestamp = entry.Timestamp, Level = PrintAgent.Common.Security.SafeText.Limit(entry.Level, 16),
            Message = PrintAgent.Common.Security.SafeText.Limit(entry.Message), JobId = PrintAgent.Common.Security.SafeText.Limit(entry.JobId, 128) };
        lock (_gate) { _logs.Insert(0, safe); if (_logs.Count > 200) _logs.RemoveAt(_logs.Count - 1); }
    }
    public List<LogEntry> Snapshot() { lock (_gate) return _logs.ToList(); }
    public void Dispose() { _session.LogAdded -= Add; _connection.LogAdded -= Add; _printing.LogAdded -= Add; }
}
