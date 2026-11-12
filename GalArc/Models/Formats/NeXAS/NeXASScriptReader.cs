using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace GalArc.Models.Formats.NeXAS;

internal enum NeXASScriptFormat
{
    NullSeparatedTextTable,
    BinV2
}

internal sealed class NeXASScriptSegment
{
    public int Index { get; init; }
    public byte[] OriginalBytes { get; init; }
    public int FollowingNulls { get; init; }
    public bool IsNotText { get; init; }
    public string Text { get; init; }

    // BINv2 mapping. These values are ignored by the original text-table format.
    public int BlockIndex { get; init; } = -1;
    public int StringIndex { get; init; } = -1;
}

internal sealed class NeXASBinV2Block
{
    public int Offset { get; init; }
    public int EndOffset { get; init; }

    // Everything from the block start through the VM array, excluding the
    // four-byte count of the primary Strings array.
    public byte[] PrefixBytes { get; init; }

    // The two non-primary string arrays, including their counts and terminators.
    public byte[] SuffixBytes { get; init; }

    public IReadOnlyList<NeXASScriptSegment> Segments { get; init; }
}

internal sealed class NeXASScriptDocument
{
    public NeXASScriptFormat Format { get; init; } = NeXASScriptFormat.NullSeparatedTextTable;
    public int FirstTextOffset { get; init; }
    public int TailOffset { get; init; }
    public IReadOnlyList<NeXASScriptSegment> Segments { get; init; }
    public IReadOnlyList<NeXASBinV2Block> BinV2Blocks { get; init; }
}

/// <summary>
/// Reads the two script layouts encountered in NeXAS BIN files:
/// the NUL-separated text table used by zbspac and the BINv2 structure
/// (version byte + VM array + three counted CString arrays).
///
/// Each string is decoded independently. This is necessary because some
/// NeXAS BIN files contain both CP932 Japanese strings and CP936/GBK
/// Simplified Chinese strings in the same string table.
/// </summary>
internal static class NeXASScriptReader
{
    private const uint MaxArrayCount = 1_000_000;
    private const int BinV2HeaderSize = 8;
    private const int BinV2MinimumVmCount = 3;
    private static readonly UTF8Encoding Utf8NoBom = new(false, true);

    private static readonly Encoding ShiftJisStrict;
    private static readonly Encoding GbkStrict;

    // These characters are useful indicators of Simplified Chinese when
    // decoded as GBK. The list intentionally avoids most characters shared
    // with Japanese kanji so that ordinary Japanese text is not misclassified.
    private const string SimplifiedChineseIndicators =
        "这说时发后还为们过将实开关经间现样吗问让给没听长东书车气语图动进业头务认总见处变边队风报备难乐归办权护层宽专领产轻许标统线观调齐帮广忆买卖厂联欢岁员习马龙县丰义乌仓仪价优伟伤伪俩俭偿兰兴养冈册农冯冻净凤凭凯击刘创删剂剑剧劝协卫历厌叠启团围圆场坏块坛坝坟垄垒墙壳夺奋奖妆妇妈孙宁宠宪宾寻导尘岗岛岭币帐带庆库应庙废异弃张弯录彻忧怀态惊惧惩惫惭惯愤戏战户扩扫扬扰抚拟拢拥拦拨择挠挡挣挤挥捞损捡换捣掷揽搀搁搂搅摄摆摊撑撵撷撸撺攒敛斩旷显晓晕暂杂权杨极构枪枫柜标栈栋栏树桥桨检欢歼毁毕毙汉汤沟沥沦沪泞泪泷泸泻泼泽洁浆浇浊济浑浓涛涝润涨涩淀渔渗溃溅滚满滤滥滨滩灭灵灾炼烁烂烦烧焕爱爷牵牺犊狮狱猎玛环电畅疗疯皑皱盏盐监盘睁瞒矫矿码砖砚础硕确碍祸离秃穷窜窝窍竖竞笋筛筝筹签篮粪粮紧纠纪红纤约级纯纱纲纳纵纷纸纹纺纽线练组织终绍绎经绑绒结绕绘络绝绞统绣继绩绪续绫绰绳维绵绸综绽绿罗罚罢耻联聪肃肠肤肾肿胀胜胶脉脏脑脸腾舰舱艰艳艺芜苍苏药莲获营萤萧萨蒋蓝蔷蕴虏虑蚀蚁蚂蜡蝇衅补裤觅视览觉订认让讯记讲讳讶许讹讼讽设访诀证评诅识诈诉词译诚诛诞诡询诧诫诬误诱诲诺读谅谋谍谎谐谓谚谜谢谣谤谦谱谩谬谭谲谴贝贞负贡财责贤败账货贩贪贫贬购贮贯贱贴贸费贺贼贾贿赁赂赃资赅赈赊赋赌赎赏赐赔赖赚赛赞赠赢赵赶趋跃践蹿躏车轨轩转轮软轰轴载较辆辈辉辊辑输辖辗辽达迁迈远违连迟适选遗邮邻郑酝酱醋酿释钟钢钦钧钩钮钱钳钻钾铁铃铅铜铝铭铲银铸铺链销锁锅锈锋锐错锡锤锦键锯镇镜门闪闭问闯闲间闷闹闸闻阅阀阎阐阔队阳阴阵际陆陈险随隐难韦页顶项顺须顾顿颁颂预颅领颇颈频颖颗题颜额风飞饭饮饰饱饲饵饶饺饼饿馋馆馈馅马驭驮驯驰驱驳驴驶驻驼驾骂骄骆骇验骏骑骗骚鲁鲜鲤鱼鸟鸣鸥鸦鸭鸯鸳鹅鹏鹤鹰麦齐齿龄龙龟";

    private static readonly HashSet<char> SimplifiedChineseIndicatorSet = new(SimplifiedChineseIndicators);

    private static readonly string[] CommonSimplifiedChinesePhrases =
    [
        "你好", "中文", "好的", "不是", "取消", "确定", "开始", "结束", "游戏", "加载",
        "欢迎", "谢谢", "继续", "退出", "返回", "保存", "读取", "失败", "成功",
        "选项", "设置", "选择", "确认", "错误", "无效", "无法", "已经", "需要",
        "玩家", "角色", "任务", "现在", "可以", "不能", "什么", "为什么", "这里",
        "那里", "因为", "所以", "如果", "然后", "请", "不要", "没有", "是否",
        "知道", "喜欢", "大家", "我们", "他们", "你们", "一个", "这个", "那个",
        "怎么", "以后", "简体中文", "简体", "当前", "继续进行", "重新启动", "测试",
        "测试文本", "对话", "剧情", "菜单", "按钮", "读取中", "加载中", "正在加载",
        "游戏开始", "开始游戏", "退出游戏", "游戏结束", "重新开始", "无法继续", "设置完成"
    ];

    static NeXASScriptReader()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        ShiftJisStrict = CreateStrictEncoding(932);
        GbkStrict = CreateStrictEncoding(936);
    }

    public static bool TryExtract(
        byte[] data,
        string textFilePath,
        Encoding textEncoding,
        out int segmentCount)
    {
        return TryExtract(data, textFilePath, textEncoding, out segmentCount, out _);
    }

    public static bool TryExtract(
        byte[] data,
        string textFilePath,
        Encoding textEncoding,
        out int segmentCount,
        out string reason)
    {
        segmentCount = 0;
        if (!TryParse(data, textEncoding, out NeXASScriptDocument document, out reason))
        {
            return false;
        }

        StringBuilder output = new();
        foreach (NeXASScriptSegment segment in document.Segments)
        {
            output.Append(NeXASScriptWriter.EscapeText(segment.Text));
            output.Append("\r\n");
        }

        string directory = Path.GetDirectoryName(textFilePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(textFilePath, output.ToString(), Utf8NoBom);
        segmentCount = document.Segments.Count;
        return true;
    }

    internal static bool TryParse(
        byte[] data,
        Encoding textEncoding,
        out NeXASScriptDocument document)
    {
        return TryParse(data, textEncoding, out document, out _);
    }

    internal static bool TryParse(
        byte[] data,
        Encoding textEncoding,
        out NeXASScriptDocument document,
        out string reason)
    {
        document = null;
        reason = null;
        if (data == null || data.Length < 16 || textEncoding == null)
        {
            reason = "BIN is null, too small, or no text encoding was selected.";
            return false;
        }

        // BINv2 stores two UInt32 fields here. Reading them as one UInt64
        // produces misleading values such as 0x000008FC00000000 (2300 << 32).
        if (TryParseBinV2(data, textEncoding, out document, out reason))
        {
            return true;
        }

        // If the file begins with a plausible BINv2 header but its arrays are
        // malformed, do not reinterpret the same bytes as the older text table.
        if (LooksLikeBinV2Header(data))
        {
            return false;
        }

        return TryParseNullSeparatedTable(data, textEncoding, out document, out reason);
    }

    private static bool TryParseBinV2(
        byte[] data,
        Encoding textEncoding,
        out NeXASScriptDocument document,
        out string reason)
    {
        document = null;
        reason = null;

        // The upstream dispatcher selects the BINv2 engine when the first
        // DWORD is zero. Avoid scanning unrelated legacy BIN payloads.
        if (!LooksLikeBinV2Header(data))
        {
            return false;
        }

        List<NeXASBinV2Block> blocks = [];
        List<NeXASScriptSegment> allSegments = [];
        int position = 0;

        // BINv2 is a structure embedded in the script binary. The reference
        // implementation scans byte-by-byte for structurally valid blocks;
        // after a valid block, continue at its exact end to avoid overlaps.
        while (position <= data.Length - BinV2HeaderSize)
        {
            if (TryReadBinV2Block(data, position, textEncoding,
                allSegments.Count, blocks.Count,
                out NeXASBinV2Block block,
                out List<NeXASScriptSegment> blockSegments))
            {
                blocks.Add(block);
                allSegments.AddRange(blockSegments);
                position = block.EndOffset;
                continue;
            }

            position++;
        }

        if (blocks.Count == 0 || allSegments.Count == 0)
        {
            if (LooksLikeBinV2Header(data))
            {
                reason = "BINv2 header detected, but its VM or CString arrays failed structural validation.";
            }
            return false;
        }

        document = new NeXASScriptDocument
        {
            Format = NeXASScriptFormat.BinV2,
            FirstTextOffset = 0,
            TailOffset = data.Length,
            Segments = allSegments,
            BinV2Blocks = blocks
        };
        reason = $"Parsed BINv2: {blocks.Count} block(s), {allSegments.Count} primary strings.";
        return true;
    }

    private static bool TryReadBinV2Block(
        byte[] data,
        int offset,
        Encoding textEncoding,
        int firstSegmentIndex,
        int blockIndex,
        out NeXASBinV2Block block,
        out List<NeXASScriptSegment> segments)
    {
        block = null;
        segments = null;

        if (offset < 0 || offset > data.Length - BinV2HeaderSize)
        {
            return false;
        }

        uint version = ReadUInt32LittleEndian(data, offset);
        if ((version & 0xFFFFFF00u) != 0)
        {
            return false;
        }

        uint vmCount = ReadUInt32LittleEndian(data, offset + 4);
        if (vmCount < BinV2MinimumVmCount || vmCount > MaxArrayCount)
        {
            return false;
        }

        long primaryCountOffset64 = (long)offset + BinV2HeaderSize + (long)vmCount * 8;
        if (primaryCountOffset64 > data.Length - 4)
        {
            return false;
        }

        int primaryCountOffset = (int)primaryCountOffset64;
        uint primaryCount = ReadUInt32LittleEndian(data, primaryCountOffset);
        if (primaryCount == 0 || primaryCount > MaxArrayCount)
        {
            return false;
        }

        // Match the upstream BINv2 signature check. The UInt64 spans the last
        // DWORD of the VM array and the primary string count.
        int probeOffset = primaryCountOffset - 4;
        ulong probe = ReadUInt64LittleEndian(data, probeOffset);
        if ((probe & 0xFF000000FF000000UL) != 0 || (probe >> 32) == 0)
        {
            return false;
        }

        int firstStringOffset = primaryCountOffset + 4;
        int printableBytes = 0;
        // Do not use the selected text code page as a structural limit here:
        // this table can contain CP932 and CP936 strings side by side.
        for (int i = 0; i < 10 && firstStringOffset + i < data.Length; i++)
        {
            byte value = data[firstStringOffset + i];
            if (value > 0x20 && value < 0xFF)
            {
                printableBytes++;
            }
        }
        if (printableBytes < 7)
        {
            return false;
        }

        int vmArrayEnd = primaryCountOffset;
        int cursor = firstStringOffset;
        List<NeXASScriptSegment> parsedSegments = new(checked((int)primaryCount));
        for (int i = 0; i < primaryCount; i++)
        {
            int terminator = Array.IndexOf(data, (byte)0, cursor);
            if (terminator < 0)
            {
                return false;
            }

            int length = terminator - cursor;
            byte[] raw = new byte[length];
            if (length > 0)
            {
                Buffer.BlockCopy(data, cursor, raw, 0, length);
            }

            // Decode each CString independently. OriginalBytes is retained so
            // unchanged lines can be written back byte-for-byte by the writer.
            string text = DecodeText(raw, textEncoding);

            parsedSegments.Add(new NeXASScriptSegment
            {
                Index = firstSegmentIndex + i,
                OriginalBytes = raw,
                FollowingNulls = 1,
                IsNotText = false,
                Text = text,
                BlockIndex = blockIndex,
                StringIndex = i
            });

            cursor = terminator + 1;
        }

        int primaryStringsEnd = cursor;
        if (!TrySkipCStringArray(data, ref cursor) || !TrySkipCStringArray(data, ref cursor))
        {
            return false;
        }

        int endOffset = cursor;
        if (endOffset <= offset || endOffset > data.Length)
        {
            return false;
        }

        byte[] prefix = new byte[vmArrayEnd - offset];
        Buffer.BlockCopy(data, offset, prefix, 0, prefix.Length);

        byte[] suffix = new byte[endOffset - primaryStringsEnd];
        Buffer.BlockCopy(data, primaryStringsEnd, suffix, 0, suffix.Length);

        block = new NeXASBinV2Block
        {
            Offset = offset,
            EndOffset = endOffset,
            PrefixBytes = prefix,
            SuffixBytes = suffix,
            Segments = parsedSegments
        };
        segments = parsedSegments;
        return true;
    }

    private static bool TrySkipCStringArray(byte[] data, ref int position)
    {
        if (position < 0 || position > data.Length - 4)
        {
            return false;
        }

        uint count = ReadUInt32LittleEndian(data, position);
        position += 4;
        if (count > MaxArrayCount || count > data.Length - position)
        {
            return false;
        }

        for (uint i = 0; i < count; i++)
        {
            int terminator = Array.IndexOf(data, (byte)0, position);
            if (terminator < 0)
            {
                return false;
            }
            position = terminator + 1;
        }

        return true;
    }

    private static bool LooksLikeBinV2Header(byte[] data)
    {
        if (data == null || data.Length < BinV2HeaderSize)
        {
            return false;
        }

        uint version = ReadUInt32LittleEndian(data, 0);
        uint vmCount = ReadUInt32LittleEndian(data, 4);
        if (version != 0 || vmCount < BinV2MinimumVmCount)
        {
            return false;
        }

        long primaryCountOffset = (long)BinV2HeaderSize + (long)vmCount * 8;
        return primaryCountOffset <= data.Length - 4;
    }

    private static bool TryParseNullSeparatedTable(
        byte[] data,
        Encoding textEncoding,
        out NeXASScriptDocument document,
        out string reason)
    {
        document = null;
        reason = null;

        // Original NeXAS text-table header: the first UInt64 is n;
        // the NUL-separated text area begins at (n + 1) * 8.
        ulong headerValue = ReadUInt64LittleEndian(data, 0);
        if (headerValue >= (ulong)data.Length / 8)
        {
            reason = $"Header value {headerValue} points outside the BIN length {data.Length}.";
            return false;
        }

        ulong textOffsetValue = (headerValue + 1) * 8;
        if (textOffsetValue >= (ulong)data.Length || textOffsetValue > int.MaxValue)
        {
            reason = $"Calculated text offset {textOffsetValue} is outside the BIN length {data.Length}.";
            return false;
        }

        int firstTextOffset = (int)textOffsetValue;
        while (firstTextOffset < data.Length && data[firstTextOffset] == 0)
        {
            firstTextOffset++;
        }

        if (firstTextOffset >= data.Length || IsFooterByte(data[firstTextOffset]))
        {
            reason = $"No text segment found at calculated text offset {textOffsetValue}.";
            return false;
        }

        List<NeXASScriptSegment> segments = [];
        int position = firstTextOffset;
        int tailOffset = data.Length;

        while (position < data.Length)
        {
            byte firstByte = data[position];
            if (IsFooterByte(firstByte))
            {
                tailOffset = position;
                break;
            }

            int terminator = Array.IndexOf(data, (byte)0, position);
            if (terminator < 0)
            {
                reason = $"Text segment beginning at BIN offset {position} has no NUL terminator.";
                return false;
            }

            int rawLength = terminator - position;
            if (rawLength <= 0)
            {
                reason = $"Empty text segment found at BIN offset {position}.";
                return false;
            }

            byte[] rawText = new byte[rawLength];
            Buffer.BlockCopy(data, position, rawText, 0, rawLength);
            string text = DecodeText(rawText, textEncoding);
            bool isNotText = IsAsciiDigit(firstByte)
                || IsAsciiUpper(firstByte)
                || text.EndsWith(".bin", StringComparison.OrdinalIgnoreCase);

            position = terminator;
            int followingNulls = 0;
            while (position < data.Length && data[position] == 0)
            {
                position++;
                followingNulls++;
            }

            segments.Add(new NeXASScriptSegment
            {
                Index = segments.Count,
                OriginalBytes = rawText,
                FollowingNulls = followingNulls,
                IsNotText = isNotText,
                Text = text
            });
        }

        if (segments.Count == 0)
        {
            reason = "No NUL-separated text segments were found.";
            return false;
        }

        document = new NeXASScriptDocument
        {
            Format = NeXASScriptFormat.NullSeparatedTextTable,
            FirstTextOffset = firstTextOffset,
            TailOffset = tailOffset,
            Segments = segments,
            BinV2Blocks = Array.Empty<NeXASBinV2Block>()
        };
        reason = $"Parsed {segments.Count} text segments.";
        return true;
    }

    /// <summary>
    /// Decodes one string as CP932 or CP936 when there is a clear script
    /// signature. The selected encoding remains the fallback for ambiguous
    /// strings and for other configured encodings.
    /// </summary>
    private static string DecodeText(byte[] bytes, Encoding preferredEncoding)
    {
        if (bytes == null || bytes.Length == 0)
        {
            return string.Empty;
        }

        int preferredCodePage = preferredEncoding.CodePage;
        if (preferredCodePage != 932 && preferredCodePage != 936)
        {
            return preferredEncoding.GetString(bytes);
        }

        bool hasShiftJis = TryDecodeStrict(ShiftJisStrict, bytes, out string shiftJisText);
        bool hasGbk = TryDecodeStrict(GbkStrict, bytes, out string gbkText);

        if (hasShiftJis && !hasGbk)
        {
            return shiftJisText;
        }
        if (hasGbk && !hasShiftJis)
        {
            return gbkText;
        }
        if (!hasShiftJis && !hasGbk)
        {
            return preferredEncoding.GetString(bytes);
        }

        int japaneseScore = GetJapaneseScore(shiftJisText);
        int chineseScore = GetSimplifiedChineseScore(gbkText);

        // Require a meaningful gap. When the evidence is weak, preserve the
        // user's selected code page instead of making an unsafe guess.
        if (japaneseScore >= 10 && japaneseScore >= chineseScore + 3)
        {
            return shiftJisText;
        }
        if (chineseScore >= 8 && chineseScore >= japaneseScore + 3)
        {
            return gbkText;
        }

        return preferredCodePage == 932 ? shiftJisText : gbkText;
    }

    private static int GetJapaneseScore(string value)
    {
        int kanaCount = CountFullWidthJapaneseKana(value);
        if (kanaCount == 0)
        {
            return 0;
        }

        int scriptCharacterCount = CountCjkCharacters(value) + kanaCount;
        double kanaRatio = kanaCount / (double)Math.Max(1, scriptCharacterCount);
        int score = kanaCount * 3;
        if (kanaCount >= 2 && kanaRatio >= 0.08)
        {
            score += 6;
        }

        return score;
    }

    private static int GetSimplifiedChineseScore(string value)
    {
        int indicatorCount = 0;
        int cjkCount = 0;
        foreach (char character in value)
        {
            if (IsCjkCharacter(character))
            {
                cjkCount++;
            }
            if (SimplifiedChineseIndicatorSet.Contains(character))
            {
                indicatorCount++;
            }
        }

        int phraseScore = 0;
        foreach (string phrase in CommonSimplifiedChinesePhrases)
        {
            if (value.Contains(phrase, StringComparison.Ordinal))
            {
                phraseScore += 12;
            }
        }

        if (indicatorCount == 0)
        {
            return phraseScore;
        }

        int score = indicatorCount * 4;
        double indicatorRatio = indicatorCount / (double)Math.Max(1, cjkCount);
        if (indicatorCount >= 2)
        {
            score += 4;
        }
        else if (indicatorRatio >= 0.15)
        {
            // A single distinguishing simplified character can be meaningful
            // in a short string such as a two-character label.
            score += 4;
        }

        return score + phraseScore;
    }

    private static int CountFullWidthJapaneseKana(string value)
    {
        int count = 0;
        foreach (char character in value)
        {
            if ((character >= '\u3040' && character <= '\u309F')
                || (character >= '\u30A0' && character <= '\u30FF')
                || (character >= '\u31F0' && character <= '\u31FF'))
            {
                count++;
            }
        }

        // Half-width katakana is deliberately not counted. GBK text decoded
        // as Shift-JIS often turns into random half-width katakana sequences.
        return count;
    }

    private static int CountCjkCharacters(string value)
    {
        int count = 0;
        foreach (char character in value)
        {
            if (IsCjkCharacter(character))
            {
                count++;
            }
        }
        return count;
    }

    private static bool IsCjkCharacter(char character)
    {
        return (character >= '\u3400' && character <= '\u4DBF')
            || (character >= '\u4E00' && character <= '\u9FFF')
            || (character >= '\uF900' && character <= '\uFAFF');
    }

    private static bool TryDecodeStrict(Encoding encoding, byte[] bytes, out string value)
    {
        try
        {
            value = encoding.GetString(bytes);
            return true;
        }
        catch (DecoderFallbackException)
        {
            value = null;
            return false;
        }
    }

    private static Encoding CreateStrictEncoding(int codePage)
    {
        return Encoding.GetEncoding(
            codePage,
            EncoderFallback.ExceptionFallback,
            DecoderFallback.ExceptionFallback);
    }

    private static bool IsFooterByte(byte value)
    {
        return value < 0x20 || value == 0xFF;
    }

    private static uint ReadUInt32LittleEndian(byte[] data, int offset)
    {
        return (uint)(data[offset]
            | (data[offset + 1] << 8)
            | (data[offset + 2] << 16)
            | (data[offset + 3] << 24));
    }

    private static ulong ReadUInt64LittleEndian(byte[] data, int offset)
    {
        return (ulong)ReadUInt32LittleEndian(data, offset)
            | ((ulong)ReadUInt32LittleEndian(data, offset + 4) << 32);
    }

    private static bool IsAsciiDigit(byte value) => value >= (byte)'0' && value <= (byte)'9';
    private static bool IsAsciiUpper(byte value) => value >= (byte)'A' && value <= (byte)'Z';
}
