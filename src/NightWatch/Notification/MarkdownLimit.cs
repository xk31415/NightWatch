using System.Text;

namespace NightWatch.Notification;

/// <summary>
/// markdown 消息的 UTF-8 字节上限与截断。
/// 企微群机器人 markdown 的 content 上限是 4096 字节，超长时服务端整条拒收（不是截断），
/// 所以发送前必须自己按行截断并留下「已截断 N 字节」标记；钉钉按同一上限保守处理。
/// </summary>
internal static class MarkdownLimit
{
    /// <summary>企微 markdown content 上限（字节）；钉钉沿用同值保守处理。</summary>
    public const int WebhookMaxBytes = 4096;

    private const string TruncatedMarker = "\n\n…（已截断 {0} 字节）";

    /// <summary>
    /// 超过 <paramref name="maxBytes"/> 个 UTF-8 字节时，按行截断（优先保留完整行；
    /// 第一行就超限时按字符边界硬切，至少保留部分内容），结尾追加「已截断 N 字节」。
    /// 返回值的 UTF-8 字节数保证不超过 <paramref name="maxBytes"/>。
    /// </summary>
    public static string Truncate(string text, int maxBytes)
    {
        var totalBytes = Encoding.UTF8.GetByteCount(text);
        if (totalBytes <= maxBytes)
            return text;

        // 先按最大值预留标记空间（截断后的字节数不会超过总字节数，位数只会更少）。
        var markerReserve = Encoding.UTF8.GetByteCount(string.Format(TruncatedMarker, totalBytes));
        var budget = maxBytes - markerReserve;
        if (budget <= 0)
            return CutToUtf8Bytes(text, maxBytes);

        var kept = new StringBuilder();
        var keptBytes = 0;
        foreach (var line in text.Split('\n'))
        {
            var lineBytes = Encoding.UTF8.GetByteCount(line);
            var separatorBytes = keptBytes > 0 ? 1 : 0;
            if (keptBytes + separatorBytes + lineBytes <= budget)
            {
                if (separatorBytes > 0)
                    kept.Append('\n');
                kept.Append(line);
                keptBytes += separatorBytes + lineBytes;
                continue;
            }

            if (keptBytes == 0)
            {
                // 第一行就超预算：按字符边界硬切，避免整条正文只剩标记
                var cut = CutToUtf8Bytes(line, budget);
                kept.Append(cut);
                keptBytes = Encoding.UTF8.GetByteCount(cut);
            }

            break;
        }

        var truncatedBytes = totalBytes - keptBytes;
        return kept.ToString() + string.Format(TruncatedMarker, truncatedBytes);
    }

    /// <summary>按 UTF-8 字节数截断，且不会切断代理对（emoji 等增补字符）。</summary>
    private static string CutToUtf8Bytes(string text, int maxBytes)
    {
        var bytes = 0;
        var chars = 0;
        while (chars < text.Length)
        {
            var rune = Rune.GetRuneAt(text, chars);
            if (bytes + rune.Utf8SequenceLength > maxBytes)
                break;
            bytes += rune.Utf8SequenceLength;
            chars += rune.Utf16SequenceLength;
        }

        return text[..chars];
    }
}
