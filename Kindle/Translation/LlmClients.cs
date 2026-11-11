using TaskAgent.Services;

namespace TaskAgent.Kindle.Translation;

/// <summary>Abstraction minimale d'un modèle de langage : un prompt système + un message → une réponse texte.</summary>
public interface ILlmClient
{
    string Name { get; }
    Task<string> CompleteAsync(string system, string user, int maxTokens, CancellationToken ct);
}

/// <summary>
/// Branche le traducteur sur le « cerveau » de TaskAgent (abonnement Claude via Claude Code, API Anthropic,
/// ou modèle local type Qwen) : changer de modèle dans les réglages change aussi celui qui traduit.
/// </summary>
public sealed class ProviderLlmClient : ILlmClient
{
    private readonly ILlmProvider _provider;
    public ProviderLlmClient(ILlmProvider provider) => _provider = provider;
    public string Name => _provider.Name;

    /// <summary>Quelques nouvelles tentatives (surcharge, coupure réseau…) avant d'abandonner le lot.</summary>
    public async Task<string> CompleteAsync(string system, string user, int maxTokens, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return (await _provider.CompleteAsync(system, user, ct)).Text;
            }
            catch (Exception) when (!ct.IsCancellationRequested && attempt < 3)
            {
                await Task.Delay(TimeSpan.FromSeconds(5 * attempt), ct);
            }
        }
    }
}
