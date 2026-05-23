using System.Text.RegularExpressions;

namespace LoLChatTranslator.Services;

public static class CommonChatEnglish
{
    public static readonly string[] ObjectWords =
    [
        "apple",
        "banana",
        "panda",
        "china",
        "chinese",
        "canada",
        "japan",
        "usa",
        "us"
    ];

    public static string ObjectPattern { get; } = string.Join("|", ObjectWords.Select(Regex.Escape));

    public static bool TryNormalizeObject(string value, out string normalized)
    {
        normalized = string.Empty;
        var key = NormalizeObjectKey(value);
        normalized = key switch
        {
            "apple" => "apple",
            "banana" => "banana",
            "panda" => "panda",
            "china" => "China",
            "chinese" => "Chinese",
            "canada" => "Canada",
            "japan" => "Japan",
            "usa" => "USA",
            "us" => "US",
            _ => string.Empty
        };

        return normalized.Length > 0;
    }

    public static string NormalizeObjectOrOriginal(string value)
    {
        return TryNormalizeObject(value, out var normalized)
            ? normalized
            : value;
    }

    public static bool TryGetSimplifiedChineseObject(
        string value,
        bool personContext,
        out string translated)
    {
        translated = string.Empty;
        var key = NormalizeObjectKey(value);
        translated = key switch
        {
            "apple" => "苹果",
            "banana" => "香蕉",
            "panda" => "熊猫",
            "china" => "中国",
            "canada" => "加拿大",
            "japan" => "日本",
            "usa" or "us" => "美国",
            "chinese" => personContext ? "中国人" : "中文",
            _ => string.Empty
        };

        return translated.Length > 0;
    }

    public static string ToTraditionalChinese(string simplified)
    {
        return simplified switch
        {
            "苹果" => "蘋果",
            "香蕉" => "香蕉",
            "熊猫" => "熊貓",
            "中国" => "中國",
            "美国" => "美國",
            "加拿大" => "加拿大",
            "日本" => "日本",
            "中国人" => "中國人",
            "中文" => "中文",
            _ => simplified
        };
    }

    public static string NormalizeObjectKey(string value)
    {
        return new string((value ?? string.Empty)
            .Normalize()
            .ToLowerInvariant()
            .Where(char.IsAsciiLetter)
            .ToArray());
    }
}
