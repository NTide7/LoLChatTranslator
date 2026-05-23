using LoLChatTranslator.Models;

namespace LoLChatTranslator.Services;

public enum TranslationKind
{
    Local,
    External
}

public sealed record TranslationJob(
    CleanedChatMessage Message,
    int SourceOrder,
    string SourceText,
    string NormalizedText,
    TranslationKind Kind,
    int BatchIndex,
    NormalizedMessage NormalizedMessage);

public sealed record TranslationResult(
    TranslationJob Job,
    bool Success,
    string? OutputText,
    string? ErrorKind,
    bool ShouldCommitDedup,
    string? RawOutputText = null);

public sealed class TranslationBatchService
{
    private readonly AppConfig _config;
    private readonly MessageNormalizer _messageNormalizer;
    private readonly Func<TranslationJob, CancellationToken, Task<string>> _externalTranslator;

    public TranslationBatchService(
        AppConfig config,
        MessageNormalizer messageNormalizer,
        TranslateService translateService)
        : this(
            config,
            messageNormalizer,
            (job, token) => translateService.TranslateAsync(
                job.NormalizedText,
                config.TranslateConfig.TargetLanguage,
                config.TranslateConfig.SourceLanguage,
                token))
    {
    }

    public TranslationBatchService(
        AppConfig config,
        MessageNormalizer messageNormalizer,
        Func<TranslationJob, CancellationToken, Task<string>> externalTranslator)
    {
        _config = config;
        _messageNormalizer = messageNormalizer;
        _externalTranslator = externalTranslator;
    }

    public List<TranslationJob> BuildJobs(IEnumerable<CleanedChatMessage> messages)
    {
        return messages
            .OrderBy(message => message.SourceOrder)
            .ThenBy(message => message.SourceTop ?? double.MaxValue)
            .ThenBy(message => message.SourceLeft ?? double.MaxValue)
            .ThenBy(message => message.SourceRawLineIndex)
            .Select((message, index) =>
            {
                var normalized = _messageNormalizer.Normalize(
                    message.Message,
                    _config.TranslateConfig.ToxicDisplayMode,
                    _config.TranslateConfig.TargetLanguage);
                return new TranslationJob(
                    message,
                    message.SourceOrder,
                    message.Message,
                    normalized.NormalizedText,
                    normalized.ShouldBypassTranslator ? TranslationKind.Local : TranslationKind.External,
                    index,
                    normalized);
            })
            .ToList();
    }

    public async Task<List<TranslationResult>> TranslateAsync(
        IReadOnlyList<TranslationJob> jobs,
        CancellationToken cancellationToken)
    {
        if (jobs.Count == 0)
        {
            return [];
        }

        using var externalLimiter = new SemaphoreSlim(ResolveExternalConcurrency(), ResolveExternalConcurrency());
        var tasks = jobs.Select(job => TranslateOneAsync(job, externalLimiter, cancellationToken)).ToArray();
        var results = await Task.WhenAll(tasks);
        return results
            .OrderBy(result => result.Job.SourceOrder)
            .ThenBy(result => result.Job.Message.Timestamp ?? string.Empty, StringComparer.Ordinal)
            .ThenBy(result => result.Job.BatchIndex)
            .ToList();
    }

    private async Task<TranslationResult> TranslateOneAsync(
        TranslationJob job,
        SemaphoreSlim externalLimiter,
        CancellationToken cancellationToken)
    {
        if (ChatCleaner.IsInvalidMessage(job.NormalizedText))
        {
            return new TranslationResult(job, false, null, "no_valid_chat", ShouldCommitDedup: false);
        }

        try
        {
            var translatedText = job.NormalizedMessage.ShouldBypassTranslator
                ? job.NormalizedMessage.DirectTranslation!
                : await TranslateExternalAsync(job, externalLimiter, cancellationToken);

            if (TranslatorErrorSanitizer.IsErrorResult(translatedText))
            {
                return new TranslationResult(job, false, null, "translation_failed", ShouldCommitDedup: false, translatedText);
            }

            if (!TranslationOutputValidator.TryBuildDisplayTranslation(
                    job.NormalizedText,
                    translatedText,
                    _config.TranslateConfig.TargetLanguage,
                    job.NormalizedMessage.IsTrustedDirectOutput,
                    out var displayTranslation))
            {
                return new TranslationResult(job, false, null, "untranslated_output", ShouldCommitDedup: false, translatedText);
            }

            return new TranslationResult(job, true, displayTranslation, null, ShouldCommitDedup: true, translatedText);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new TranslationResult(job, false, null, $"translation_failed:{ex.GetType().Name}", ShouldCommitDedup: false, ex.Message);
        }
    }

    private async Task<string> TranslateExternalAsync(
        TranslationJob job,
        SemaphoreSlim externalLimiter,
        CancellationToken cancellationToken)
    {
        await externalLimiter.WaitAsync(cancellationToken);
        try
        {
            return await _externalTranslator(job, cancellationToken);
        }
        finally
        {
            externalLimiter.Release();
        }
    }

    private int ResolveExternalConcurrency()
    {
        var engine = TranslatorEngines.Normalize(_config.TranslateConfig.TranslateEngine);
        if (TranslatorEngines.IsMyMemoryFreeTranslator(engine))
        {
            return 1;
        }

        return engine is TranslatorEngines.AiApi or TranslatorEngines.Ollama
            ? 3
            : 2;
    }
}
