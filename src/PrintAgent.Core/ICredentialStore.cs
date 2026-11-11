using PrintAgent.Common.Models;

namespace PrintAgent.Core;

// The host supplies the Linux credential store. No plaintext/DPAPI fallback is provided.
public interface ICredentialStore
{
    AuthData? Load();
    void Save(AuthData auth);
    void Clear();
}
