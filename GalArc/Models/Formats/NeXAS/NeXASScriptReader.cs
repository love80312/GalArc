
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
/// Each string is decoded independently. This supports mixed CP932 Japanese,
/// CP936/GBK Simplified Chinese, and UTF-8 text in the same string table.
/// </summary>
internal static class NeXASScriptReader
{
    private const uint MaxArrayCount = 1_000_000;
    private const int BinV2HeaderSize = 8;
    private const int BinV2MinimumVmCount = 3;

    private static readonly UTF8Encoding Utf8NoBom = new(false, true);
    private static readonly Encoding Utf8Strict = new UTF8Encoding(false, true);

    private static readonly Encoding ShiftJisStrict;
    private static readonly Encoding GbkStrict;

    // These characters are useful indicators of Simplified Chinese when
    // decoded as GBK. The list intentionally avoids most characters shared
    // with Japanese kanji so that ordinary Japanese text is not misclassified.
    private const string SimplifiedChineseIndicators =
        "这说时发后还为们过将实开关经间现样吗问让给没听长东书车气语图动进业头务认总见处变边队风报备难乐归办权护层宽专领产轻许标统线观调齐帮广忆买卖厂联欢岁员习马龙县丰义乌仓仪价优伟伤伪俩俭偿兰兴养冈册农冯冻净凤凭凯击刘创删剂剑剧劝协卫历厌叠启团围圆场坏块坛坝坟垄垒墙壳夺奋奖妆妇妈孙宁宠宪宾寻导尘岗岛岭币帐带庆库应庙废异弃张弯录彻忧怀态惊惧惩惫惭惯愤戏战户扩扫扬扰抚拟拢拥拦拨择挠挡挣挤挥捞损捡换捣掷揽搀搁搂搅摄摆摊撑撵撷撸撺攒敛斩旷显晓晕暂杂权杨极构枪枫柜标栈栋栏树桥桨检欢歼毁毕毙汉汤沟沥沦沪泞泪泷泸泻泼泽洁浆浇浊济浑浓涛涝润涨涩淀渔渗溃溅滚满滤滥滨滩灭灵灾炼烁烂烦烧焕爱爷牵牺犊狮狱猎玛环电畅疗疯皑皱盏盐监盘睁瞒矫矿码砖砚础硕确碍祸离秃穷窜窝窍竖竞笋筛筝筹签篮粪粮紧纠纪红纤约级纯纱纲纳纵纷纸纹纺纽线练组织终绍绎经绑绒结绕绘络绝绞统绣继绩绪续绫绰绳维绵绸综绽绿罗罚罢耻联聪肃肠肤肾肿胀胜胶脉脏脑脸腾舰舱艰艳艺芜苍苏药莲获营萤萧萨蒋蓝蔷蕴虏虑蚀蚁蚂蜡蝇衅补裤觅视览觉订认让讯记讲讳讶许讹讼讽设访诀证评诅识诈诉词译诚诛诞诡询诧诫诬误诱诲诺读谅谋谍谎谐谓谚谜谢谣谤谦谱谩谬谭谲谴贝贞负贡财责贤败账货贩贪贫贬购贮贯贱贴贸费贺贼贾贿赁赂赃资赅赈赊赋赌赎赏赐赔赖赚赛赞赠赢赵赶趋跃践蹿躏车轨轩转轮软轰轴载较辆辈辉辊辑输辖辗辽达迁迈远违连迟适选遗邮邻郑酝酱醋酿释钟钢钦钧钩钮钱钳钻钾铁铃铅铜铝铭铲银铸铺链销锁锅锈锋锐错锡锤锦键锯镇镜门闪闭问闯闲间闷闹闸闻阅阀阎阐阔队阳阴阵际陆陈险随隐难韦页顶项顺须顾顿颁颂预颅领颇颈频颖颗题颜额风飞饭饮饰饱饲饵饶饺饼饿馋馆馈馅马驭驮驯驰驱驳驴驶驻驼驾骂骄骆骇验骏骑骗骚鲁鲜鲤鱼鸟鸣鸥鸦鸭鸯鸳鹅鹏鹤鹰麦齐齿龄龙龟";

    private static readonly HashSet<char> SimplifiedChineseIndicatorSet =
        new(SimplifiedChineseIndicators);

    // Traditional Chinese characters that are uncommon in Japanese text.
    // They help distinguish UTF-8 Traditional Chinese from CP932/GBK mojibake.
    private const string TraditionalChineseIndicators =
        "這說時發後還為們過將實開關經間現樣嗎問讓給沒聽長東書車氣語圖動進業頭務認總見處變邊隊風報備難樂歸辦權護層寬專領產輕許標統線觀調齊幫廣憶買賣廠聯歡歲員馬龍縣豐義烏倉儀價優偉傷偽倆儉償蘭興養冊農馮凍淨鳳憑凱擊劉創刪劑劍劇勸協衛歷厭疊啟團圍圓場壞塊壇壩墳壟壘牆殼奪奮獎妝婦媽孫寧寵憲賓尋導塵崗島嶺幣帳帶慶庫應廟廢異棄張彎錄徹憂懷態驚懲憤戲戰戶擴掃揚擾撫擬攏擁攔撥擇撓擋掙擠揮撈損撿換搗擲攬擱攪攝擺攤撐斂斬曠顯曉暈暫雜楊極構槍楓櫃標棧棟欄樹橋槳檢歡殲毀畢斃漢湯溝瀝淪滬淚瀧瀘瀉潑澤潔漿澆濁濟渾濃濤澇潤漲澀澱漁滲潰濺滾滿濾濫濱灘滅靈災煉爍爛煩燒煥愛爺牽犧犢獅獄獵瑪環電暢療瘋皚皺盞鹽監盤瞞矯礦碼磚硯礎碩確礙禍離禿窮窩竅豎競筍篩箏籌籤籃糞糧緊糾紀紅纖約級純紗綱納縱紛紙紋紡紐線練組織終紹繹經綁絨結繞繪絡絕絞統繡繼績緒續綾綽繩維綿綢綜綻綠羅罰罷恥聯聰肅腸膚腎腫脹勝膠脈臟腦臉騰艦艙艱艷藝蕪蒼蘇藥蓮獲營螢蕭薩蔣藍薔蘊虜慮蝕蟻蠟蠅釁補褲覓視覽覺訂認讓訊記講諱訝許訛訟諷設訪訣證評詛識詐訴詞譯誠誅誕詭詢詫誡誣誤誘誨諾讀諒謀諜謊諧謂諺謎謝謠謗謙譜謾謬譚譎譴貝貞負貢財責賢敗帳貨販貪貧貶購貯貫賤貼貿費賀賊賈賄賃賂贓資賅賑賒賦賭贖賞賜賠賴賺賽贊贈贏趙趕趨躍踐躥躪車軌軒轉輪軟轟軸載較輛輩輝輥輯輸轄輾遼達遷邁遠違連遲適選遺郵鄰鄭醞醬醋釀釋鐘鋼欽鈞鉤鈕錢鉗鑽鉀鐵鈴鉛銅鋁銘鏟銀鑄鋪鏈銷鎖鍋鏽鋒銳錯錫錘錦鍵鋸鎮鏡門閃閉問闖閒間悶鬧閘聞閱閥閻闡闊隊陽陰陣際陸陳險隨隱難韋頁頂項順須顧頓頒頌預顱領頗頸頻穎顆題顏額風飛飯飲飾飽飼餌饒餃餅餓饞館饋餡馬馭馱馴馳驅駁驢駛駐駝駕罵驕駱駭驗駿騎騙騷魯鮮鯉魚鳥鳴鷗鴉鴨鴦鴛鵝鵬鶴鷹麥齊齒齡龍龜誰謝體國學對單當點萬無聲廣轉換應該機畫傳說親園總變價讀寫資料檔案畫面";

    private static readonly HashSet<char> TraditionalChineseIndicatorSet =
        new(TraditionalChineseIndicators);

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

    private static readonly string[] CommonTraditionalChinesePhrases =
    [
        "你好", "謝謝", "對不起", "沒關係", "為什麼", "怎麼了", "是這樣嗎",
        "我不知道", "我知道", "可以嗎", "不可以", "沒問題", "不要緊", "真的嗎",
        "你在做什麼", "你是誰", "你知道嗎", "我很好", "我愛你", "我想回家",
        "歡迎", "遊戲", "對話", "這裡", "現在", "我們", "你們", "開始", "結束",
        "繼續", "退出", "選擇", "設定", "讀取", "儲存", "確認", "取消",
        "檔案", "畫面", "聲音", "繁體中文"
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
    /// Decodes one string independently as CP932, CP936, or UTF-8.
    /// Strong script-specific evidence takes priority; the selected encoding
    /// is used as the fallback for ambiguous strings.
    /// </summary>
    private static string DecodeText(byte[] bytes, Encoding preferredEncoding)
    {
        if (bytes == null || bytes.Length == 0)
        {
            return string.Empty;
        }

        int preferredCodePage = preferredEncoding.CodePage;
        if (preferredCodePage != 932
            && preferredCodePage != 936
            && preferredCodePage != 65001)
        {
            return preferredEncoding.GetString(bytes);
        }

        bool hasShiftJis = TryDecodeStrict(ShiftJisStrict, bytes, out string shiftJisText);
        bool hasGbk = TryDecodeStrict(GbkStrict, bytes, out string gbkText);
        bool hasUtf8 = TryDecodeStrict(Utf8Strict, bytes, out string utf8Text);

        int validEncodingCount = (hasShiftJis ? 1 : 0)
            + (hasGbk ? 1 : 0)
            + (hasUtf8 ? 1 : 0);

        // When only one decoder accepts the byte sequence, use that decoder.
        if (validEncodingCount == 1)
        {
            if (hasShiftJis)
            {
                return shiftJisText;
            }
            if (hasGbk)
            {
                return gbkText;
            }
            return utf8Text;
        }

        int japaneseScore = hasShiftJis ? GetJapaneseScore(shiftJisText) : -1;
        int chineseScore = hasGbk ? GetSimplifiedChineseScore(gbkText) : -1;

        int utf8Score = -1;
        if (hasUtf8)
        {
            int utf8JapaneseScore = GetJapaneseScore(utf8Text);
            int utf8SimplifiedScore = GetSimplifiedChineseScore(utf8Text);
            int utf8TraditionalScore = GetTraditionalChineseScore(utf8Text);

            utf8Score = Math.Max(
                utf8JapaneseScore,
                Math.Max(utf8SimplifiedScore, utf8TraditionalScore));
        }

        // Keep the original CP932/CP936 detection behavior, but compare the
        // UTF-8 candidate as well so mixed-encoding script tables are handled.
        if (hasShiftJis
            && japaneseScore >= 10
            && japaneseScore >= Math.Max(chineseScore, utf8Score) + 3)
        {
            return shiftJisText;
        }

        if (hasGbk
            && chineseScore >= 8
            && chineseScore >= Math.Max(japaneseScore, utf8Score) + 3)
        {
            return gbkText;
        }

        if (hasUtf8
            && utf8Score >= 8
            && utf8Score >= Math.Max(japaneseScore, chineseScore) + 3)
        {
            return utf8Text;
        }

        // For ambiguous strings, preserve the selected encoding.
        if (preferredCodePage == 932 && hasShiftJis)
        {
            return shiftJisText;
        }
        if (preferredCodePage == 936 && hasGbk)
        {
            return gbkText;
        }
        if (preferredCodePage == 65001 && hasUtf8)
        {
            return utf8Text;
        }

        // If UTF-8 was selected but the bytes are not valid UTF-8, the source
        // NeXAS script is most commonly CP932. Try CP932 before falling back
        // to GBK. This also preserves kanji-only Japanese strings that have
        // too little kana to trigger the Japanese scoring threshold.
        if (preferredCodePage == 65001)
        {
            if (hasShiftJis)
            {
                return shiftJisText;
            }
            if (hasGbk)
            {
                return gbkText;
            }
        }

        return preferredEncoding.GetString(bytes);
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

    private static int GetTraditionalChineseScore(string value)
    {
        int indicatorCount = 0;
        int cjkCount = 0;

        foreach (char character in value)
        {
            if (IsCjkCharacter(character))
            {
                cjkCount++;
            }
            if (TraditionalChineseIndicatorSet.Contains(character))
            {
                indicatorCount++;
            }
        }

        int phraseScore = 0;
        foreach (string phrase in CommonTraditionalChinesePhrases)
        {
            if (value.Contains(phrase, StringComparison.Ordinal))
            {
                phraseScore += 10;
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
