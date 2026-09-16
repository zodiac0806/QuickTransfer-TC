using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace QuickTransfer;

/// <summary>
/// [TC] 翻譯外部化。
///
/// 程式碼裡一律保留**英文原文**當鍵值，譯文放在外部的 <c>LanguageChineseTraditional.ini</c>。
/// 這樣上游改動介面文字時，diff 能乾淨套上來（英文原文還在原處），翻譯也不會因為合併而遺失。
/// 直接把英文換成中文會同時毀掉這兩件事——那是這個檔案存在的理由。
///
/// 檔案格式刻意跟艦隊其他 TC fork（AutoDuty-TC／Questionable-TC／Lifestream-TC…）完全一致：
///   原文==譯文
///   分隔符號 "=="、參數佔位 "??"、UTF-8、<c>\n</c> 代表換行、空行略過。
/// 所以譯文檔跟艦隊既有工具是互通的。
///
/// 不使用 ECommons 的 <c>.Loc()</c>：QuickTransfer 目前零 submodule、零第三方 NuGet，
/// 為了翻譯拉進整個 ECommons 並不划算，還得從此顧慮開發規範第 4 節的 ECommons 版本不變式。
/// 這裡自己讀同一個格式，約五十行。
/// </summary>
internal static class Loc
{
    public const string Separator = "==";
    public const string ParameterSymbol = "??";
    public const string FileName = "LanguageChineseTraditional.ini";

    private static readonly Dictionary<string, string> Entries = new(StringComparer.Ordinal);
    private static bool loaded;

    public static int Count => Entries.Count;

    public static void Init()
    {
        Entries.Clear();
        loaded = true;

        try
        {
            var dir = Plugin.PluginInterface.AssemblyLocation.Directory?.FullName;
            if (string.IsNullOrEmpty(dir))
                return;

            var path = Path.Combine(dir, FileName);
            if (!File.Exists(path))
            {
                Plugin.Log.Information($"[Loc] No {FileName}; falling back to source strings.");
                return;
            }

            var lines = File.ReadAllText(path, Encoding.UTF8)
                            .Replace("\r\n", "\n")
                            .Replace("\r", "\n")
                            .Split('\n');

            foreach (var raw in lines)
            {
                // 空行不是錯誤：檔尾換行必定產生一個空元素。
                if (string.IsNullOrWhiteSpace(raw))
                    continue;

                // 註解行，方便譯文檔分段。
                if (raw.StartsWith(';') || raw.StartsWith('#'))
                    continue;

                var idx = raw.IndexOf(Separator, StringComparison.Ordinal);
                if (idx <= 0)
                {
                    Plugin.Log.Warning($"[Loc] Invalid entry: {raw}");
                    continue;
                }

                var key = raw[..idx];
                var value = raw[(idx + Separator.Length)..].Replace("\\n", "\n");
                Entries[key] = value;
            }

            Plugin.Log.Information($"[Loc] Loaded {Entries.Count} entries from {FileName}.");
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, "[Loc] Failed to load localization; falling back to source strings.");
        }
    }

    /// <summary>查不到就回傳原文——缺譯文只會看到英文，不會壞掉。</summary>
    public static string L(this string source)
    {
        if (!loaded)
            Init();

        return source != null && Entries.TryGetValue(source, out var v) ? v : source;
    }

    /// <summary>帶參數的版本：原文裡用 <c>??</c> 佔位，依序replace。</summary>
    public static string L(this string source, params object[] args)
    {
        var text = source.L();
        if (args == null || args.Length == 0)
            return text;

        foreach (var arg in args)
        {
            var idx = text.IndexOf(ParameterSymbol, StringComparison.Ordinal);
            if (idx < 0)
                break;

            text = text[..idx] + arg + text[(idx + ParameterSymbol.Length)..];
        }

        return text;
    }
}
