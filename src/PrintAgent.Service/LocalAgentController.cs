using System.Net;
using PrintAgent.Common.Local;
using PrintAgent.Common.Models;
using PrintAgent.Core;
using PrintAgent.Linux;

namespace PrintAgent.Service;

public sealed class LocalAgentController(CloudSession session, CloudConnection connection, CloudRuntime runtime,
    PrinterCatalog catalog, LinuxPrintEngine printing, AgentLogBuffer logs)
{
    private readonly SemaphoreSlim _userAction = new(1, 1);

    public async Task<LocalResponse> ExecuteAsync(LocalRequest request, CancellationToken ct = default)
    {
        if (request.Version != 1) return new() { Success = false, Error = "Unsupported local protocol version." };
        if (request.Offset < 0 || request.PageSize is < 1 or > 100 || request.Email?.Length > 320 ||
            request.Password?.Length > 1024 || request.PrinterName?.Length > 256 || request.Method?.Length > 32)
            return new() { Success = false, Error = "Local request exceeds allowed limits." };
        switch (request.Method)
        {
            case "status": case "printers": case "history": case "connection": case "diagnostics":
                return new() { State = Snapshot(request.Offset, request.PageSize) };
            case "version": return new() { Version = await session.FetchVersionInfoAsync() };
            case "clear-jobs": printing.ClearCompletedJobs(); return new() { State = Snapshot() };
            case "test-print": return new() { PrintResult = await printing.PrintAsync(TestJob(request.PrinterName ?? ""), ct) };
            case "login": case "logout":
                await _userAction.WaitAsync(ct);
                try
                {
                    if (request.Method == "logout")
                    {
                        await runtime.StopAsync();
                        await session.LogoutAsync();
                        runtime.Resume();
                        return new() { State = Snapshot() };
                    }
                    if (session.IsAuthenticated)
                        return new() { Success = false, Error = "Already signed in. Sign out before signing in again." };
                    if (string.IsNullOrWhiteSpace(request.Email) || string.IsNullOrWhiteSpace(request.Password))
                        return new() { Success = false, Error = "Email and password required" };
                    try
                    {
                        var result = await session.LoginAsync(request.Email, request.Password, ct);
                        if (string.IsNullOrEmpty(result?.SessionToken)) return new() { Success = false, Error = "Login failed: unexpected response" };
                        session.SetAuth(new AuthData { SessionToken = result.SessionToken, Token = result.AccessToken, Customer = result.Customer });
                        return new() { State = Snapshot() };
                    }
                    catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.Unauthorized)
                    { return new() { Success = false, Error = "Invalid email or password" }; }
                }
                finally { _userAction.Release(); }
            default: return new() { Success = false, Error = "Unknown local operation." };
        }
    }

    private AgentSnapshot Snapshot(int offset = 0, int pageSize = 100)
    {
        var jobs = printing.GetJobsSnapshot(); var entries = logs.Snapshot();
        return new()
        {
        Running = true, IsAuthenticated = session.IsAuthenticated, IsConnected = connection.IsConnected,
        DeviceId = session.DeviceId, DeviceName = session.DeviceName,
        Customer = session.Auth?.Customer, Printers = catalog.Printers.ToList(),
        DefaultPrinter = catalog.DefaultPrinter, Jobs = jobs.Skip(offset).Take(pageSize).ToList(), Logs = entries.Skip(offset).Take(pageSize).ToList(),
        TotalJobs = jobs.Count, TotalLogs = entries.Count
        };
    }

    private static PrintJobData TestJob(string printer) => new()
    {
        JobId = Guid.NewGuid().ToString(), PrinterName = printer, DocumentName = "test-print.pdf",
        DocumentData = TestPdf(), Copies = 1
    };

    private static string TestPdf()
    {
        // Same test document as the reference; this does not introduce a new cloud job type.
        var pdf = "%PDF-1.4\n1 0 obj<</Type/Catalog/Pages 2 0 R>>endobj\n2 0 obj<</Type/Pages/Kids[3 0 R]/Count 1>>endobj\n3 0 obj<</Type/Page/Parent 2 0 R/MediaBox[0 0 164 113]/Contents 4 0 R/Resources<</Font<</F1 5 0 R>>>>>>endobj\n4 0 obj<</Length 40>>stream\nBT /F1 14 Tf 50 50 Td (TEST) Tj ET\nendstream\nendobj\n5 0 obj<</Type/Font/Subtype/Type1/BaseFont/Helvetica>>endobj\nxref\n0 6\n0000000000 65535 f \n0000000009 00000 n \n0000000058 00000 n \n0000000115 00000 n \n0000000266 00000 n \n0000000360 00000 n \ntrailer<</Size 6/Root 1 0 R>>\nstartxref\n441\n%%EOF";
        return Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(pdf));
    }
}
