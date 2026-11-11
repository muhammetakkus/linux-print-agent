namespace PrintAgent.Common.Models;

public class AuthData
{
    /// <summary>
    /// Long-lived, opaque agent session token ("ag_…"). This — not the access
    /// token — is what keeps the machine signed in: it has no expiry and
    /// survives reboots and outages. Only an explicit sign-out here or a revoke
    /// from the panel ends it.
    /// </summary>
    public string SessionToken { get; set; } = string.Empty;

    /// <summary>
    /// Short-lived JWT used for the socket handshake. Disposable: it is
    /// re-minted from <see cref="SessionToken"/> before every connect, so an
    /// expired one is never a reason to sign the user out.
    /// </summary>
    public string Token { get; set; } = string.Empty;

    public CustomerInfo? Customer { get; set; }

    /// <summary>Credentials stored by agents before 1.9.0 carry only a JWT.</summary>
    public bool IsLegacy => string.IsNullOrEmpty(SessionToken) && !string.IsNullOrEmpty(Token);
}

public class CustomerInfo
{
    public string Id { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string ApiKey { get; set; } = string.Empty;
}

public class LoginRequest
{
    public string Email { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
}

public class LoginResponse
{
    public CustomerInfo? Customer { get; set; }
    public string Token { get; set; } = string.Empty;
}

/// <summary>Reply from /api/agent/session and /api/agent/session/adopt.</summary>
public class AgentSessionResponse
{
    public string SessionToken { get; set; } = string.Empty;
    public string AccessToken { get; set; } = string.Empty;
    public CustomerInfo? Customer { get; set; }
}

/// <summary>Reply from /api/agent/session/refresh.</summary>
public class AgentRefreshResponse
{
    public string AccessToken { get; set; } = string.Empty;
    public CustomerInfo? Customer { get; set; }
}
