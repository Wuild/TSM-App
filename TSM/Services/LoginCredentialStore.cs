using ktsu.CredentialCache;
using ktsu.CredentialCache.Storage;
using ktsu.Semantics.Strings;
using CredentialCacheClient = ktsu.CredentialCache.CredentialCache;

namespace TSM.Services;

public sealed record SavedLogin(string Email, string Password);

public interface ILoginCredentialStore
{
    string StoreName { get; }
    SavedLogin? Load(string credentialKey);
    string Save(string? credentialKey, string email, string password);
    void Delete(string credentialKey);
}

public sealed class NativeLoginCredentialStore : ILoginCredentialStore
{
    private const string ServiceName = "TSM-CrossPlatform";
    private readonly ICredentialStore _store;

    public NativeLoginCredentialStore()
        : this(CredentialStoreFactory.CreateDefault(ServiceName))
    {
    }

    public NativeLoginCredentialStore(ICredentialStore store) => _store = store;

    public string StoreName => _store.Name;

    public SavedLogin? Load(string credentialKey)
    {
        if (!TryParseKey(credentialKey, out var persona))
            return null;

        using var cache = new CredentialCacheClient(_store);
        if (!cache.TryGet(persona, out var credential) ||
            credential is not CredentialWithUsernamePassword login)
            return null;

        return new SavedLogin(login.Username.ToString(), login.Password.ToString());
    }

    public string Save(string? credentialKey, string email, string password)
    {
        var persona = TryParseKey(credentialKey, out var existing)
            ? existing
            : CredentialCacheClient.CreatePersonaGUID();
        var credential = new CredentialWithUsernamePassword
        {
            Username = SemanticString<CredentialUsername>.Create(email),
            Password = SemanticString<CredentialPassword>.Create(password)
        };
        using var cache = new CredentialCacheClient(_store);
        cache.AddOrReplace(persona, credential);
        return persona.ToString();
    }

    public void Delete(string credentialKey)
    {
        if (!TryParseKey(credentialKey, out var persona))
            return;
        using var cache = new CredentialCacheClient(_store);
        cache.Remove(persona);
    }

    private static bool TryParseKey(string? value, out PersonaGUID persona)
    {
        if (!Guid.TryParse(value, out var parsed))
        {
            persona = null!;
            return false;
        }

        persona = SemanticString<PersonaGUID>.Create(parsed.ToString("D"));
        return true;
    }
}
