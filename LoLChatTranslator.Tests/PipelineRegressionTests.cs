using LoLChatTranslator.Models;
using LoLChatTranslator.Services;
using System.Text.RegularExpressions;
using System.Windows;

namespace LoLChatTranslator.Tests;

public sealed class PipelineRegressionTests
{
    [Fact]
    public void PpOcrV5WorkerDoesNotReferenceKwargsBeforeInitialization()
    {
        var script = File.ReadAllText(FindRepoFile("LoLChatTranslator", "OCR", "ppocrv5_multilingual.py"));

        Assert.Contains("def build_engine_kwargs(det_model, rec_model, mode, lang=\"auto\"):", script);
        Assert.DoesNotContain("\"use_space_char\": kwargs.get(\"use_space_char\")", script);
        Assert.DoesNotContain("kwargs.get('use_space_char'", script);

        var callMatches = Regex.Matches(script, @"build_engine_kwargs\((?<args>[^)]*)\)");
        var calls = callMatches
            .Where(match => !match.Value.StartsWith("build_engine_kwargs(det_model", StringComparison.Ordinal))
            .ToArray();
        Assert.All(
            calls,
            match => Assert.True(
                match.Groups["args"].Value.Split(',').Length >= 4,
                $"Expected build_engine_kwargs call to pass lang: {match.Value}"));
    }

    [Theory]
    [InlineData("hello", "你好")]
    [InlineData("i am chinese", "我是中国人")]
    [InlineData("apple", "苹果")]
    [InlineData("i like apple", "我喜欢苹果")]
    [InlineData("banana", "香蕉")]
    [InlineData("i like banana", "我喜欢香蕉")]
    [InlineData("pls gank mid", "请来中路抓一下")]
    [InlineData("plsgankmid", "请来中路抓一下")]
    [InlineData("please gank mid", "请来中路抓一下")]
    [InlineData("gank mid pls", "请来中路抓一下")]
    public void BuiltInTranslationCoversCriticalShortPhrases(string input, string expected)
    {
        Assert.True(OcrTextFixer.TryTranslateBuiltInPhrase(input, "zh-Hans", out var translated));
        Assert.Equal(expected, translated);
    }

    [Theory]
    [InlineData("plsgankmid", "pls gank mid")]
    [InlineData("okilikechinesetoo", "ok i like chinese too")]
    [InlineData("ilikebanana", "i like banana")]
    public void EnglishGlueFixerSplitsCommonOcrGluedText(string input, string expected)
    {
        Assert.Equal(expected, OcrTextFixer.ApplyBuiltInFixes(input));
    }

    [Fact]
    public void ChatDeduperDoesNotCommitUntilSuccess()
    {
        var deduper = new ChatDeduper(new ChannelAliasService());
        var message = BuildMessage("01:06 [队伍] Ntide07（惩戒之箭）: pls gank mid");

        var firstProbe = deduper.Probe(message);
        var retryBeforeSuccess = deduper.Probe(message);

        Assert.True(firstProbe.ShouldTranslate);
        Assert.True(retryBeforeSuccess.ShouldTranslate);

        deduper.CommitSuccess(message);
        var afterSuccess = deduper.Probe(message);

        Assert.False(afterSuccess.ShouldTranslate);
        Assert.Equal("duplicate_same_timestamp", afterSuccess.Reason);
    }

    [Fact]
    public void ChatDeduperHandlesTimestampAndNoTimestampVariantsAfterSuccess()
    {
        var deduper = new ChatDeduper(new ChannelAliasService());
        var timestamped = BuildMessage("00:45 [队伍] Ntide07（惩戒之箭）: hello");
        var noTimestamp = BuildMessage("[队伍] Ntide07（惩戒之箭）: hello");

        Assert.True(deduper.Probe(timestamped).ShouldTranslate);
        deduper.CommitSuccess(timestamped);

        var variant = deduper.Probe(noTimestamp);
        Assert.False(variant.ShouldTranslate);
        Assert.Equal("duplicate_timestamp_variant_within_ttl", variant.Reason);
    }

    [Theory]
    [InlineData("00:45 [队伍] Ntide07（惩戒之箭）: hello")]
    [InlineData("01:06 [队伍] Ntide07（惩戒之箭）: pls gank mid")]
    [InlineData("02:40 [队伍] Ntide07（惩戒之箭）: ok i like chinese too...")]
    [InlineData("01:06 [队人伍] Ntide07（惩戒之箭）: pls gank mid")]
    [InlineData("01:06 [所有人] Ntide07(惩戒之箭): hello")]
    [InlineData("01:06 [小队] Ntide07（惩戒之箭）：hello")]
    public void PlayerChatSamplesRemainValid(string raw)
    {
        var parsed = ChatDeduper.ParseChatLine(raw);
        var validation = ChatDeduper.IsValidPlayerChat(parsed, new ChannelAliasService());

        Assert.True(parsed.MatchedPlayerChatPattern);
        Assert.True(validation.Valid);
    }

    [Fact]
    public void LineMergerMergesWrappedEnglishWithBoundingBoxes()
    {
        var lines = new[]
        {
            "02:40 [队伍] Ntide07（惩戒之箭）: ok i like chinese too because this sentence is long and",
            "continues on the next visual line"
        };
        var textLines = new[]
        {
            new OcrTextLine { Text = lines[0], Confidence = 0.99, BoundingBox = new Rect(10, 10, 620, 24) },
            new OcrTextLine { Text = lines[1], Confidence = 0.99, BoundingBox = new Rect(20, 38, 380, 24) }
        };

        var merged = OcrLineContinuationMerger.Merge(lines, textLines);

        Assert.Single(merged.Lines);
        Assert.Contains("continues on the next visual line", merged.Lines[0]);
    }

    [Theory]
    [InlineData("23:20 Ntide07(惩戒之箭)守卫任务完成!")]
    [InlineData("23:20 你已临近训练模式自由的最大游戏时长")]
    [InlineData("S3 2O Ntide07(惩戒之箭)守卫任务完成!")]
    [InlineData("pls gank mid")]
    [InlineData("ff")]
    [InlineData("hello")]
    public void LineMergerDoesNotMergeSystemOrStandaloneLines(string secondLine)
    {
        var lines = new[]
        {
            "02:40 [队伍] Ntide07（惩戒之箭）: ok i like chinese too because this sentence is long and",
            secondLine
        };

        var merged = OcrLineContinuationMerger.Merge(lines);

        Assert.Equal(2, merged.Lines.Count);
    }

    [Fact]
    public void PlayerExclusionSkipsMatchingRiotId()
    {
        var config = new TranslateConfig
        {
            ExcludePlayersEnabled = true,
            ExcludedPlayers = [PlayerExclusionService.CreateEntry("Ntide07", "1234")]
        };
        var message = BuildMessage("00:45 [队伍] Ntide07#1234（惩戒之箭）: hello");

        var decision = PlayerExclusionService.IsPlayerExcluded(message, config);

        Assert.True(decision.Excluded);
        Assert.Equal("excluded_player_exact_riot_id", decision.Reason);
    }

    [Fact]
    public void ChatCleanerHandlesChannelOcrTypoAndColonVariants()
    {
        var cleaner = new ChatCleaner(new ChannelAliasService());
        var config = AppConfig.CreateDefault();
        var cleaned = cleaner.CleanMessage("01:06 [队人伍] Ntide07（惩戒之箭）：pls gank mid", config);

        Assert.NotNull(cleaned);
        Assert.Equal(ChatChannel.Team, cleaned.Channel);
        Assert.Equal("pls gank mid", cleaned.Message);
    }

    [Fact]
    public void ReadingOrderSortsRawOcrLinesByBoundingBox()
    {
        var raw = new[]
        {
            BuildOcrLine("apple", rawIndex: 0, x: 10, y: 10),
            BuildOcrLine("canada", rawIndex: 1, x: 10, y: 70),
            BuildOcrLine("banana", rawIndex: 2, x: 10, y: 40)
        };

        var sorted = ReadingOrderService.Sort(raw).Lines;

        Assert.Equal(["apple", "banana", "canada"], sorted.Select(line => line.Text).ToArray());
        Assert.Equal([0, 1, 2], sorted.Select(line => line.VisualOrder).ToArray());
        Assert.Equal([0, 2, 1], sorted.Select(line => line.RawIndex).ToArray());
    }

    [Fact]
    public void ReadingOrderSortsSameVisualLineFromLeftToRight()
    {
        var raw = new[]
        {
            BuildOcrLine("right", rawIndex: 0, x: 180, y: 10),
            BuildOcrLine("left", rawIndex: 1, x: 10, y: 12),
            BuildOcrLine("middle", rawIndex: 2, x: 90, y: 11)
        };

        var sorted = ReadingOrderService.Sort(raw).Lines;

        Assert.Equal(["left", "middle", "right"], sorted.Select(line => line.Text).ToArray());
    }

    [Fact]
    public void ReadingOrderKeepsRawOrderWhenBoundingBoxesAreMissing()
    {
        var raw = new[]
        {
            new OcrTextLine { Text = "apple", RawIndex = 0 },
            new OcrTextLine { Text = "canada", RawIndex = 1 },
            new OcrTextLine { Text = "banana", RawIndex = 2 }
        };

        var sorted = ReadingOrderService.Sort(raw);

        Assert.Equal("raw_fallback", sorted.Mode);
        Assert.Equal(["apple", "canada", "banana"], sorted.Lines.Select(line => line.Text).ToArray());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void CleanerPreservesVisualOrderAfterSystemMessagesAreFiltered(bool includeTimestamp)
    {
        var config = AppConfig.CreateDefault();
        var cleaner = new ChatCleaner(new ChannelAliasService());
        var textLines = new[]
        {
            BuildChatOcrLine("apple", 0, 10, includeTimestamp, "00:01"),
            BuildOcrLine("你已经选择了位置", rawIndex: 1, x: 10, y: 24),
            BuildChatOcrLine("canada", 2, 70, includeTimestamp, "00:03"),
            BuildChatOcrLine("banana", 3, 40, includeTimestamp, "00:02")
        };
        var sortedLines = ReadingOrderService.Sort(textLines).Lines;
        var merge = OcrLineContinuationMerger.Merge(sortedLines.Select(line => line.Text).ToList(), sortedLines);

        var cleaned = cleaner.CleanMessages(merge.MergedLines, config);

        Assert.Equal(["apple", "banana", "canada"], cleaned.Select(message => message.Message).ToArray());
        Assert.Equal([0, 3, 2], cleaned.Select(message => message.SourceRawLineIndex).ToArray());
    }

    [Fact]
    public async Task TranslationBatchReturnsResultsInSourceOrderNotCompletionOrder()
    {
        var config = AppConfig.CreateDefault();
        var normalizer = new MessageNormalizer();
        var delays = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["first external line"] = 80,
            ["second external line"] = 160,
            ["third external line"] = 10
        };
        var outputs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["first external line"] = "第一句",
            ["second external line"] = "第二句",
            ["third external line"] = "第三句"
        };
        var service = new TranslationBatchService(
            config,
            normalizer,
            async (job, token) =>
            {
                await Task.Delay(delays[job.NormalizedText], token);
                return outputs[job.NormalizedText];
            });
        var jobs = service.BuildJobs(
        [
            BuildMessage("00:01 [队伍] Ntide07（惩戒之箭）: first external line", sourceOrder: 0),
            BuildMessage("00:02 [队伍] Ntide07（惩戒之箭）: second external line", sourceOrder: 1),
            BuildMessage("00:03 [队伍] Ntide07（惩戒之箭）: third external line", sourceOrder: 2)
        ]);

        var results = await service.TranslateAsync(jobs, CancellationToken.None);

        Assert.All(results, result => Assert.True(result.Success));
        Assert.Equal(["第一句", "第二句", "第三句"], results.Select(result => result.OutputText).ToArray());
    }

    [Fact]
    public async Task TranslationBatchFailureDoesNotBlockOtherMessagesAndDoesNotCommit()
    {
        var service = new TranslationBatchService(
            AppConfig.CreateDefault(),
            new MessageNormalizer(),
            (job, _) => Task.FromResult(job.NormalizedText == "second external line" ? "second external line" : "可用翻译"));
        var jobs = service.BuildJobs(
        [
            BuildMessage("00:01 [队伍] Ntide07（惩戒之箭）: first external line", sourceOrder: 0),
            BuildMessage("00:02 [队伍] Ntide07（惩戒之箭）: second external line", sourceOrder: 1),
            BuildMessage("00:03 [队伍] Ntide07（惩戒之箭）: third external line", sourceOrder: 2)
        ]);

        var results = await service.TranslateAsync(jobs, CancellationToken.None);

        Assert.Equal([true, false, true], results.Select(result => result.Success).ToArray());
        Assert.Equal("untranslated_output", results[1].ErrorKind);
        Assert.False(results[1].ShouldCommitDedup);
    }

    [Fact]
    public void DuplicateMessageDoesNotPreventSameFrameCandidates()
    {
        var deduper = new ChatDeduper(new ChannelAliasService());
        var duplicate = BuildMessage("00:01 [队伍] Ntide07（惩戒之箭）: hello", sourceOrder: 0);
        deduper.CommitSuccess(duplicate);
        var messages = new[]
        {
            duplicate,
            BuildMessage("00:02 [队伍] Ntide07（惩戒之箭）: apple", sourceOrder: 1),
            BuildMessage("00:03 [队伍] Ntide07（惩戒之箭）: banana", sourceOrder: 2)
        };

        var candidates = messages.Where(message => deduper.Probe(message).ShouldTranslate).ToList();

        Assert.Equal(["apple", "banana"], candidates.Select(message => message.Message).ToArray());
    }

    [Fact]
    public void RecommendedSettingsDisableExperimentalOcrWithoutClearingUserData()
    {
        var config = AppConfig.CreateDefault();
        config.OcrConfig.RegionX = 123;
        config.OcrConfig.RegionY = -200;
        config.OcrConfig.RegionWidth = 456;
        config.OcrConfig.RegionHeight = 111;
        config.TranslateConfig.ApiKey = "kept";
        config.TranslateConfig.ExcludedPlayers = [PlayerExclusionService.CreateEntry("Ntide07", "1234")];
        config.EnableAdvancedSettings = true;
        config.OcrConfig.EnableAdaptiveDirtyRegionOcr = true;
        config.OcrConfig.EnableTextMaskDetection = true;
        config.OcrConfig.EnableFixedBottomOcr = true;
        config.OcrConfig.ImageScale = 2;
        config.OcrConfig.Contrast = 2;
        config.OcrConfig.EnableSharpen = true;

        SettingsProfileService.ApplyRecommendedDefaults(config);

        Assert.False(config.EnableAdvancedSettings);
        Assert.False(config.OcrConfig.EnableAdaptiveDirtyRegionOcr);
        Assert.False(config.OcrConfig.EnableTextMaskDetection);
        Assert.False(config.OcrConfig.EnableFixedBottomOcr);
        Assert.Equal(1.0, config.OcrConfig.ImageScale);
        Assert.Equal(1.0, config.OcrConfig.Contrast);
        Assert.False(config.OcrConfig.EnableSharpen);
        Assert.Equal("kept", config.TranslateConfig.ApiKey);
        Assert.Single(config.TranslateConfig.ExcludedPlayers);
        Assert.Equal(123, config.OcrConfig.RegionX);
        Assert.Equal(-200, config.OcrConfig.RegionY);
        Assert.Equal(456, config.OcrConfig.RegionWidth);
        Assert.Equal(111, config.OcrConfig.RegionHeight);
    }

    [Fact]
    public void DefaultAndRecommendedCaptureIntervalIsTwelveHundredMs()
    {
        var config = AppConfig.CreateDefault();

        Assert.Equal(1200, config.OcrConfig.CaptureIntervalMs);

        config.OcrConfig.CaptureIntervalMs = 650;
        SettingsProfileService.ApplyRecommendedDefaults(config);

        Assert.Equal(1200, config.OcrConfig.CaptureIntervalMs);
    }

    [Fact]
    public void OcrEnvironmentDirectoryCanMoveManagedPythonAndVenvOffDefaultDrive()
    {
        var root = Path.Combine(Path.GetTempPath(), "LoLChatTranslator OCR Env");
        var config = new OcrConfig { OcrEnvironmentDirectory = root };
        var fullRoot = Path.GetFullPath(root);

        Assert.Equal(fullRoot, PythonEnvironmentService.ResolveOcrEnvironmentDirectory(config));
        Assert.Equal(Path.Combine(fullRoot, "Python311"), PythonEnvironmentService.ResolveManagedPythonDirectory(config));
        Assert.Equal(Path.Combine(fullRoot, "ocr_env"), PythonEnvironmentService.ResolveOcrVenvDirectory(config));
        Assert.Equal(Path.Combine(fullRoot, "ocr_env", "Scripts", "python.exe"), PythonEnvironmentService.ResolveOcrVenvPythonPath(config));
    }

    [Fact]
    public void DefaultOcrEnvironmentDirectoryIsApplicationFolder()
    {
        var expected = PythonEnvironmentService.NormalizeOcrEnvironmentDirectory(AppContext.BaseDirectory);

        Assert.Equal(expected, PythonEnvironmentService.ResolveOcrEnvironmentDirectory(new OcrConfig()));
    }

    [Theory]
    [InlineData("OpenAICompatible")]
    [InlineData("OpenAI Compatible")]
    [InlineData("DeepSeekPreset")]
    [InlineData("Gemini")]
    [InlineData("AI API")]
    public void LegacyAiTranslatorNamesNormalizeToAiApi(string engine)
    {
        Assert.Equal(TranslatorEngines.AiApi, TranslatorEngines.Normalize(engine));
        Assert.True(TranslatorEngines.UsesApiSettings(engine));
        Assert.True(TranslatorEngines.RequiresApiKey(engine));
    }

    [Fact]
    public void OllamaIsLocalApiEngineAndDoesNotRequireApiKey()
    {
        var config = new TranslateConfig { TranslateEngine = TranslatorEngines.Ollama };

        Assert.Equal(TranslatorEngines.Ollama, TranslatorEngines.Normalize("Ollama"));
        Assert.True(TranslatorEngines.UsesApiSettings(config.TranslateEngine));
        Assert.False(TranslatorEngines.RequiresApiKey(config.TranslateEngine));
        Assert.Equal(TranslatorEngines.OllamaDefaultApiBase, TranslatorEngines.ResolveApiBase(config));
        Assert.Equal(TranslatorEngines.OllamaDefaultModel, TranslatorEngines.ResolveModel(config));
    }

    [Fact]
    public void PendingMessageStabilizerHoldsThenReleasesLongStableMessage()
    {
        var stabilizer = new PendingMessageStabilizer();
        var message = BuildMessage("02:40 [队伍] Ntide07（惩戒之箭）: ok i like chinese too because this is a long wrapped chat line");

        var first = stabilizer.GetStableMessages([message], forceFlushPending: false, allowImmediateLongMessages: false, "auto", 250);
        Thread.Sleep(275);
        var second = stabilizer.GetStableMessages([message], forceFlushPending: false, allowImmediateLongMessages: false, "auto", 250);

        Assert.Empty(first);
        Assert.Single(second);
    }

    [Fact]
    public void PendingMessageStabilizerReturnsReadyMessagesBySourceOrder()
    {
        var stabilizer = new PendingMessageStabilizer();
        var ready = stabilizer.GetStableMessages(
        [
            BuildMessage("00:03 [队伍] Ntide07（惩戒之箭）: canada", sourceOrder: 2),
            BuildMessage("00:01 [队伍] Ntide07（惩戒之箭）: apple", sourceOrder: 0),
            BuildMessage("00:02 [队伍] Ntide07（惩戒之箭）: banana", sourceOrder: 1)
        ], forceFlushPending: true, allowImmediateLongMessages: true, "auto", 250);

        Assert.Equal(["apple", "banana", "canada"], ready.Select(message => message.Message).ToArray());
    }

    [Theory]
    [InlineData("23:20 Ntide07(惩戒之箭)守卫任务完成!")]
    [InlineData("23:20 你已临近训练模式自由的最大游戏时长")]
    [InlineData("已经选择了位置")]
    [InlineData("Ntide07 已经击杀了敌方英雄")]
    public void ChatCleanerFiltersKnownSystemMessages(string raw)
    {
        var cleaner = new ChatCleaner(new ChannelAliasService());

        var cleaned = cleaner.CleanMessage(raw, AppConfig.CreateDefault());

        Assert.Null(cleaned);
    }

    [Fact]
    public void NormalizerKeepsFfAndNmslAsLocalGameIntentOrToxicLabels()
    {
        var normalizer = new MessageNormalizer();

        var ff = normalizer.Normalize("ff", "label", "zh-Hans");
        var nmsl = normalizer.Normalize("nmsl", "label", "zh-Hans");

        Assert.Contains("投降", ff.DirectTranslation);
        Assert.DoesNotContain("逼迫", ff.DirectTranslation ?? string.Empty);
        Assert.Contains("严重辱骂", nmsl.DirectTranslation);
        Assert.True(nmsl.ShouldBypassTranslator);
        Assert.True(nmsl.IsTrustedDirectOutput);
    }

    [Theory]
    [InlineData("label", "[严重辱骂：家人攻击]", "toxic_label")]
    [InlineData("literal", "你妈死了", "toxic_literal")]
    [InlineData("source", "nmsl", "toxic_source")]
    [InlineData("hide", "[严重辱骂]", "toxic_hide")]
    public void ToxicDisplayModeControlsTrustedLocalOutput(string mode, string expected, string expectedKind)
    {
        var normalized = new MessageNormalizer().Normalize("nmsl", mode, "zh-Hans");

        Assert.True(normalized.ShouldBypassTranslator);
        Assert.True(normalized.IsTrustedDirectOutput);
        Assert.Equal(expectedKind, normalized.DirectOutputKind);
        Assert.Equal(expected, normalized.DirectTranslation);
        Assert.True(TranslationOutputValidator.TryBuildDisplayTranslation(
            normalized.NormalizedText,
            normalized.DirectTranslation!,
            "zh-Hans",
            normalized.IsTrustedDirectOutput,
            out var display));
        Assert.Equal(expected, display);
    }

    [Theory]
    [InlineData("nmsl", "literal", "zh-Hans", "你妈死了")]
    [InlineData("你妈死了", "literal", "zh-Hans", "你妈死了")]
    [InlineData("你媽死了", "literal", "zh-Hant", "你媽死了")]
    [InlineData("kys", "literal", "zh-Hans", "去死")]
    [InlineData("trash", "literal", "zh-Hans", "垃圾")]
    public void ToxicLiteralModeUsesDedicatedLiteralOutput(string input, string mode, string targetLanguage, string expected)
    {
        var normalized = new MessageNormalizer().Normalize(input, mode, targetLanguage);

        Assert.True(normalized.IsTrustedDirectOutput);
        Assert.Equal(expected, normalized.DirectTranslation);
    }

    [Fact]
    public void TrustedSourceModeCanDisplayOriginalOcrForChineseTarget()
    {
        var normalized = new MessageNormalizer().Normalize("nmsl", "source", "zh-Hans");

        var accepted = TranslationOutputValidator.TryBuildDisplayTranslation(
            normalized.NormalizedText,
            normalized.DirectTranslation!,
            "zh-Hans",
            normalized.IsTrustedDirectOutput,
            out var display);

        Assert.True(accepted);
        Assert.Equal("nmsl", display);
    }

    [Fact]
    public void OrdinaryExternalUntranslatedOutputStillGetsFiltered()
    {
        var accepted = TranslationOutputValidator.TryBuildDisplayTranslation(
            "apple",
            "apple",
            "zh-Hans",
            allowTrustedDirectOutput: false,
            out var display);

        Assert.False(accepted);
        Assert.Equal(string.Empty, display);
    }

    [Fact]
    public async Task ToxicLocalOutputDoesNotCallExternalTranslator()
    {
        var calls = 0;
        var service = new TranslationBatchService(
            AppConfig.CreateDefault(),
            new MessageNormalizer(),
            (_, _) =>
            {
                calls++;
                return Task.FromResult("should not be used");
            });
        var jobs = service.BuildJobs([BuildMessage("00:01 [队伍] Ntide07（惩戒之箭）: nmsl", sourceOrder: 0)]);

        var results = await service.TranslateAsync(jobs, CancellationToken.None);

        Assert.Equal(0, calls);
        Assert.Single(results);
        Assert.True(results[0].Success);
        Assert.Equal("[严重辱骂：家人攻击]", results[0].OutputText);
    }

    [Fact]
    public void GankIntentStillBypassesUntranslatedFilter()
    {
        var normalized = new MessageNormalizer().Normalize("pls gank mid", "label", "zh-Hans");

        Assert.True(normalized.IsTrustedDirectOutput);
        Assert.Equal("请来中路抓一下", normalized.DirectTranslation);
        Assert.True(TranslationOutputValidator.TryBuildDisplayTranslation(
            normalized.NormalizedText,
            normalized.DirectTranslation!,
            "zh-Hans",
            normalized.IsTrustedDirectOutput,
            out var display));
        Assert.Equal("请来中路抓一下", display);
    }

    [Theory]
    [InlineData("ilikeapple", "i like apple")]
    [InlineData("doyoulikechina", "do you like China")]
    [InlineData("chinesekongfu", "Chinese kung fu")]
    public void TranslationInputNormalizerRestoresCommonGlue(string input, string expected)
    {
        Assert.Equal(expected, TranslationInputNormalizer.NormalizeForTranslation(input));
    }

    [Fact]
    public async Task AutoOcrCoordinatorInvalidatesOldSessionOnStop()
    {
        using var coordinator = new AutoOcrCoordinator();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var session = coordinator.Start(async (_, token) =>
        {
            entered.SetResult();
            await release.Task.WaitAsync(token);
        });

        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var stop = await coordinator.StopAsync(TimeSpan.FromMilliseconds(100));

        Assert.True(stop.PreviousTask is not null);
        Assert.False(coordinator.IsCurrent(session.Generation));
        release.TrySetResult();
    }

    private static CleanedChatMessage BuildMessage(string raw, int sourceOrder = int.MaxValue)
    {
        var parsed = ChatDeduper.ParseChatLine(raw);
        return new CleanedChatMessage
        {
            RawLine = raw,
            Timestamp = parsed.Timestamp,
            Channel = parsed.Channel switch
            {
                "队伍" or "隊伍" => ChatChannel.Team,
                "所有人" or "所有" => ChatChannel.All,
                "小队" or "小隊" => ChatChannel.Party,
                _ => ChatChannel.Unknown
            },
            RawChannelText = parsed.Channel,
            OcrPlayerName = parsed.Sender,
            OcrChampionText = parsed.Champion,
            RawMessageBody = parsed.RawMessageBody,
            Message = parsed.Message,
            SourceOrder = sourceOrder
        };
    }

    private static OcrTextLine BuildOcrLine(string text, int rawIndex, double x, double y)
    {
        return new OcrTextLine
        {
            Text = text,
            Confidence = 0.99,
            RawIndex = rawIndex,
            BoundingBox = new Rect(x, y, 80, 20)
        };
    }

    private static OcrTextLine BuildChatOcrLine(
        string message,
        int rawIndex,
        double y,
        bool includeTimestamp,
        string timestamp)
    {
        var prefix = includeTimestamp ? $"{timestamp} " : string.Empty;
        return BuildOcrLine($"{prefix}[队伍] Ntide07（惩戒之箭）: {message}", rawIndex, 10, y);
    }

    private static string FindRepoFile(params string[] relativeParts)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(new[] { directory.FullName }.Concat(relativeParts).ToArray());
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException($"Unable to locate {Path.Combine(relativeParts)} from {AppContext.BaseDirectory}");
    }
}
