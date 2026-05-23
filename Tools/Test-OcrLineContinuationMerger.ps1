$ErrorActionPreference = "Stop"

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
$projectPath = Join-Path $repoRoot "LoLChatTranslator\LoLChatTranslator.csproj"
$testDir = Join-Path $env:TEMP "LoLChatTranslatorLineMergeTests"

if (Test-Path -LiteralPath $testDir) {
    Remove-Item -LiteralPath $testDir -Recurse -Force
}

New-Item -ItemType Directory -Path $testDir | Out-Null

@"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net8.0-windows10.0.19041.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <UseWPF>true</UseWPF>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="$projectPath" />
  </ItemGroup>
</Project>
"@ | Set-Content -LiteralPath (Join-Path $testDir "LineMergeTests.csproj") -Encoding UTF8

@'
using LoLChatTranslator.Models;
using LoLChatTranslator.Services;
using LoLChatTranslator;
using System.Reflection;
using System.Windows;

static OcrTextLine Line(string text, double x, double y)
{
    return new OcrTextLine { Text = text, Confidence = 0.95, BoundingBox = new Rect(x, y, 600, 18) };
}

static string MessageOf(string line) => ChatDeduper.ParseChatLine(line).RawMessageBody;

var cases = new[]
{
    new TestCase(
        "single line unchanged",
        ["[队伍] A（英雄）: hello"],
        [Line("[队伍] A（英雄）: hello", 20, 10)],
        1,
        "hello"),
    new TestCase(
        "english timestamp continuation",
        ["02:40 [队伍] Ntide07（惩戒之箭）: ok i like chinese too,and i also like chinese kon", "gfu, but i not like apple, because is very red"],
        [Line("02:40 [队伍] Ntide07（惩戒之箭）: ok i like chinese too,and i also like chinese kon", 20, 10), Line("gfu, but i not like apple, because is very red", 5, 31)],
        1,
        "ok i like chinese too,and i also like chinese kongfu, but i not like apple, because is very red"),
    new TestCase(
        "and i stays separated",
        ["[队伍] A（英雄）: this message is long enough and", "i also like it"],
        [Line("[队伍] A（英雄）: this message is long enough and", 20, 10), Line("i also like it", 5, 31)],
        1,
        "this message is long enough and i also like it"),
    new TestCase(
        "chinese too stays separated",
        ["[队伍] A（英雄）: i like chinese", "too much today because it is good"],
        [Line("[队伍] A（英雄）: i like chinese", 20, 10), Line("too much today because it is good", 5, 31)],
        1,
        "i like chinese too much today because it is good"),
    new TestCase(
        "english continuation then quest completion blocker",
        ["02:40 [队伍] A（英雄）: ok i like chinese kon", "gfu, but i not like apple", "23:20 A（英雄）守卫任务完成!"],
        [Line("02:40 [队伍] A（英雄）: ok i like chinese kon", 20, 10), Line("gfu, but i not like apple", 5, 31), Line("23:20 A（英雄）守卫任务完成!", 5, 52)],
        2,
        "ok i like chinese kongfu, but i not like apple"),
    new TestCase(
        "ocr no-space timestamp quest completion blocker",
        ["02:40 [队伍] A（英雄）: ok i like chinese kon", "gfu, but i not like apple", "23:20A（英雄）守卫任务完成!"],
        [Line("02:40 [队伍] A（英雄）: ok i like chinese kon", 20, 10), Line("gfu, but i not like apple", 5, 31), Line("23:20A（英雄）守卫任务完成!", 5, 52)],
        2,
        "ok i like chinese kongfu, but i not like apple"),
    new TestCase(
        "position selected system blocker",
        ["02:40 [队伍] A（英雄）: ok i like chinese kon", "00:19 惩戒之箭已经选择了位置：辅助"],
        [Line("02:40 [队伍] A（英雄）: ok i like chinese kon", 20, 10), Line("00:19 惩戒之箭已经选择了位置：辅助", 5, 31)],
        2,
        "ok i like chinese kon"),
    new TestCase(
        "chinese continuation no extra space",
        ["[队伍] A（英雄）: 我们等一下再开团", "不要先打"],
        [Line("[队伍] A（英雄）: 我们等一下再开团", 20, 10), Line("不要先打", 6, 31)],
        1,
        "我们等一下再开团不要先打"),
    new TestCase(
        "no timestamp continuation",
        ["[全部] B（英雄）: this message is very long and should continue", "from the left side"],
        [Line("[全部] B（英雄）: this message is very long and should continue", 20, 10), Line("from the left side", 4, 31)],
        1,
        "this message is very long and should continue from the left side"),
    new TestCase(
        "system prompt not merged",
        ["[队伍] A（英雄）: this message is very long and should continue", "游戏将在5分钟内结束"],
        [Line("[队伍] A（英雄）: this message is very long and should continue", 20, 10), Line("游戏将在5分钟内结束", 5, 31)],
        2,
        "this message is very long and should continue"),
    new TestCase(
        "practice tool max duration prompt not merged into player long line",
        ["02:32 [队伍] Ntide07（炽炎雏龙）: i from USA,but i like canada,i also like china an", "d panda,but i not like japan", "55:00 你已临近训练模式自由的最大游戏时长。", "55:00 游戏将在5分钟内结束。"],
        [Line("02:32 [队伍] Ntide07（炽炎雏龙）: i from USA,but i like canada,i also like china an", 20, 10), Line("d panda,but i not like japan", 5, 31), Line("55:00 你已临近训练模式自由的最大游戏时长。", 5, 52), Line("55:00 游戏将在5分钟内结束。", 5, 73)],
        3,
        "i from USA,but i like canada,i also like china and panda,but i not like japan"),
    new TestCase(
        "different players not merged",
        ["[队伍] A（英雄）: this message is very long and should continue", "[队伍] B（英雄）: hello"],
        [Line("[队伍] A（英雄）: this message is very long and should continue", 20, 10), Line("[队伍] B（英雄）: hello", 20, 31)],
        2,
        "this message is very long and should continue"),
    new TestCase(
        "short phrases stay separate",
        ["[队伍] A（英雄）: hello", "ff"],
        [Line("[队伍] A（英雄）: hello", 20, 10), Line("ff", 5, 31)],
        2,
        "hello"),
    new TestCase(
        "pls gank mid full line",
        ["[队伍] A（英雄）: pls gank mid", "[队伍] B（英雄）: ff", "[队伍] C（英雄）: hello"],
        [Line("[队伍] A（英雄）: pls gank mid", 20, 10), Line("[队伍] B（英雄）: ff", 20, 31), Line("[队伍] C（英雄）: hello", 20, 52)],
        3,
        "pls gank mid"),
};

foreach (var test in cases)
{
    var result = OcrLineContinuationMerger.Merge(test.Lines, test.TextLines);
    var message = MessageOf(result.Lines[0]);
    if (result.Lines.Count != test.ExpectedCount || message != test.ExpectedFirstMessage)
    {
        throw new InvalidOperationException($"FAILED {test.Name}: count={result.Lines.Count}, message=[{message}], lines=[{string.Join(" || ", result.Lines)}]");
    }

    Console.WriteLine($"PASS {test.Name}: count={result.Lines.Count}, continuations={result.Events.Count}");
}

var noBoxSplit = OcrLineContinuationMerger.Merge(
    ["02:40 [队伍] Ntide07（惩戒之箭）: ok i like chinese too,and i also like chinese kon", "gfu, but i not like apple"],
    null);
AssertText(
    "known split word joins without boxes",
    MessageOf(noBoxSplit.Lines[0]),
    "ok i like chinese too,and i also like chinese kongfu, but i not like apple");

static string NormalizeForTranslator(string text)
{
    return new MessageNormalizer().Normalize(text, "label", "zh-Hans").NormalizedText;
}

static void AssertText(string name, string actual, string expected)
{
    if (!string.Equals(actual, expected, StringComparison.Ordinal))
    {
        throw new InvalidOperationException($"FAILED {name}: expected=[{expected}], actual=[{actual}]");
    }

    Console.WriteLine($"PASS {name}: {actual}");
}

AssertText(
    "english single line keeps spaces",
    NormalizeForTranslator("ok i like chinese too"),
    "ok i like Chinese language too");

AssertText(
    "chinese inner spaces are cleaned",
    NormalizeForTranslator("你 喜 欢 中 国"),
    "你喜欢中国");

AssertText(
    "mixed chinese english keeps english spaces",
    NormalizeForTranslator("我 like chinese too"),
    "我 like Chinese language too");

AssertText(
    "english continuation translator input",
    NormalizeForTranslator("ok i like chinese too,and i also like chinese kongfu, but i not like apple,because is very red"),
    "ok i like Chinese language too, and i also like Chinese kung fu, but i do not like apple, because is very red");

AssertText(
    "direct translation input normalizer long sentence",
    TranslationInputNormalizer.NormalizeForTranslation("ok i like chinese too,and i also like chinese kon gfu, but i not like apple,because is very red"),
    "ok i like Chinese language too, and i also like Chinese kung fu, but i do not like apple, because is very red");

AssertText(
    "direct translation input restores glued english",
    TranslationInputNormalizer.NormalizeForTranslation("ifromUS, buti like china, ialsolikechinesek ong fu, buti not like panda"),
    "i from US, but i like China, i also like Chinese kung fu, but i do not like panda");

AssertText(
    "parser fixes ifromchina body only",
    ChatDeduper.ParseChatLine("00:40 [队伍] Ntide07（百裂冥犬）:ifromchina").Message,
    "i from China");

var parsedNameCheck = ChatDeduper.ParseChatLine("00:40 [队伍] Ntide07（百裂冥犬）:ifromchina");
if (parsedNameCheck.Sender != "Ntide07" || parsedNameCheck.Champion != "百裂冥犬")
{
    throw new InvalidOperationException($"FAILED parser preserves player/champion: sender=[{parsedNameCheck.Sender}], champion=[{parsedNameCheck.Champion}]");
}

Console.WriteLine("PASS parser preserves player/champion");

AssertText(
    "legacy RapidOCR migrates to PP-OCRv5",
    OcrEngines.Normalize("RapidOCR"),
    OcrEngines.PpOcrV5Multilingual);

AssertText(
    "legacy LocalScript migrates to PP-OCRv5",
    OcrEngines.Normalize("LocalScript"),
    OcrEngines.PpOcrV5Multilingual);

AssertText(
    "Windows OCR stays WindowsOCR",
    OcrEngines.Normalize("Windows OCR"),
    OcrEngines.WindowsOcr);

AssertText(
    "unknown non-Windows OCR migrates to PP-OCRv5",
    OcrEngines.Normalize("Other"),
    OcrEngines.PpOcrV5Multilingual);

AssertText(
    "unknown OCR language defaults to auto",
    OcrLanguages.Normalize("made_up_language"),
    OcrLanguages.Auto);

var migrationConfig = AppConfig.CreateDefault();
migrationConfig.OcrConfig.OcrEngine = "PaddleOCR";
migrationConfig.OcrConfig.OcrLanguage = "";
var normalizeConfig = typeof(ConfigService).GetMethod("NormalizeConfig", BindingFlags.Instance | BindingFlags.NonPublic)
    ?? throw new InvalidOperationException("NormalizeConfig reflection lookup failed");
var migratedConfig = (AppConfig)(normalizeConfig.Invoke(new ConfigService(), [migrationConfig])
    ?? throw new InvalidOperationException("NormalizeConfig returned null"));
AssertText(
    "ConfigService migrates PaddleOCR to PP-OCRv5",
    migratedConfig.OcrConfig.OcrEngine,
    OcrEngines.PpOcrV5Multilingual);
AssertText(
    "ConfigService fills missing OCR language",
    migratedConfig.OcrConfig.OcrLanguage,
    OcrLanguages.Auto);

AssertText(
    "ocr glue fixer ifromUS",
    OcrEnglishGlueFixer.FixMessageBody("ifromUS"),
    "i from US");

AssertText(
    "ocr glue fixer ifromUSA",
    OcrEnglishGlueFixer.FixMessageBody("ifromUSA"),
    "i from USA");

AssertText(
    "ocr glue fixer iverylikeUSA",
    TranslationInputNormalizer.NormalizeForTranslation("iverylikeUSA"),
    "i like USA very much");

AssertText(
    "ocr glue fixer ilikeappinc",
    OcrEnglishGlueFixer.FixMessageBody("ilikeappinc"),
    "i like app inc");

AssertText(
    "ocr glue fixer butilikechina",
    OcrEnglishGlueFixer.FixMessageBody("butilikechina"),
    "but i like China");

AssertText(
    "ocr glue fixer ialsilikechinese",
    OcrEnglishGlueFixer.FixMessageBody("ialsilikechinese"),
    "i also like Chinese");

AssertText(
    "direct translation input restores butinotlikepanda",
    TranslationInputNormalizer.NormalizeForTranslation("i from US, but i like china, i also like chinese k ong fu, butinotlikepanda"),
    "i from US, but i like China, i also like Chinese kung fu, but i do not like panda");

AssertText(
    "direct translation input restores OCR inserted m glue",
    TranslationInputNormalizer.NormalizeForTranslation("ifromUS, buti like china, ialsomlikechinesek ong fu"),
    "i from US, but i like China, i also like Chinese kung fu");

AssertText(
    "app inc glue is restored",
    TranslationInputNormalizer.NormalizeForTranslation("appinc"),
    "app inc");

AssertText(
    "glued english phrase is segmented",
    NormalizeForTranslator("ok i like chinese too,andialsolikechinesekon gfu, but i not like apple,because is very red"),
    "ok i like Chinese language too, and i also like Chinese kung fu, but i do not like apple, because is very red");

AssertText(
    "split no-space english OCR tokens are segmented",
    OcrTextFixer.ApplyBuiltInFixes("Wehavemanyen ginehere"),
    "we have many engine here");

if (!OcrTextFixer.LooksUntranslated(
        "hello im Lihua, i am from China, we have many engine here",
        "您好，Im Lihua，我来自中国，Wehavemanyen ginehere",
        "zh-Hans"))
{
    throw new InvalidOperationException("FAILED Chinese target mixed untranslated residue is rejected");
}

Console.WriteLine("PASS Chinese target mixed untranslated residue is rejected");

if (OcrTextFixer.LooksUntranslated("中路 mid 没闪", "中路 mid 没闪", "zh-Hans"))
{
    throw new InvalidOperationException("FAILED Chinese sentence with a short LOL term is not untranslated");
}

Console.WriteLine("PASS Chinese sentence with a short LOL term is not untranslated");

var tryBuildDisplayTranslation = typeof(MainWindow).GetMethod("TryBuildDisplayTranslation", BindingFlags.NonPublic | BindingFlags.Static)
    ?? throw new InvalidOperationException("FAILED display translation reflection: method not found");
object?[] displayArgs =
[
    "hello im Lihua, i am from China, we have many engine here",
    "您好，Im Lihua，我来自中国，Wehavemanyen ginehere",
    "zh-Hans",
    string.Empty
];
var displayAccepted = (bool)(tryBuildDisplayTranslation.Invoke(null, displayArgs) ?? false);
if (displayAccepted || !string.IsNullOrWhiteSpace(displayArgs[3]?.ToString()))
{
    throw new InvalidOperationException($"FAILED mixed untranslated display should be rejected: accepted={displayAccepted}, display=[{displayArgs[3]}]");
}

Console.WriteLine("PASS mixed untranslated display is rejected before Chinese target acceptance");

AssertText(
    "kon gfu normalizes to kung fu",
    NormalizeForTranslator("kon gfu ,"),
    "kung fu,");

AssertText(
    "chinese kon gfu normalizes to Chinese kung fu",
    NormalizeForTranslator("i also like chinese kon gfu,"),
    "i also like Chinese kung fu,");

AssertText(
    "chinese k ong fu normalizes to Chinese kung fu",
    NormalizeForTranslator("i also like chinese k ong fu,"),
    "i also like Chinese kung fu,");

AssertText(
    "chinesek ongfu normalizes to Chinese kung fu",
    NormalizeForTranslator("i also like chinesek ongfu,"),
    "i also like Chinese kung fu,");

AssertText(
    "ongfu comma glue normalizes to kung fu",
    NormalizeForTranslator("ongfu,butinotlikepanda"),
    "kung fu, but i do not like panda");

AssertText(
    "kongfu normalizes to kung fu",
    NormalizeForTranslator("kongfu."),
    "kung fu.");

AssertText(
    "kungfu normalizes to kung fu",
    NormalizeForTranslator("kungfu!"),
    "kung fu!");

AssertText(
    "Chinese food keeps food meaning",
    NormalizeForTranslator("i like Chinese food"),
    "i like Chinese food");

AssertText(
    "Chinese restaurant keeps restaurant meaning",
    NormalizeForTranslator("i like Chinese restaurant"),
    "i like Chinese restaurant");

AssertText(
    "Chinese language postprocess avoids food mistranslation",
    TranslationInputNormalizer.PostProcessTranslation("i like Chinese language too", "我喜欢中国菜", "zh-Hans"),
    "我喜欢中文");

AssertText(
    "Chinese food postprocess keeps food mistranslation",
    TranslationInputNormalizer.PostProcessTranslation("i like Chinese food", "我喜欢中国菜", "zh-Hans"),
    "我喜欢中国菜");

AssertText(
    "Chinese kung fu postprocess avoids language mistranslation",
    TranslationInputNormalizer.PostProcessTranslation("i like Chinese kung fu", "我喜欢中文功夫", "zh-Hans"),
    "我喜欢中国功夫");

var playerChatWithSystemKeyword = ChatDeduper.ParseChatLine("[队伍] A（英雄）: 我购买了装备");
var playerChatKeywordValidation = ChatDeduper.IsValidPlayerChat(playerChatWithSystemKeyword, new ChannelAliasService());
if (!playerChatKeywordValidation.Valid)
{
    throw new InvalidOperationException($"FAILED player chat with system keyword stays valid: reason={playerChatKeywordValidation.Reason}");
}

Console.WriteLine("PASS player chat with system keyword stays valid");

var cleaner = new ChatCleaner(new ChannelAliasService());
var purchasePrompt = "Ntide07 purchased Long Sword";
if (cleaner.CleanMessage(purchasePrompt, AppConfig.CreateDefault()) is not null)
{
    throw new InvalidOperationException("FAILED purchase prompt is filtered by default");
}

var allowPurchaseConfig = AppConfig.CreateDefault();
allowPurchaseConfig.FilterConfig.FilterPurchaseMessages = false;
var unfilteredPurchase = cleaner.CleanMessage(purchasePrompt, allowPurchaseConfig);
if (unfilteredPurchase is null || unfilteredPurchase.Channel != ChatChannel.System)
{
    throw new InvalidOperationException("FAILED purchase prompt follows FilterPurchaseMessages=false");
}

Console.WriteLine("PASS purchase prompt follows FilterConfig");

var practicePrompt = "55:00 你已临近训练模式自由的最大游戏时长。";
if (!ChatDeduper.IsSystemOrCommandLine(practicePrompt))
{
    throw new InvalidOperationException("FAILED practice tool max duration prompt classified as system");
}

if (cleaner.CleanMessage(practicePrompt, AppConfig.CreateDefault()) is not null)
{
    throw new InvalidOperationException("FAILED practice tool max duration prompt is filtered by default");
}

var allowSystemConfig = AppConfig.CreateDefault();
allowSystemConfig.FilterConfig.FilterSystemMessages = false;
var unfilteredPracticePrompt = cleaner.CleanMessage(practicePrompt, allowSystemConfig);
if (unfilteredPracticePrompt is null || unfilteredPracticePrompt.Channel != ChatChannel.System)
{
    throw new InvalidOperationException("FAILED practice tool prompt follows FilterSystemMessages=false");
}

Console.WriteLine("PASS practice tool prompt follows system filtering and stays independent");

AssertText(
    "ascii punctuation adds following space",
    NormalizeForTranslator("too,and apple,because"),
    "too, and apple, because");

AssertText(
    "pls gank mid keeps spaces",
    NormalizeForTranslator("pls gank mid"),
    "pls gank mid");

static NormalizedMessage NormalizeFull(string text)
{
    return new MessageNormalizer().Normalize(text, "label", "zh-Hans");
}

static void AssertDirectTranslation(string name, string text, string expected)
{
    var normalized = NormalizeFull(text);
    if (!string.Equals(normalized.DirectTranslation, expected, StringComparison.Ordinal))
    {
        throw new InvalidOperationException($"FAILED {name}: expected=[{expected}], actual=[{normalized.DirectTranslation}], normalized=[{normalized.NormalizedText}]");
    }

    Console.WriteLine($"PASS {name}: {normalized.DirectTranslation}");
}

AssertDirectTranslation("pls gank mid translates as tactic", "pls gank mid", "来抓中");
AssertDirectTranslation("pls gank int fixes OCR mid", "pls gank int", "来抓中");
AssertDirectTranslation("jg gank int fixes OCR mid", "jg gank int", "来抓中");
AssertDirectTranslation("come mid translates as tactic", "come mid", "来抓中");
AssertText("glue iamchinese", NormalizeForTranslator("iamchinese"), "i am Chinese");
AssertText("glue imfromchina", NormalizeForTranslator("imfromchina"), "i am from China");
AssertText("glue ilikebanana", NormalizeForTranslator("ilikebanana"), "i like banana");
AssertText("glue ilikebananatoo", NormalizeForTranslator("ilikebananatoo"), "i like banana too");
AssertText("glue buti not likeapple", NormalizeForTranslator("buti not likeapple"), "but i do not like apple");
AssertText(
    "long acceptance OCR glue sentence",
    NormalizeForTranslator("hello,iverylikeUSA,andialsollikechina,ilike pandatoo,buti not likeapple,becauseisveryred"),
    "hello, i like USA very much, and i also like China, i like panda too, but i do not like apple, because it is very red");
AssertDirectTranslation("hello built-in", "hello", "你好");
AssertDirectTranslation("apple built-in", "apple", "苹果");
AssertDirectTranslation("banana built-in", "banana", "香蕉");
AssertDirectTranslation("i am chinese built-in", "iamchinese", "我是中国人");
AssertDirectTranslation("i like apple built-in", "ilikeapple", "我喜欢苹果");
AssertDirectTranslation("i like banana built-in", "ilikebanana", "我喜欢香蕉");
AssertDirectTranslation("i like panda too built-in", "ilikepandatoo", "我也喜欢熊猫");
AssertDirectTranslation("but i do not like banana built-in", "butinotlikebanana", "但我不喜欢香蕉");
AssertDirectTranslation("i from USA built-in", "i from USA", "我来自美国");
AssertDirectTranslation("i am from USA built-in", "i am from USA", "我来自美国");
AssertDirectTranslation("i from china built-in", "i from china", "我来自中国");
AssertDirectTranslation("i like canada built-in", "i like canada", "我喜欢加拿大");
AssertDirectTranslation("i also like china and panda built-in", "i also like china and panda", "我也喜欢中国和熊猫");
AssertDirectTranslation("but i not like japan built-in", "but i not like japan", "但我不喜欢日本");
AssertDirectTranslation(
    "long natural built-in fallback",
    "i from USA,but i like canada,i also like china and panda,but i not like japan",
    "我来自美国，但我喜欢加拿大，我也喜欢中国和熊猫，但我不喜欢日本");

if (!OcrTextFixer.LooksUntranslated("i from USA", "i来自美国", "zh-Hans"))
{
    throw new InvalidOperationException("FAILED i-from-USA half translation should be rejected");
}

Console.WriteLine("PASS i-from-USA half translation is rejected");

foreach (var acceptance in new[]
{
    ("acceptance hello", "hello", "你好"),
    ("acceptance i am chinese", "i am chinese", "我是中国人"),
    ("acceptance apple", "apple", "苹果"),
    ("acceptance i like apple", "i like apple", "我喜欢苹果"),
    ("acceptance banana", "banana", "香蕉"),
    ("acceptance i like banana", "i like banana", "我喜欢香蕉")
})
{
    AssertDirectTranslation(acceptance.Item1, acceptance.Item2, acceptance.Item3);
}

var intTranslation = NormalizeFull("int").DirectTranslation ?? string.Empty;
if (!intTranslation.Contains("故意送", StringComparison.Ordinal) && !intTranslation.Contains("负面行为", StringComparison.Ordinal))
{
    throw new InvalidOperationException($"FAILED int keeps feeding meaning: actual=[{intTranslation}]");
}

Console.WriteLine($"PASS int keeps feeding meaning: {intTranslation}");

var openMidTranslation = NormalizeFull("open mid").DirectTranslation ?? string.Empty;
if (openMidTranslation.Contains("来抓中", StringComparison.Ordinal))
{
    throw new InvalidOperationException($"FAILED open mid should stay negative, actual=[{openMidTranslation}]");
}

Console.WriteLine($"PASS open mid stays non-gank: {openMidTranslation}");

var question = NormalizeForTranslator("do you like china?");
if (question.Contains("doyoulikechina", StringComparison.OrdinalIgnoreCase))
{
    throw new InvalidOperationException($"FAILED do you like china spacing: actual=[{question}]");
}
Console.WriteLine($"PASS do you like china spacing: {question}");

foreach (var shortText in new[] { "ff", "gg", "hello" })
{
    var normalized = NormalizeForTranslator(shortText);
    if (normalized.Contains(' ') || string.IsNullOrWhiteSpace(normalized))
    {
        throw new InvalidOperationException($"FAILED short text {shortText}: actual=[{normalized}]");
    }

    Console.WriteLine($"PASS short text {shortText}: {normalized}");
}

static ParsedChatMessage Parsed(string line) => ChatDeduper.ParseChatLine(line);

static void AssertDedupe(string name, ChatDedupeDecision decision, bool expected, string? expectedReason = null)
{
    if (decision.ShouldTranslate != expected
        || expectedReason is not null && !string.Equals(decision.Reason, expectedReason, StringComparison.Ordinal))
    {
        throw new InvalidOperationException($"FAILED {name}: should={decision.ShouldTranslate}, reason={decision.Reason}");
    }

    Console.WriteLine($"PASS {name}: should={decision.ShouldTranslate}, reason={decision.Reason}");
}

var fullLine = "02:40 [队伍] Ntide07（惩戒之箭）: ok i like chinese too,and i also like chinese kongfu, but i not like apple, because is very red";
var partialLine = "02:40 [队伍] Ntide07（惩戒之箭）: ok i like chinese too,and i also like chinese kon";
var deduper = new ChatDeduper();
AssertDedupe("full long message first translates", deduper.ShouldTranslate(Parsed(fullLine)), true);
AssertDedupe("later first-line partial is suppressed", deduper.ShouldTranslate(Parsed(partialLine)), false, "partial_duplicate_recent_full");

deduper.Clear();
AssertDedupe("first-line partial can be held initially", deduper.ShouldTranslate(Parsed(partialLine)), true);
AssertDedupe("full extension is still allowed to replace partial", deduper.ShouldTranslate(Parsed(fullLine)), true);

deduper.Clear();
AssertDedupe("pls gank mid is not partial duplicate", deduper.ShouldTranslate(Parsed("[队伍] A（英雄）: pls gank mid")), true);
AssertDedupe("ff is not partial duplicate", deduper.ShouldTranslate(Parsed("[队伍] B（英雄）: ff")), true);

deduper.Clear();
AssertDedupe("apple first translates", deduper.ShouldTranslate(Parsed("01:00 [队伍] A（英雄）: apple")), true);
AssertDedupe("i like apple after apple still translates", deduper.ShouldTranslate(Parsed("01:00 [队伍] A（英雄）: i like apple")), true);
AssertDedupe("banana first translates", deduper.ShouldTranslate(Parsed("01:01 [队伍] A（英雄）: banana")), true);
AssertDedupe("i like banana after banana still translates", deduper.ShouldTranslate(Parsed("01:01 [队伍] A（英雄）: i like banana")), true);

deduper.Clear();
AssertDedupe("new player partial is not suppressed by other player full", deduper.ShouldTranslate(Parsed(fullLine)), true);
AssertDedupe("different player partial stays new", deduper.ShouldTranslate(Parsed("02:40 [队伍] Other（英雄）: ok i like chinese too,and i also like chinese kon")), true);

deduper.Clear();
var gluedFullLine = "02:40 [队伍] Ntide07（惩戒之箭）: i from US, but i like china, i also like chinese k ong fu, but i not like panda";
var gluedPartialLine = "02:40 [队伍] Ntide07（惩戒之箭）: ifromUS, buti like china, ialsomlikechinesek";
AssertDedupe("glued full long message first translates", deduper.ShouldTranslate(Parsed(gluedFullLine)), true);
AssertDedupe("glued later first-line partial is suppressed", deduper.ShouldTranslate(Parsed(gluedPartialLine)), false, "partial_duplicate_recent_full");

deduper.Clear();
var badGlueLine = "02:40 [队伍] Ntide07（惩戒之箭）: i from US, but i like china, i also like chinese k ong fu, butinotlikepanda";
var goodGlueLine = "02:40 [队伍] Ntide07（惩戒之箭）: i from US, but i like china, i also like chinese k ong fu, but i not like panda";
AssertDedupe("bad glue long message first translates once", deduper.ShouldTranslate(Parsed(badGlueLine)), true);
AssertDedupe("good glue equivalent is duplicate after bad normalized", deduper.ShouldTranslate(Parsed(goodGlueLine)), false, "partial_duplicate_recent_full");

var rawWrappedBad = OcrLineContinuationMerger.Merge(
    ["02:21 [队伍] Ntide07（百裂冥犬）:ifromUS, butilikechina, ialsolikechinesek", "ongfu, butinotlikepanda"],
    [Line("02:21 [队伍] Ntide07（百裂冥犬）:ifromUS, butilikechina, ialsolikechinesek", 20, 10), Line("ongfu, butinotlikepanda", 5, 31)]);
AssertText(
    "raw bad wrapped line parses to final fixed body",
    ChatDeduper.ParseChatLine(rawWrappedBad.Lines[0]).Message,
    "i from US, but i like China, i also like Chinese kung fu, but i do not like panda");

static CleanedChatMessage Cleaned(string line)
{
    var parsed = ChatDeduper.ParseChatLine(line);
    return new CleanedChatMessage
    {
        RawLine = line,
        Timestamp = parsed.Timestamp,
        Channel = ChatChannel.Team,
        RawChannelText = parsed.Channel,
        OcrPlayerName = parsed.Sender,
        OcrChampionText = parsed.Champion,
        RawMessageBody = parsed.RawMessageBody,
        Message = parsed.Message
    };
}

var stabilizer = new PendingMessageStabilizer();
var firstPending = stabilizer.GetStableMessages([Cleaned(badGlueLine)], forceFlushPending: false, allowImmediateLongMessages: false, source: "auto");
if (firstPending.Count != 0)
{
    throw new InvalidOperationException($"FAILED stabilizer holds first long bad frame: count={firstPending.Count}");
}

var secondPending = stabilizer.GetStableMessages([Cleaned(goodGlueLine)], forceFlushPending: false, allowImmediateLongMessages: false, source: "pending-stability");
if (secondPending.Count != 0)
{
    throw new InvalidOperationException($"FAILED stabilizer does not release immediately after update: count={secondPending.Count}");
}

System.Threading.Thread.Sleep(520);
var tooEarlyPending = stabilizer.GetStableMessages([], forceFlushPending: false, allowImmediateLongMessages: false, source: "pending-stability");
if (tooEarlyPending.Count != 0)
{
    throw new InvalidOperationException($"FAILED stabilizer waits longer than 500ms for long pending text: count={tooEarlyPending.Count}");
}

System.Threading.Thread.Sleep(850);
var releasedPending = stabilizer.GetStableMessages([], forceFlushPending: false, allowImmediateLongMessages: false, source: "pending-stability");
if (releasedPending.Count != 1 || releasedPending[0].Message != ChatDeduper.ParseChatLine(goodGlueLine).Message)
{
    throw new InvalidOperationException($"FAILED stabilizer releases final good frame once: count={releasedPending.Count}");
}

Console.WriteLine("PASS stabilizer holds bad frame and releases final good frame once");

var shortStabilizer = new PendingMessageStabilizer();
var shortReady = shortStabilizer.GetStableMessages([Cleaned("[队伍] A（英雄）: hello")], forceFlushPending: false, allowImmediateLongMessages: false, source: "auto");
if (shortReady.Count != 1 || shortReady[0].Message != "hello")
{
    throw new InvalidOperationException($"FAILED stabilizer keeps short hello fast: count={shortReady.Count}");
}

Console.WriteLine("PASS stabilizer keeps short hello fast");

var stableDeduplicate = typeof(MainWindow).GetMethod("DeduplicateStableMessages", BindingFlags.NonPublic | BindingFlags.Static)
    ?? throw new InvalidOperationException("FAILED stable dedupe reflection: method not found");
var partialMessage = new CleanedChatMessage
{
    RawLine = partialLine,
    Timestamp = "02:40",
    Channel = ChatChannel.Team,
    RawChannelText = "队伍",
    OcrPlayerName = "Ntide07",
    OcrChampionText = "惩戒之箭",
    RawMessageBody = ChatDeduper.ParseChatLine(partialLine).RawMessageBody,
    Message = ChatDeduper.ParseChatLine(partialLine).Message
};
var fullMessage = new CleanedChatMessage
{
    RawLine = fullLine,
    Timestamp = "02:40",
    Channel = ChatChannel.Team,
    RawChannelText = "队伍",
    OcrPlayerName = "Ntide07",
    OcrChampionText = "惩戒之箭",
    RawMessageBody = ChatDeduper.ParseChatLine(fullLine).RawMessageBody,
    Message = ChatDeduper.ParseChatLine(fullLine).Message
};
var stableResult = (List<CleanedChatMessage>?)stableDeduplicate.Invoke(null, [new List<CleanedChatMessage> { partialMessage, fullMessage }]);
if (stableResult is null || stableResult.Count != 1 || stableResult[0].Message != fullMessage.Message)
{
    throw new InvalidOperationException($"FAILED partial first then full stable replacement: count={stableResult?.Count ?? -1}");
}

Console.WriteLine("PASS partial first then full stable replacement");

internal sealed record TestCase(
    string Name,
    string[] Lines,
    OcrTextLine[] TextLines,
    int ExpectedCount,
    string ExpectedFirstMessage);
'@ | Set-Content -LiteralPath (Join-Path $testDir "Program.cs") -Encoding UTF8

try {
    $outDir = Join-Path $testDir "out\"
    dotnet run --project (Join-Path $testDir "LineMergeTests.csproj") -c Release -p:OutDir="$outDir"
}
finally {
    if (Test-Path -LiteralPath $testDir) {
        Remove-Item -LiteralPath $testDir -Recurse -Force
    }
}
