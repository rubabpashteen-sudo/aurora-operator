namespace AuroraOperator.Core;

/// <summary>Detects the language of a user instruction (Simplified Chinese,
/// Urdu or English) and provides core UI strings in each language.
/// Chinese: any CJK Unified Ideograph. Urdu: Arabic-script range.
/// Everything else defaults to English.</summary>
public static class LanguageDetector
{
    public static Language Detect(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return Language.Unknown;
        foreach (var ch in text)
        {
            if (ch >= '\u4E00' && ch <= '\u9FFF') return Language.Chinese;
            if (ch >= '\u0600' && ch <= '\u06FF') return Language.Urdu;
        }
        return Language.English;
    }

    public static string Get(Language language, string key)
    {
        if (UiStrings.TryGetValue(language, out var table) && table.TryGetValue(key, out var value))
            return value;
        if (UiStrings[Language.English].TryGetValue(key, out var fallback)) return fallback;
        return key;
    }

    public static readonly IReadOnlyDictionary<Language, IReadOnlyDictionary<string, string>> UiStrings =
        new Dictionary<Language, IReadOnlyDictionary<string, string>>
        {
            [Language.English] = new Dictionary<string, string>
            {
                ["NewTask"] = "New Task", ["Send"] = "Send", ["Stop"] = "Stop",
                ["Settings"] = "Settings", ["ModelManager"] = "Model Manager",
                ["Memory"] = "Memory", ["Tasks"] = "Tasks", ["Files"] = "Files",
                ["Activity"] = "Activity", ["Projects"] = "Projects",
                ["Thinking"] = "Thinking…", ["Completed"] = "Completed",
                ["Failed"] = "Failed", ["ApprovalRequired"] = "Approval required",
            },
            [Language.Chinese] = new Dictionary<string, string>
            {
                ["NewTask"] = "新任务", ["Send"] = "发送", ["Stop"] = "停止",
                ["Settings"] = "设置", ["ModelManager"] = "模型管理",
                ["Memory"] = "记忆", ["Tasks"] = "任务", ["Files"] = "文件",
                ["Activity"] = "活动", ["Projects"] = "项目",
                ["Thinking"] = "思考中…", ["Completed"] = "已完成",
                ["Failed"] = "失败", ["ApprovalRequired"] = "需要批准",
            },
            [Language.Urdu] = new Dictionary<string, string>
            {
                ["NewTask"] = "نیا کام", ["Send"] = "بھیجیں", ["Stop"] = "روکیں",
                ["Settings"] = "ترتیبات", ["ModelManager"] = "ماڈل مینیجر",
                ["Memory"] = "یادداشت", ["Tasks"] = "کام", ["Files"] = "فائلیں",
                ["Activity"] = "سرگرمی", ["Projects"] = "منصوبے",
                ["Thinking"] = "سوچ رہا ہے…", ["Completed"] = "مکمل",
                ["Failed"] = "ناکام", ["ApprovalRequired"] = "منظوری درکار ہے",
            },
        };
}
