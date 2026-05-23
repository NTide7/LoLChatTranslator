using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace LoLChatTranslator.Services;

public sealed class GlossaryMatcher
{
    private const string SlangPackRelativePath = "Resources/lol_slang_v2.json";
    private const string DefaultToxicDisplayMode = "label";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly object _syncRoot = new();
    private GlossaryPack? _pack;
    private MatcherIndex? _index;

    public GlossaryMatchResult Match(
        string text,
        string? toxicDisplayMode = null,
        string? targetLanguage = null)
    {
        var original = text ?? string.Empty;
        var normalized = NormalizeForMatch(original);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return GlossaryMatchResult.None(original, normalized);
        }

        var index = LoadIndex();
        if (index is null)
        {
            return GlossaryMatchResult.None(original, normalized);
        }

        var targetCode = ToGlossaryLanguageCode(targetLanguage);
        var mode = NormalizeToxicDisplayMode(toxicDisplayMode);

        if (TryMatchSurrenderIntent(original, normalized, mode, out var surrenderMatch))
        {
            WriteDebugLog(surrenderMatch);
            return surrenderMatch;
        }

        // Match priority is intentional: toxic exact phrase > chat phrase > pattern > core term > alias.
        // Tactical gank requests are protected so an OCR "mid"->"int" fix cannot be overridden by toxic matching.
        if (!IsProtectedGankInstruction(normalized)
            && TryMatchToxic(index, original, normalized, mode, targetLanguage, out var toxicMatch))
        {
            WriteDebugLog(toxicMatch);
            return toxicMatch;
        }

        if (TryMatchCandidateList(index.ChatPhraseCandidates, index, original, normalized, targetCode, "chat_phrase_exact", 0.98, out var exactMatch))
        {
            WriteDebugLog(exactMatch);
            return exactMatch;
        }

        if (TryMatchPattern(index, original, normalized, targetCode, out var patternMatch))
        {
            WriteDebugLog(patternMatch);
            return patternMatch;
        }

        if (TryMatchCandidateList(index.CoreTermCandidates, index, original, normalized, targetCode, "core_term", 0.93, out var coreTermMatch))
        {
            WriteDebugLog(coreTermMatch);
            return coreTermMatch;
        }

        if (TryMatchAliasPattern(index, original, normalized, out var aliasMatch))
        {
            WriteDebugLog(aliasMatch);
            return aliasMatch;
        }

        if (TryMatchCandidateList(index.AliasCandidates, index, original, normalized, targetCode, "alias", 0.86, out var aliasExactMatch))
        {
            WriteDebugLog(aliasExactMatch);
            return aliasExactMatch;
        }

        return GlossaryMatchResult.None(original, normalized);
    }

    public static string NormalizeForMatch(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var normalized = value
            .Normalize(NormalizationForm.FormKC)
            .Replace('\u00A0', ' ')
            .Replace('\u200B', ' ')
            .Replace('\u200C', ' ')
            .Replace('\u200D', ' ')
            .Replace('\uFEFF', ' ')
            .Replace('：', ':')
            .Replace('；', ';')
            .Replace('，', ',')
            .Replace('。', '.')
            .Replace('？', '?')
            .Replace('！', '!')
            .Replace('（', '(')
            .Replace('）', ')')
            .Replace('【', '[')
            .Replace('】', ']')
            .Replace('、', ' ');

        normalized = Regex.Replace(normalized, @"[“”""'`´]+", string.Empty);
        normalized = Regex.Replace(normalized, @"[\[\]\(\){}<>]", " ");
        normalized = Regex.Replace(normalized, @"[,:;|/\\]+", " ");
        normalized = Regex.Replace(normalized, @"\s+", " ");
        normalized = normalized.Trim();
        normalized = Regex.Replace(normalized, @"[\?!\.,~\-_]+$", string.Empty).Trim();

        return normalized.ToLowerInvariant();
    }

    private MatcherIndex? LoadIndex()
    {
        lock (_syncRoot)
        {
            if (_index is not null)
            {
                return _index;
            }

            var pack = LoadPack();
            if (pack is null)
            {
                return null;
            }

            var index = new MatcherIndex
            {
                AliasMaps = BuildAliasMaps(pack),
                SlotDisplays = BuildSlotDisplays(pack)
            };

            foreach (var entry in pack.Entries ?? [])
            {
                if (IsToxicEntry(entry))
                {
                    foreach (var pattern in GetEntryTerms(entry, includeAliases: true))
                    {
                        AddCandidate(index.ToxicCandidates, pattern, entry);
                    }

                    continue;
                }

                if (IsChatPhraseCandidate(entry))
                {
                    foreach (var pattern in GetEntryTerms(entry, includeAliases: true))
                    {
                        AddCandidate(index.ChatPhraseCandidates, pattern, entry);
                    }
                }
                else if (IsCoreTermCandidate(entry))
                {
                    if (!string.IsNullOrWhiteSpace(entry.Term))
                    {
                        AddCandidate(index.CoreTermCandidates, entry.Term, entry);
                    }
                }
                else if (IsAliasCandidate(entry))
                {
                    foreach (var pattern in GetEntryTerms(entry, includeAliases: true))
                    {
                        AddCandidate(index.AliasCandidates, pattern, entry);
                    }
                }

                if (string.Equals(entry.Category, "chat_phrase_pattern", StringComparison.OrdinalIgnoreCase))
                {
                    foreach (var pattern in entry.Patterns ?? [])
                    {
                        var netPattern = ConvertPythonNamedGroups(pattern);
                        try
                        {
                            index.PatternCandidates.Add(new PatternCandidate(
                                entry,
                                pattern,
                                new Regex(netPattern, RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
                                entry.Priority));
                        }
                        catch (ArgumentException ex)
                        {
                            Trace.TraceWarning($"Glossary pattern skipped: {entry.Id ?? entry.Term}, {ex.Message}");
                        }
                    }
                }
            }

            index.ToxicCandidates = DeduplicateCandidates(index.ToxicCandidates)
                .OrderByDescending(candidate => candidate.NormalizedPattern.Length)
                .ToList();
            index.ChatPhraseCandidates = DeduplicateCandidates(index.ChatPhraseCandidates)
                .OrderByDescending(candidate => candidate.NormalizedPattern.Length)
                .ToList();
            index.CoreTermCandidates = DeduplicateCandidates(index.CoreTermCandidates)
                .OrderByDescending(candidate => candidate.NormalizedPattern.Length)
                .ToList();
            index.AliasCandidates = DeduplicateCandidates(index.AliasCandidates)
                .OrderByDescending(candidate => candidate.NormalizedPattern.Length)
                .ToList();
            index.PatternCandidates = index.PatternCandidates
                .OrderByDescending(candidate => candidate.Priority)
                .ThenByDescending(candidate => candidate.RawPattern.Length)
                .ToList();

            _index = index;
            return _index;
        }
    }

    private GlossaryPack? LoadPack()
    {
        if (_pack is not null)
        {
            return _pack;
        }

        var path = Path.Combine(AppContext.BaseDirectory, SlangPackRelativePath);
        if (!File.Exists(path))
        {
            Trace.TraceError($"LOL slang v2 pack not found: {path}");
            return null;
        }

        try
        {
            var json = File.ReadAllText(path);
            _pack = JsonSerializer.Deserialize<GlossaryPack>(json, JsonOptions);
            return _pack;
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            Trace.TraceError($"Failed to load LOL slang v2 pack: {ex.Message}");
            return null;
        }
    }

    private static void AddCandidate(List<MatchCandidate> candidates, string pattern, GlossaryEntry entry)
    {
        var normalizedPattern = NormalizeForMatch(pattern);
        if (string.IsNullOrWhiteSpace(normalizedPattern))
        {
            return;
        }

        candidates.Add(new MatchCandidate(pattern, normalizedPattern, entry));
    }

    private static IEnumerable<MatchCandidate> DeduplicateCandidates(IEnumerable<MatchCandidate> candidates)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var candidate in candidates)
        {
            if (seen.Add(candidate.NormalizedPattern))
            {
                yield return candidate;
            }
        }
    }

    private static bool TryMatchToxic(
        MatcherIndex index,
        string original,
        string normalized,
        string toxicDisplayMode,
        string? targetLanguage,
        out GlossaryMatchResult result)
    {
        var mode = NormalizeToxicDisplayMode(toxicDisplayMode);
        foreach (var candidate in index.ToxicCandidates)
        {
            if (!ContainsNormalizedTerm(normalized, candidate.NormalizedPattern))
            {
                continue;
            }

            var output = BuildToxicOutput(candidate.Entry, original, mode, targetLanguage);
            result = new GlossaryMatchResult
            {
                Matched = true,
                MatchLevel = "toxic",
                OriginalText = original,
                NormalizedText = normalized,
                OutputText = output,
                Confidence = 1.0,
                MatchedEntry = DescribeEntry(candidate.Entry, candidate.Pattern),
                DirectOutputKind = $"toxic_{mode}"
            };
            return true;
        }

        if (TryMatchBuiltinToxic(original, normalized, toxicDisplayMode, out result))
        {
            return true;
        }

        result = GlossaryMatchResult.None(original, normalized);
        return false;
    }

    private static bool TryMatchSurrenderIntent(
        string original,
        string normalized,
        string toxicDisplayMode,
        out GlossaryMatchResult result)
    {
        var compact = normalized.Replace(" ", string.Empty, StringComparison.Ordinal);
        var label = compact switch
        {
            "ff" or "/ff" or "surrender" => "[请求投降]",
            "ff15" => "[请求15分钟投降]",
            "ff20" => "[请求20分钟投降]",
            _ => null
        };

        if (label is null)
        {
            result = GlossaryMatchResult.None(original, normalized);
            return false;
        }

        result = new GlossaryMatchResult
        {
            Matched = true,
            MatchLevel = "game_intent",
            OriginalText = original,
            NormalizedText = normalized,
            OutputText = toxicDisplayMode == "source" ? original : label,
            Confidence = 1,
            MatchedEntry = "game_intent:surrender",
            DirectOutputKind = "game_intent"
        };
        return true;
    }

    private static bool TryMatchBuiltinToxic(
        string original,
        string normalized,
        string toxicDisplayMode,
        out GlossaryMatchResult result)
    {
        string? label = null;
        string? entry = null;
        var gameIntent = false;

        if (ContainsAny(normalized, "report", "x9", "举报", "檢舉", "檢舉", "신고"))
        {
            label = "[举报相关]";
            entry = "negative_builtin:report";
        }
        else if (ContainsAny(normalized, "afk", "挂机", "掛機", "탈주"))
        {
            label = "[负面行为指控：挂机/离开游戏]";
            entry = "negative_builtin:afk";
        }
        else if (ContainsAny(normalized, "open mid", "int", "feed", "送", "故意送", "摆烂", "擺爛"))
        {
            label = "[负面行为指控：摆烂/破坏游戏]";
            entry = "negative_builtin:griefing";
        }
        else if (ContainsAny(normalized, "ff", "ff 15", "surrender", "投降"))
        {
            label = "[请求投降]";
            entry = "game_intent:surrender";
            gameIntent = true;
        }

        if (label is null)
        {
            result = GlossaryMatchResult.None(original, normalized);
            return false;
        }

        var output = toxicDisplayMode switch
        {
            "hide" => gameIntent ? label : "[辱骂]",
            "source" => original,
            "literal" => label,
            _ => label
        };

        result = new GlossaryMatchResult
        {
            Matched = true,
            MatchLevel = gameIntent ? "game_intent" : "toxic",
            OriginalText = original,
            NormalizedText = normalized,
            OutputText = output,
            Confidence = gameIntent ? 0.98 : 0.92,
            MatchedEntry = entry,
            DirectOutputKind = gameIntent ? "game_intent" : $"toxic_{toxicDisplayMode}"
        };
        return true;
    }

    private static bool TryMatchCandidateList(
        IReadOnlyList<MatchCandidate> candidates,
        MatcherIndex index,
        string original,
        string normalized,
        string targetCode,
        string matchLevel,
        double confidence,
        out GlossaryMatchResult result)
    {
        foreach (var candidate in candidates)
        {
            if (!string.Equals(normalized, candidate.NormalizedPattern, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var output = BuildExactOutput(candidate.Entry, normalized, targetCode, index);
            if (string.IsNullOrWhiteSpace(output))
            {
                continue;
            }

            result = new GlossaryMatchResult
            {
                Matched = true,
                MatchLevel = matchLevel,
                OriginalText = original,
                NormalizedText = normalized,
                OutputText = output,
                Confidence = confidence,
                MatchedEntry = DescribeEntry(candidate.Entry, candidate.Pattern)
            };
            return true;
        }

        result = GlossaryMatchResult.None(original, normalized);
        return false;
    }

    private static bool TryMatchPattern(
        MatcherIndex index,
        string original,
        string normalized,
        string targetCode,
        out GlossaryMatchResult result)
    {
        foreach (var candidate in index.PatternCandidates)
        {
            var match = candidate.Regex.Match(normalized);
            if (!match.Success)
            {
                continue;
            }

            var slotValues = ResolveMatchSlots(index, candidate.Entry, match, targetCode);
            var output = BuildPatternOutput(candidate.Entry, normalized, targetCode, slotValues);
            if (string.IsNullOrWhiteSpace(output))
            {
                continue;
            }

            result = new GlossaryMatchResult
            {
                Matched = true,
                MatchLevel = "pattern",
                OriginalText = original,
                NormalizedText = normalized,
                OutputText = output,
                Confidence = 0.9,
                MatchedEntry = $"{DescribeEntry(candidate.Entry, candidate.RawPattern)}"
            };
            return true;
        }

        result = GlossaryMatchResult.None(original, normalized);
        return false;
    }

    private static bool TryMatchAliasPattern(
        MatcherIndex index,
        string original,
        string normalized,
        out GlossaryMatchResult result)
    {
        var hasLane = TryFindAlias(index, "lane", normalized, out var lane);
        var hasSpell = TryFindAlias(index, "summoner_or_spell", normalized, out var spell);
        var hasObjective = TryFindAlias(index, "objective", normalized, out var objective);
        var hasArea = TryFindAlias(index, "area", normalized, out var area);

        if (hasLane && hasSpell && HasNoSpellMarker(normalized))
        {
            var output = $"{GetLaneFullZh(index, lane)}没{GetSpellShortZh(spell)}";
            result = BuildAliasResult(original, normalized, output, $"alias:no_spell lane={lane} spell={spell}");
            return true;
        }

        if (hasLane && HasGankMarker(normalized))
        {
            var output = $"请来抓{GetLaneShortZh(lane)}";
            result = BuildAliasResult(original, normalized, output, $"alias:request_gank lane={lane}");
            return true;
        }

        var visionArea = hasArea ? area : objective;
        if (!string.IsNullOrWhiteSpace(visionArea) && HasClearVisionMarker(normalized))
        {
            var output = $"清{GetAreaZh(index, visionArea)}视野";
            result = BuildAliasResult(original, normalized, output, $"alias:clear_vision area={visionArea}");
            return true;
        }

        if (!string.IsNullOrWhiteSpace(visionArea) && HasWardMarker(normalized))
        {
            var output = $"做{GetAreaZh(index, visionArea)}视野";
            result = BuildAliasResult(original, normalized, output, $"alias:ward area={visionArea}");
            return true;
        }

        if (hasObjective && HasObjectiveMarker(normalized))
        {
            var output = $"去打{GetObjectiveZh(index, objective)}";
            result = BuildAliasResult(original, normalized, output, $"alias:objective objective={objective}");
            return true;
        }

        result = GlossaryMatchResult.None(original, normalized);
        return false;
    }

    private static GlossaryMatchResult BuildAliasResult(
        string original,
        string normalized,
        string output,
        string matchedEntry)
    {
        return new GlossaryMatchResult
        {
            Matched = true,
            MatchLevel = "alias",
            OriginalText = original,
            NormalizedText = normalized,
            OutputText = output,
            Confidence = 0.84,
            MatchedEntry = matchedEntry
        };
    }

    private static string? BuildExactOutput(
        GlossaryEntry entry,
        string normalized,
        string targetCode,
        MatcherIndex index)
    {
        var canonicalSlots = ResolveCanonicalSlots(index, entry, targetCode);

        if (targetCode.Equals("zh_CN", StringComparison.OrdinalIgnoreCase))
        {
            var special = BuildSpecialZhOutput(entry, normalized, index, canonicalSlots);
            if (!string.IsNullOrWhiteSpace(special))
            {
                return special;
            }
        }

        if (entry.Translation?.TryGetValue(targetCode, out var translated) == true
            && !string.IsNullOrWhiteSpace(translated))
        {
            return translated;
        }

        if (entry.TranslationTemplate?.TryGetValue(targetCode, out var template) == true
            && !string.IsNullOrWhiteSpace(template))
        {
            return RenderTemplate(template, canonicalSlots);
        }

        if (entry.Translation?.TryGetValue("zh_CN", out var zhCn) == true
            && !string.IsNullOrWhiteSpace(zhCn))
        {
            return zhCn;
        }

        return string.IsNullOrWhiteSpace(entry.MeaningZh) ? null : entry.MeaningZh;
    }

    private static string? BuildPatternOutput(
        GlossaryEntry entry,
        string normalized,
        string targetCode,
        IReadOnlyDictionary<string, ResolvedSlot> slotValues)
    {
        if (targetCode.Equals("zh_CN", StringComparison.OrdinalIgnoreCase))
        {
            var special = BuildSpecialZhOutput(entry, normalized, null, slotValues);
            if (!string.IsNullOrWhiteSpace(special))
            {
                return special;
            }
        }

        if (entry.TranslationTemplate?.TryGetValue(targetCode, out var template) == true
            && !string.IsNullOrWhiteSpace(template))
        {
            return RenderTemplate(template, slotValues);
        }

        if (entry.TranslationTemplate?.TryGetValue("zh_CN", out var zhTemplate) == true
            && !string.IsNullOrWhiteSpace(zhTemplate))
        {
            return RenderTemplate(zhTemplate, slotValues);
        }

        return string.IsNullOrWhiteSpace(entry.MeaningZh) ? null : entry.MeaningZh;
    }

    private static string? BuildSpecialZhOutput(
        GlossaryEntry entry,
        string normalized,
        MatcherIndex? index,
        IReadOnlyDictionary<string, ResolvedSlot> slots)
    {
        var concept = entry.PhraseIntent
            ?? entry.Canonical?.ConceptId
            ?? entry.Id
            ?? string.Empty;

        if (concept.Contains("request_gank_lane", StringComparison.OrdinalIgnoreCase)
            && TryGetSlot(slots, "lane", out var lane))
        {
            return $"请来抓{GetLaneShortZh(lane.Canonical)}";
        }

        if ((concept.Contains("no_spell", StringComparison.OrdinalIgnoreCase)
                || concept.Contains("enemy_no_spell", StringComparison.OrdinalIgnoreCase))
            && TryGetSlot(slots, "lane", out lane)
            && TryGetSlot(slots, "spell", out var spell))
        {
            var laneText = index is null
                ? GetLaneFullZh(lane.Canonical)
                : GetLaneFullZh(index, lane.Canonical);
            return $"{laneText}没{GetSpellShortZh(spell.Canonical)}";
        }

        if (concept.Contains("vision_call", StringComparison.OrdinalIgnoreCase)
            && (TryGetSlot(slots, "area", out var area) || TryGetSlot(slots, "objective", out area)))
        {
            var areaText = index is null
                ? GetObjectiveFallbackZh(area.Canonical)
                : GetAreaZh(index, area.Canonical);

            if (HasClearVisionMarker(normalized))
            {
                return $"清{areaText}视野";
            }

            return $"做{areaText}视野";
        }

        if ((concept.Contains("objective", StringComparison.OrdinalIgnoreCase)
                || concept.Contains("go", StringComparison.OrdinalIgnoreCase))
            && TryGetSlot(slots, "objective", out var objective))
        {
            var objectiveText = index is null
                ? GetObjectiveFallbackZh(objective.Canonical)
                : GetObjectiveZh(index, objective.Canonical);
            return $"去打{objectiveText}";
        }

        if (concept.Contains("lane_missing", StringComparison.OrdinalIgnoreCase)
            && TryGetSlot(slots, "lane", out lane))
        {
            var laneText = index is null
                ? GetLaneFullZh(lane.Canonical)
                : GetLaneFullZh(index, lane.Canonical);
            return $"{laneText}不见了，小心";
        }

        return null;
    }

    private static bool TryGetSlot(
        IReadOnlyDictionary<string, ResolvedSlot> slots,
        string name,
        out ResolvedSlot slot)
    {
        if (slots.TryGetValue(name, out slot!))
        {
            return true;
        }

        if (name.Equals("spell", StringComparison.OrdinalIgnoreCase)
            && slots.TryGetValue("summoner_or_spell", out slot!))
        {
            return true;
        }

        return false;
    }

    private static IReadOnlyDictionary<string, ResolvedSlot> ResolveCanonicalSlots(
        MatcherIndex index,
        GlossaryEntry entry,
        string targetCode)
    {
        var slots = new Dictionary<string, ResolvedSlot>(StringComparer.OrdinalIgnoreCase);

        foreach (var pair in entry.Canonical?.Slots ?? [])
        {
            var slotName = pair.Key;
            var category = ToSlotCategory(slotName);
            var canonical = NormalizeCanonical(pair.Value);
            slots[slotName] = ResolveSlot(index, slotName, category, canonical, targetCode);
        }

        return slots;
    }

    private static IReadOnlyDictionary<string, ResolvedSlot> ResolveMatchSlots(
        MatcherIndex index,
        GlossaryEntry entry,
        Match match,
        string targetCode)
    {
        var slots = new Dictionary<string, ResolvedSlot>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in match.Groups.Keys)
        {
            if (int.TryParse(name, NumberStyles.None, CultureInfo.InvariantCulture, out _))
            {
                continue;
            }

            var group = match.Groups[name];
            if (!group.Success || string.IsNullOrWhiteSpace(group.Value))
            {
                continue;
            }

            var category = ToSlotCategory(name);
            var canonical = ResolveAliasCanonical(index, category, group.Value);
            slots[name] = ResolveSlot(index, name, category, canonical, targetCode);
        }

        foreach (var slotName in entry.Slots ?? [])
        {
            if (slots.ContainsKey(slotName))
            {
                continue;
            }

            var category = ToSlotCategory(slotName);
            if (TryFindAlias(index, category, match.Value, out var canonical))
            {
                slots[slotName] = ResolveSlot(index, slotName, category, canonical, targetCode);
            }
        }

        return slots;
    }

    private static ResolvedSlot ResolveSlot(
        MatcherIndex index,
        string slotName,
        string category,
        string canonical,
        string targetCode)
    {
        var zhCn = ResolveSlotDisplay(index, category, canonical, "zh_CN")
            ?? GetObjectiveFallbackZh(canonical);
        var targetText = ResolveSlotDisplay(index, category, canonical, targetCode)
            ?? zhCn;

        return new ResolvedSlot(slotName, category, canonical, zhCn, targetText);
    }

    private static string RenderTemplate(
        string template,
        IReadOnlyDictionary<string, ResolvedSlot> slotValues)
    {
        var rendered = Regex.Replace(
            template,
            @"\{(?<name>[A-Za-z_]+)(?:\.(?<lang>[A-Za-z_]+))?\}",
            match =>
            {
                var name = match.Groups["name"].Value;
                if (name.Equals("action", StringComparison.OrdinalIgnoreCase))
                {
                    return match.Value;
                }

                if (!TryGetSlot(slotValues, name, out var slot))
                {
                    return match.Value;
                }

                var lang = match.Groups["lang"].Success
                    ? match.Groups["lang"].Value
                    : string.Empty;

                return lang.Equals("zh_CN", StringComparison.OrdinalIgnoreCase)
                    ? slot.ZhCn
                    : slot.TargetText;
            });

        return rendered.Replace("去打/拿", "去打");
    }

    private static Dictionary<string, Dictionary<string, string>> BuildAliasMaps(GlossaryPack pack)
    {
        var maps = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var category in pack.PhraseSlotAliases ?? [])
        {
            if (category.Value.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var map = GetOrCreateMap(maps, category.Key);
            foreach (var alias in category.Value.EnumerateObject())
            {
                if (alias.Value.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                var normalizedAlias = NormalizeForMatch(alias.Name);
                var canonical = NormalizeCanonical(alias.Value.GetString() ?? string.Empty);
                if (!string.IsNullOrWhiteSpace(normalizedAlias)
                    && !string.IsNullOrWhiteSpace(canonical))
                {
                    map[normalizedAlias] = canonical;
                }
            }
        }

        AddFallbackAliases(maps);
        return maps;
    }

    private static Dictionary<string, Dictionary<string, Dictionary<string, string>>> BuildSlotDisplays(GlossaryPack pack)
    {
        var displays = new Dictionary<string, Dictionary<string, Dictionary<string, string>>>(StringComparer.OrdinalIgnoreCase);

        foreach (var category in pack.PhraseSlots ?? [])
        {
            if (category.Value.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var categoryMap = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var canonicalSlot in category.Value.EnumerateObject())
            {
                if (canonicalSlot.Value.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var languageMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var languageValue in canonicalSlot.Value.EnumerateObject())
                {
                    if (languageValue.Value.ValueKind == JsonValueKind.String)
                    {
                        languageMap[languageValue.Name] = languageValue.Value.GetString() ?? string.Empty;
                    }
                }

                categoryMap[NormalizeCanonical(canonicalSlot.Name)] = languageMap;
            }

            displays[category.Key] = categoryMap;
        }

        return displays;
    }

    private static void AddFallbackAliases(Dictionary<string, Dictionary<string, string>> maps)
    {
        AddAliases(maps, "lane", "top", "top", "上", "上路", "탑", "トップ");
        AddAliases(maps, "lane", "jg", "jg", "jgl", "jungle", "打野", "野区", "정글", "ジャングル", "rừng");
        AddAliases(maps, "lane", "mid", "mid", "中", "中路", "미드", "ミッド");
        AddAliases(maps, "lane", "bot", "bot", "下", "下路", "바텀", "ボット");
        AddAliases(maps, "lane", "adc", "adc", "ad", "AD", "원딜");
        AddAliases(maps, "lane", "sup", "sup", "support", "辅助", "輔助", "서폿", "サポ", "sp");

        AddAliases(maps, "summoner_or_spell", "flash", "flash", "f", "闪", "闪现", "閃", "閃現", "점멸", "플 없음", "노플", "점멸 없음", "フラッシュ", "フラ", "フラなし", "フラッシュない", "Fない", "tốc biến");
        AddAliases(maps, "summoner_or_spell", "tp", "tp", "传送", "傳送", "텔", "텔 없음", "テレポート", "TPない", "dịch chuyển");
        AddAliases(maps, "summoner_or_spell", "heal", "heal", "治疗", "治療", "힐");
        AddAliases(maps, "summoner_or_spell", "ignite", "ignite", "点燃", "點燃");
        AddAliases(maps, "summoner_or_spell", "ult", "ult", "ulti", "r", "R", "大", "大招", "궁", "궁 없음", "노궁", "ウルト", "ult ない", "Rない");
        AddAliases(maps, "summoner_or_spell", "smite", "smite", "惩戒", "懲戒", "강타", "강타 없음", "スマイト", "スマイトない", "trừng phạt");

        AddAliases(maps, "objective", "dragon", "dragon", "drake", "小龙", "小龍", "龙", "龍", "용", "ドラゴン", "rồng");
        AddAliases(maps, "objective", "baron", "baron", "大龙", "大龍", "巴龙", "巴龍", "바론", "バロン");
        AddAliases(maps, "objective", "herald", "herald", "先锋", "先鋒", "전령", "ヘラルド");
        AddAliases(maps, "objective", "grubs", "grubs", "巢虫", "巢蟲", "虚空巢虫", "虛空巢蟲");

        foreach (var alias in GetOrCreateMap(maps, "objective"))
        {
            GetOrCreateMap(maps, "area").TryAdd(alias.Key, alias.Value);
        }
    }

    private static void AddAliases(
        Dictionary<string, Dictionary<string, string>> maps,
        string category,
        string canonical,
        params string[] aliases)
    {
        var map = GetOrCreateMap(maps, category);
        foreach (var alias in aliases)
        {
            var normalized = NormalizeForMatch(alias);
            if (!string.IsNullOrWhiteSpace(normalized))
            {
                map[normalized] = NormalizeCanonical(canonical);
            }
        }
    }

    private static Dictionary<string, string> GetOrCreateMap(
        Dictionary<string, Dictionary<string, string>> maps,
        string category)
    {
        if (!maps.TryGetValue(category, out var map))
        {
            map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            maps[category] = map;
        }

        return map;
    }

    private static bool TryFindAlias(
        MatcherIndex index,
        string category,
        string normalized,
        out string canonical)
    {
        canonical = string.Empty;
        if (!index.AliasMaps.TryGetValue(category, out var aliases))
        {
            return false;
        }

        foreach (var pair in aliases.OrderByDescending(pair => pair.Key.Length))
        {
            if (ContainsNormalizedTerm(normalized, pair.Key))
            {
                canonical = pair.Value;
                return true;
            }
        }

        return false;
    }

    private static string ResolveAliasCanonical(MatcherIndex index, string category, string value)
    {
        var normalized = NormalizeForMatch(value);
        if (index.AliasMaps.TryGetValue(category, out var aliases)
            && aliases.TryGetValue(normalized, out var canonical))
        {
            return canonical;
        }

        return NormalizeCanonical(value);
    }

    private static string? ResolveSlotDisplay(
        MatcherIndex index,
        string category,
        string canonical,
        string languageCode)
    {
        if (index.SlotDisplays.TryGetValue(category, out var categoryMap)
            && categoryMap.TryGetValue(NormalizeCanonical(canonical), out var languageMap)
            && languageMap.TryGetValue(languageCode, out var display)
            && !string.IsNullOrWhiteSpace(display))
        {
            return display;
        }

        return null;
    }

    private static bool IsToxicEntry(GlossaryEntry entry)
    {
        if (!string.IsNullOrWhiteSpace(entry.ToxicityLevel)
            && !entry.ToxicityLevel.Equals("none", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (entry.RenderPolicy.Equals("label_only", StringComparison.OrdinalIgnoreCase)
            || !string.IsNullOrWhiteSpace(entry.SafeOutputZhCn))
        {
            return true;
        }

        return entry.Category.StartsWith("toxic_", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsChatPhraseCandidate(GlossaryEntry entry)
    {
        return string.Equals(entry.Category, "chat_phrase_exact", StringComparison.OrdinalIgnoreCase)
            || string.Equals(entry.Category, "chat_particle", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsCoreTermCandidate(GlossaryEntry entry)
    {
        return string.Equals(entry.Category, "core_term", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsAliasCandidate(GlossaryEntry entry)
    {
        return entry.Category.EndsWith("_alias", StringComparison.OrdinalIgnoreCase);
    }

    private static IEnumerable<string> GetEntryTerms(GlossaryEntry entry, bool includeAliases)
    {
        if (!string.IsNullOrWhiteSpace(entry.Term))
        {
            yield return entry.Term;
        }

        if (!includeAliases)
        {
            yield break;
        }

        foreach (var alias in entry.Aliases ?? [])
        {
            if (!string.IsNullOrWhiteSpace(alias))
            {
                yield return alias;
            }
        }
    }

    private static string BuildToxicOutput(
        GlossaryEntry entry,
        string original,
        string toxicDisplayMode,
        string? targetLanguage)
    {
        var mode = NormalizeToxicDisplayMode(toxicDisplayMode);
        return mode switch
        {
            "hide" => IsSevere(entry) ? "[严重辱骂]" : "[辱骂]",
            "source" => original,
            "literal" => GetToxicLiteralOutput(entry, targetLanguage)
                ?? GetBestLiteralFallback(entry, original, targetLanguage)
                ?? original,
            _ => GetToxicLabel(entry)
        };
    }

    private static string? GetToxicLiteralOutput(GlossaryEntry entry, string? targetLanguage)
    {
        var target = TranslatorLanguage.NormalizeTargetLanguage(targetLanguage);
        if (TranslatorLanguage.IsTraditionalChinese(target))
        {
            return FirstNonEmpty(entry.LiteralOutputZhTw, entry.LiteralOutputZhCn, entry.LiteralOutputEnUs);
        }

        if (TranslatorLanguage.IsSimplifiedChinese(target))
        {
            return FirstNonEmpty(entry.LiteralOutputZhCn, entry.LiteralOutputZhTw, entry.LiteralOutputEnUs);
        }

        if (target.Equals("en", StringComparison.OrdinalIgnoreCase))
        {
            return FirstNonEmpty(entry.LiteralOutputEnUs, entry.LiteralOutputZhCn, entry.LiteralOutputZhTw);
        }

        return FirstNonEmpty(entry.LiteralOutputZhCn, entry.LiteralOutputZhTw, entry.LiteralOutputEnUs);
    }

    private static string? GetBestLiteralFallback(GlossaryEntry entry, string original, string? targetLanguage)
    {
        var target = TranslatorLanguage.NormalizeTargetLanguage(targetLanguage);
        if (TranslatorLanguage.IsAnyChinese(target) && ContainsCjk(entry.Term))
        {
            return entry.Term;
        }

        if (target.Equals("en", StringComparison.OrdinalIgnoreCase) && IsAsciiPhrase(entry.Term))
        {
            return entry.Term;
        }

        return string.IsNullOrWhiteSpace(original) ? null : original;
    }

    private static string? FirstNonEmpty(params string?[] values)
    {
        return values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
    }

    private static bool ContainsCjk(string value)
    {
        return value.Any(ch => ch is >= '\u4e00' and <= '\u9fff');
    }

    private static bool IsAsciiPhrase(string value)
    {
        return value.Any(char.IsAsciiLetter)
            && value.All(ch => char.IsAsciiLetterOrDigit(ch) || char.IsWhiteSpace(ch) || ch is '\'' or '-' or '/');
    }

    private static string GetToxicLabel(GlossaryEntry entry)
    {
        if (!string.IsNullOrWhiteSpace(entry.SafeOutputZhCn))
        {
            return entry.SafeOutputZhCn;
        }

        var typeLabel = entry.ToxicityType switch
        {
            "family_attack" => "家人攻击",
            "death_attack" => "死亡攻击",
            "personal_insult" => "人身攻击",
            "verbal_abuse" => "辱骂",
            "report_request" => "举报相关",
            "afk" => "挂机/离开游戏",
            "intentional_feeding" => "故意送",
            "griefing" => "摆烂/破坏游戏",
            "surrender_pressure" => "逼迫投降",
            "mild_taunt" => "轻度嘲讽",
            "blame" => "甩锅/责怪",
            "boosting_accusation" => "代练指控",
            "cheating_accusation" => "外挂指控",
            _ => string.Empty
        };

        if (string.IsNullOrWhiteSpace(typeLabel))
        {
            return IsSevere(entry) ? "[严重辱骂]" : "[辱骂]";
        }

        return IsSevere(entry)
            ? $"[严重辱骂：{typeLabel}]"
            : $"[辱骂：{typeLabel}]";
    }

    private static bool IsSevere(GlossaryEntry entry)
    {
        return entry.ToxicityLevel.Equals("severe", StringComparison.OrdinalIgnoreCase);
    }

    private static bool ContainsNormalizedTerm(string normalized, string normalizedTerm)
    {
        if (string.IsNullOrWhiteSpace(normalizedTerm))
        {
            return false;
        }

        if (string.Equals(normalized, normalizedTerm, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (IsAsciiWordLike(normalizedTerm))
        {
            var escaped = Regex.Escape(normalizedTerm).Replace(@"\ ", @"\s+");
            return Regex.IsMatch(
                normalized,
                $@"(?<![a-z0-9]){escaped}(?![a-z0-9])",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }

        return normalized.Contains(normalizedTerm, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsAsciiWordLike(string value)
    {
        return value.All(ch => ch <= 127 && (char.IsLetterOrDigit(ch) || char.IsWhiteSpace(ch) || ch is '_' or '-'));
    }

    private static bool HasNoSpellMarker(string normalized)
    {
        return ContainsAny(normalized, "no", "without", "down", "used", "没", "無", "无", "没有", "沒有", "없", "なし", "ない", "mất", "hết", "không", "노");
    }

    private static bool HasGankMarker(string normalized)
    {
        return ContainsAny(normalized, "gank", "come", "help", "camp", "抓", "来抓", "來抓", "帮", "幫", "갱", "ガンク", "giúp", "ra");
    }

    private static bool IsProtectedGankInstruction(string normalized)
    {
        return Regex.IsMatch(
            normalized,
            @"^(?:(?:pls|plz|please)\s+gank\s+(?:mid|middle)|gank\s+(?:mid|middle)(?:\s+(?:pls|plz|please))?|(?:jg|jungle)\s+gank\s+(?:mid|middle)|come\s+(?:mid|middle)|help\s+(?:mid|middle)|please\s+gank\s+mid\s+lane)$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    private static bool HasClearVisionMarker(string normalized)
    {
        return ContainsAny(normalized, "clear vision", "clear ward", "sweep", "排眼", "清视野", "清視野", "清", "掃", "扫");
    }

    private static bool HasWardMarker(string normalized)
    {
        return ContainsAny(normalized, "ward", "pink", "need vision", "vision", "做视野", "做視野", "插眼", "视野", "視野", "와드");
    }

    private static bool HasObjectiveMarker(string normalized)
    {
        return ContainsAny(normalized, "go", "take", "do", "rush", "start", "free", "打", "拿", "开", "開", "가자", "行こう", "đi");
    }

    private static bool ContainsAny(string normalized, params string[] markers)
    {
        return markers.Any(marker => ContainsNormalizedTerm(normalized, NormalizeForMatch(marker)));
    }

    private static string GetLaneShortZh(string canonical)
    {
        return NormalizeCanonical(canonical) switch
        {
            "top" => "上",
            "mid" => "中",
            "bot" => "下",
            "jg" => "打野",
            "jungle" => "打野",
            "adc" => "ADC",
            "sup" => "辅助",
            "support" => "辅助",
            _ => canonical
        };
    }

    private static string GetLaneFullZh(MatcherIndex index, string canonical)
    {
        return ResolveSlotDisplay(index, "lane", canonical, "zh_CN")
            ?? GetLaneFullZh(canonical);
    }

    private static string GetLaneFullZh(string canonical)
    {
        return NormalizeCanonical(canonical) switch
        {
            "top" => "上路",
            "mid" => "中路",
            "bot" => "下路",
            "jg" => "打野",
            "jungle" => "打野",
            "adc" => "ADC",
            "sup" => "辅助",
            "support" => "辅助",
            _ => canonical
        };
    }

    private static string GetSpellShortZh(string canonical)
    {
        return NormalizeCanonical(canonical) switch
        {
            "flash" => "闪",
            "tp" => "传送",
            "heal" => "治疗",
            "ignite" => "点燃",
            "ult" => "大招",
            "r" => "大招",
            "smite" => "惩戒",
            "exhaust" => "虚弱",
            "cleanse" => "净化",
            _ => canonical
        };
    }

    private static string GetObjectiveZh(MatcherIndex index, string canonical)
    {
        return ResolveSlotDisplay(index, "objective", canonical, "zh_CN")
            ?? GetObjectiveFallbackZh(canonical);
    }

    private static string GetAreaZh(MatcherIndex index, string canonical)
    {
        return ResolveSlotDisplay(index, "area", canonical, "zh_CN")
            ?? ResolveSlotDisplay(index, "objective", canonical, "zh_CN")
            ?? GetObjectiveFallbackZh(canonical);
    }

    private static string GetObjectiveFallbackZh(string canonical)
    {
        return NormalizeCanonical(canonical) switch
        {
            "dragon" or "drake" => "小龙",
            "baron" => "大龙",
            "herald" => "先锋",
            "grubs" => "巢虫",
            "tower" => "塔",
            "inhib" => "水晶",
            "nexus" => "基地",
            "blue" => "蓝区",
            "red" => "红区",
            "river" => "河道",
            "tribush" => "三角草",
            _ => canonical
        };
    }

    private static string ToSlotCategory(string slotName)
    {
        return slotName switch
        {
            "spell" => "summoner_or_spell",
            "summoner" => "summoner_or_spell",
            "summoner_or_spell" => "summoner_or_spell",
            "lane" => "lane",
            "target" => "target",
            "objective" => "objective",
            "area" => "area",
            _ => slotName
        };
    }

    private static string NormalizeCanonical(string value)
    {
        return NormalizeForMatch(value).Replace(" ", "_");
    }

    private static string ConvertPythonNamedGroups(string pattern)
    {
        return pattern.Replace("(?P<", "(?<", StringComparison.Ordinal);
    }

    private static string NormalizeToxicDisplayMode(string? mode)
    {
        if (string.IsNullOrWhiteSpace(mode))
        {
            return DefaultToxicDisplayMode;
        }

        var normalized = mode.Trim().ToLowerInvariant();
        return normalized switch
        {
            "hide" or "label" or "literal" or "source" => normalized,
            "raw" => "literal",
            _ => DefaultToxicDisplayMode
        };
    }

    private static string ToGlossaryLanguageCode(string? language)
    {
        return TranslatorLanguage.NormalizeTargetLanguage(language) switch
        {
            "zh-Hant" => "zh_TW",
            "en" => "en_US",
            "ko" => "ko_KR",
            "ja" => "ja_JP",
            "vi" => "vi_VN",
            _ => "zh_CN"
        };
    }

    private static string DescribeEntry(GlossaryEntry entry, string pattern)
    {
        var key = entry.Id ?? entry.Term;
        return $"{entry.Category}:{key}; pattern={pattern}";
    }

    private static void WriteDebugLog(GlossaryMatchResult result)
    {
        try
        {
            var line = $"{DateTime.Now:HH:mm:ss} [GlossaryMatcher] level={result.MatchLevel} confidence={result.Confidence:0.00} normalized={SanitizeLogValue(result.NormalizedText)} output={SanitizeLogValue(result.OutputText)} entry={SanitizeLogValue(result.MatchedEntry)}";
            AppLogService.AppendVerboseText("glossary-matcher.log", $"{line}{Environment.NewLine}");
            Trace.TraceInformation(line);
        }
        catch
        {
            // Glossary debug logging must never affect OCR or translation.
        }
    }

    private static string SanitizeLogValue(string? value)
    {
        return Regex.Replace(value ?? string.Empty, @"\s+", " ").Trim();
    }

    private sealed class MatcherIndex
    {
        public List<MatchCandidate> ToxicCandidates { get; set; } = [];

        public List<MatchCandidate> ChatPhraseCandidates { get; set; } = [];

        public List<MatchCandidate> CoreTermCandidates { get; set; } = [];

        public List<MatchCandidate> AliasCandidates { get; set; } = [];

        public List<PatternCandidate> PatternCandidates { get; set; } = [];

        public Dictionary<string, Dictionary<string, string>> AliasMaps { get; init; } = new(StringComparer.OrdinalIgnoreCase);

        public Dictionary<string, Dictionary<string, Dictionary<string, string>>> SlotDisplays { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private sealed record MatchCandidate(string Pattern, string NormalizedPattern, GlossaryEntry Entry);

    private sealed record PatternCandidate(GlossaryEntry Entry, string RawPattern, Regex Regex, int Priority);

    private sealed record ResolvedSlot(
        string SlotName,
        string Category,
        string Canonical,
        string ZhCn,
        string TargetText);

    private sealed class GlossaryPack
    {
        [JsonPropertyName("phrase_slots")]
        public Dictionary<string, JsonElement>? PhraseSlots { get; set; }

        [JsonPropertyName("phrase_slot_aliases")]
        public Dictionary<string, JsonElement>? PhraseSlotAliases { get; set; }

        [JsonPropertyName("entries")]
        public List<GlossaryEntry>? Entries { get; set; }
    }

    private sealed class GlossaryEntry
    {
        [JsonPropertyName("id")]
        public string? Id { get; set; }

        [JsonPropertyName("term")]
        public string Term { get; set; } = string.Empty;

        [JsonPropertyName("lang")]
        public string Lang { get; set; } = string.Empty;

        [JsonPropertyName("category")]
        public string Category { get; set; } = string.Empty;

        [JsonPropertyName("phrase_intent")]
        public string? PhraseIntent { get; set; }

        [JsonPropertyName("meaning_zh")]
        public string MeaningZh { get; set; } = string.Empty;

        [JsonPropertyName("translation")]
        public Dictionary<string, string>? Translation { get; set; }

        [JsonPropertyName("translation_template")]
        public Dictionary<string, string>? TranslationTemplate { get; set; }

        [JsonPropertyName("aliases")]
        public List<string>? Aliases { get; set; }

        [JsonPropertyName("canonical")]
        public GlossaryCanonical? Canonical { get; set; }

        [JsonPropertyName("toxicity_level")]
        public string ToxicityLevel { get; set; } = "none";

        [JsonPropertyName("toxicity_type")]
        public string ToxicityType { get; set; } = string.Empty;

        [JsonPropertyName("render_policy")]
        public string RenderPolicy { get; set; } = string.Empty;

        [JsonPropertyName("safe_output_zh_CN")]
        public string SafeOutputZhCn { get; set; } = string.Empty;

        [JsonPropertyName("literal_output_zh_CN")]
        public string LiteralOutputZhCn { get; set; } = string.Empty;

        [JsonPropertyName("literal_output_zh_TW")]
        public string LiteralOutputZhTw { get; set; } = string.Empty;

        [JsonPropertyName("literal_output_en_US")]
        public string LiteralOutputEnUs { get; set; } = string.Empty;

        [JsonPropertyName("patterns")]
        public List<string>? Patterns { get; set; }

        [JsonPropertyName("priority")]
        public int Priority { get; set; }

        [JsonPropertyName("slots")]
        public List<string>? Slots { get; set; }
    }

    private sealed class GlossaryCanonical
    {
        [JsonPropertyName("concept_id")]
        public string? ConceptId { get; set; }

        [JsonPropertyName("slots")]
        public Dictionary<string, string>? Slots { get; set; }
    }

}

public sealed class GlossaryMatchResult
{
    public bool Matched { get; init; }

    public string MatchLevel { get; init; } = "none";

    public string OriginalText { get; init; } = string.Empty;

    public string NormalizedText { get; init; } = string.Empty;

    public string? OutputText { get; init; }

    public string DirectOutputKind { get; init; } = "none";

    public double Confidence { get; init; }

    public string? MatchedEntry { get; init; }

    public static GlossaryMatchResult None(string originalText, string normalizedText)
    {
        return new GlossaryMatchResult
        {
            Matched = false,
            MatchLevel = "none",
            OriginalText = originalText,
            NormalizedText = normalizedText,
            OutputText = null,
            Confidence = 0,
            MatchedEntry = null
        };
    }
}
