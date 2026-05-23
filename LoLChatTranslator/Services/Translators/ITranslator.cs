namespace LoLChatTranslator.Services;

public interface ITranslator
{
    Task<string> TranslateAsync(
        string text,
        string targetLanguage,
        string? sourceLanguage = null,
        CancellationToken cancellationToken = default);
}
