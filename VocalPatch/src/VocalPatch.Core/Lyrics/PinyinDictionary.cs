using System.Text;

namespace VocalPatch.Core.Lyrics;

/// <summary>
/// Hanzi-to-pinyin lookup. Ships a built-in table of the characters that dominate
/// sung Mandarin, and can load a full external table (see <see cref="LoadTable"/>)
/// to cover the rest. Readings carry a tone digit; <see cref="PinyinConverter"/>
/// decides whether tones are kept.
/// </summary>
public static class PinyinDictionary
{
    // char=toneNumberedPinyin;. Polyphones list the primary reading first.
    private const string Builtin =
        "的=de5;一=yi1;是=shi4;不=bu4;了=le5;人=ren2;我=wo3;在=zai4;有=you3;他=ta1;" +
        "这=zhe4;个=ge4;们=men5;中=zhong1;来=lai2;上=shang4;大=da4;为=wei4;和=he2;国=guo2;" +
        "地=di4;到=dao4;以=yi3;说=shuo1;时=shi2;要=yao4;就=jiu4;出=chu1;会=hui4;可=ke3;" +
        "也=ye3;你=ni3;对=dui4;生=sheng1;能=neng2;而=er2;子=zi3;那=na4;得=de2;于=yu2;" +
        "着=zhe5;下=xia4;自=zi4;之=zhi1;年=nian2;过=guo4;发=fa1;后=hou4;作=zuo4;里=li3;" +
        "用=yong4;道=dao4;行=xing2;所=suo3;然=ran2;家=jia1;种=zhong3;事=shi4;成=cheng2;方=fang1;" +
        "多=duo1;经=jing1;么=me5;去=qu4;法=fa3;学=xue2;如=ru2;都=dou1;同=tong2;现=xian4;" +
        "当=dang1;没=mei2;动=dong4;面=mian4;起=qi3;看=kan4;定=ding4;天=tian1;分=fen1;还=hai2;" +
        "进=jin4;好=hao3;小=xiao3;部=bu4;其=qi2;些=xie1;主=zhu3;样=yang4;理=li3;心=xin1;" +
        "她=ta1;本=ben3;前=qian2;开=kai1;但=dan4;因=yin1;只=zhi3;从=cong2;想=xiang3;实=shi2;" +
        "日=ri4;军=jun1;者=zhe3;意=yi4;无=wu2;力=li4;它=ta1;与=yu3;长=chang2;把=ba3;" +
        "机=ji1;十=shi2;民=min2;第=di4;公=gong1;此=ci3;已=yi3;工=gong1;使=shi3;情=qing2;" +
        "明=ming2;性=xing4;知=zhi1;全=quan2;三=san1;又=you4;关=guan1;点=dian3;正=zheng4;业=ye4;" +
        "外=wai4;两=liang3;高=gao1;间=jian1;由=you2;问=wen4;很=hen3;最=zui4;重=zhong4;并=bing4;" +
        "物=wu4;手=shou3;应=ying1;战=zhan4;向=xiang4;头=tou2;文=wen2;体=ti3;政=zheng4;美=mei3;" +
        "相=xiang1;见=jian4;被=bei4;利=li4;什=shen2;二=er4;等=deng3;产=chan3;或=huo4;新=xin1;" +
        "己=ji3;制=zhi4;身=shen1;果=guo3;加=jia1;西=xi1;斯=si1;月=yue4;话=hua4;合=he2;" +
        "回=hui2;特=te4;代=dai4;内=nei4;信=xin4;表=biao3;化=hua4;老=lao3;给=gei3;世=shi4;" +
        "位=wei4;次=ci4;度=du4;门=men2;任=ren4;常=chang2;先=xian1;海=hai3;通=tong1;教=jiao4;" +
        "儿=er2;原=yuan2;东=dong1;声=sheng1;提=ti2;立=li4;及=ji2;比=bi3;员=yuan2;解=jie3;" +
        "水=shui3;名=ming2;真=zhen1;论=lun4;处=chu4;走=zou3;义=yi4;各=ge4;入=ru4;几=ji3;" +
        "口=kou3;认=ren4;条=tiao2;平=ping2;系=xi4;气=qi4;题=ti2;活=huo2;尔=er3;更=geng4;" +
        "别=bie2;打=da3;女=nv3;变=bian4;四=si4;神=shen2;总=zong3;何=he2;电=dian4;数=shu4;" +
        "安=an1;少=shao3;报=bao4;才=cai2;结=jie2;反=fan3;受=shou4;目=mu4;太=tai4;量=liang4;" +
        "再=zai4;感=gan3;建=jian4;务=wu4;做=zuo4;接=jie1;必=bi4;场=chang3;件=jian4;计=ji4;" +
        "管=guan3;期=qi1;市=shi4;直=zhi2;德=de2;资=zi1;命=ming4;山=shan1;金=jin1;指=zhi3;" +
        "克=ke4;许=xu3;统=tong3;区=qu1;保=bao3;至=zhi4;队=dui4;形=xing2;社=she4;便=bian4;" +
        "空=kong1;决=jue2;治=zhi4;展=zhan3;马=ma3;科=ke1;司=si1;五=wu3;基=ji1;眼=yan3;" +
        "书=shu1;非=fei1;则=ze2;听=ting1;白=bai2;却=que4;界=jie4;达=da2;光=guang1;放=fang4;" +
        "强=qiang2;即=ji2;像=xiang4;难=nan2;且=qie3;权=quan2;思=si1;王=wang2;完=wan2;色=se4;" +
        "路=lu4;记=ji4;南=nan2;品=pin3;住=zhu4;告=gao4;类=lei4;求=qiu2;据=ju4;程=cheng2;" +
        "北=bei3;边=bian1;死=si3;张=zhang1;该=gai1;交=jiao1;规=gui1;万=wan4;取=qu3;拉=la1;" +
        "格=ge2;望=wang4;觉=jue2;术=shu4;领=ling3;共=gong4;确=que4;传=chuan2;师=shi1;观=guan1;" +
        "清=qing1;今=jin1;切=qie4;院=yuan4;让=rang4;识=shi2;候=hou4;带=dai4;导=dao3;争=zheng1;" +
        "运=yun4;笑=xiao4;飞=fei1;风=feng1;步=bu4;改=gai3;收=shou1;根=gen1;干=gan4;造=zao4;" +
        "言=yan2;联=lian2;持=chi2;组=zu3;每=mei3;济=ji4;车=che1;亲=qin1;极=ji2;林=lin2;" +
        "服=fu2;快=kuai4;办=ban4;议=yi4;往=wang3;元=yuan2;英=ying1;士=shi4;证=zheng4;近=jin4;" +
        "失=shi1;转=zhuan3;夫=fu1;令=ling4;准=zhun3;布=bu4;始=shi3;怎=zen3;呢=ne5;存=cun2;" +
        "未=wei4;远=yuan3;叫=jiao4;台=tai2;单=dan1;影=ying3;具=ju4;罗=luo2;字=zi4;爱=ai4;" +
        "击=ji1;流=liu2;备=bei4;兵=bing1;连=lian2;调=diao4;深=shen1;商=shang1;算=suan4;质=zhi4;" +
        "团=tuan2;集=ji2;百=bai3;需=xu1;价=jia4;花=hua1;党=dang3;华=hua2;城=cheng2;石=shi2;" +
        "级=ji2;整=zheng3;府=fu3;离=li2;况=kuang4;亚=ya4;请=qing3;技=ji4;际=ji4;约=yue1;" +
        "示=shi4;复=fu4;病=bing4;息=xi1;究=jiu1;线=xian4;似=si4;官=guan1;火=huo3;断=duan4;" +
        "精=jing1;满=man3;支=zhi1;视=shi4;消=xiao1;越=yue4;器=qi4;容=rong2;照=zhao4;须=xu1;" +
        "九=jiu3;增=zeng1;研=yan2;写=xie3;称=cheng1;企=qi3;八=ba1;功=gong1;吗=ma5;包=bao1;" +
        "片=pian4;史=shi3;委=wei3;乎=hu1;查=cha2;轻=qing1;易=yi4;早=zao3;曾=ceng2;除=chu2;" +
        "农=nong2;找=zhao3;装=zhuang1;广=guang3;显=xian3;吧=ba5;阿=a1;李=li3;标=biao1;谈=tan2;" +
        "吃=chi1;图=tu2;念=nian4;六=liu4;引=yin3;历=li4;首=shou3;医=yi1;局=ju2;突=tu1;" +
        "专=zhuan1;费=fei4;号=hao4;尽=jin4;另=ling4;周=zhou1;较=jiao4;注=zhu4;语=yu3;仅=jin3;" +
        "考=kao3;落=luo4;青=qing1;随=sui2;选=xuan3;列=lie4;武=wu3;红=hong2;响=xiang3;虽=sui1;" +
        "推=tui1;势=shi4;参=can1;希=xi1;古=gu3;众=zhong4;构=gou4;房=fang2;半=ban4;节=jie2;" +
        "土=tu3;投=tou2;某=mou3;案=an4;黑=hei1;维=wei2;革=ge2;划=hua4;敌=di2;致=zhi4;" +
        "陈=chen2;律=lv4;足=zu2;态=tai4;护=hu4;七=qi1;兴=xing1;派=pai4;孩=hai2;验=yan4;" +
        "责=ze2;营=ying2;星=xing1;够=gou4;章=zhang1;音=yin1;跟=gen1;志=zhi4;底=di3;站=zhan4;" +
        "严=yan2;巴=ba1;例=li4;防=fang2;族=zu2;供=gong1;效=xiao4;续=xu4;施=shi1;留=liu2;" +
        "讲=jiang3;型=xing2;料=liao4;终=zhong1;答=da2;紧=jin3;黄=huang2;绝=jue2;奇=qi2;察=cha2;" +
        "母=mu3;京=jing1;段=duan4;依=yi1;批=pi1;群=qun2;称=cheng1;构=gou4;视=shi4;朋=peng2;" +
        "友=you3;山=shan1;河=he2;湖=hu2;云=yun2;雨=yu3;雪=xue3;花=hua1;草=cao3;树=shu4;" +
        "鸟=niao3;鱼=yu2;猫=mao1;狗=gou3;马=ma3;牛=niu2;羊=yang2;龙=long2;虎=hu3;兔=tu4;" +
        "夜=ye4;梦=meng4;泪=lei4;痛=tong4;伤=shang1;孤=gu1;独=du2;寂=ji4;寞=mo4;思=si1;" +
        "念=nian4;欢=huan1;喜=xi3;怒=nu4;哀=ai1;乐=le4;甜=tian2;苦=ku3;酸=suan1;辣=la4;" +
        "春=chun1;夏=xia4;秋=qiu1;冬=dong1;晨=chen2;昏=hun1;昼=zhou4;永=yong3;恒=heng2;瞬=shun4;" +
        "光=guang1;影=ying3;声=sheng1;色=se4;歌=ge1;舞=wu3;琴=qin2;弦=xian2;鼓=gu3;笛=di2;" +
        "唱=chang4;听=ting1;望=wang4;等=deng3;寻=xun2;找=zhao3;飞=fei1;奔=ben1;跑=pao3;跳=tiao4;" +
        "走=zou3;停=ting2;留=liu2;去=qu4;回=hui2;来=lai2;归=gui1;离=li2;散=san4;聚=ju4;" +
        "相=xiang1;逢=feng2;别=bie2;念=nian4;恋=lian4;恨=hen4;愁=chou2;悲=bei1;喜=xi3;忧=you1;";

    private static readonly Dictionary<char, string[]> Table = Build();

    private static Dictionary<char, string[]> Build()
    {
        var table = new Dictionary<char, string[]>();
        foreach (var entry in Builtin.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
        {
            int eq = entry.IndexOf('=');
            if (eq <= 0) continue;
            char ch = entry[0];
            string reading = entry.Substring(eq + 1);
            table[ch] = reading.Split('|');
        }
        return table;
    }

    public static int Count => Table.Count;

    public static bool TryGet(char ch, out string[] readings) => Table.TryGetValue(ch, out readings!);

    /// <summary>
    /// Merge a full external table into the dictionary. Accepts "char\tpinyin"
    /// (also "," or "=" separated), or "U+XXXX: pinyin" style lines. Lines starting
    /// with '#' are ignored. Returns how many entries were added or updated.
    /// </summary>
    public static int LoadTable(TextReader reader)
    {
        int added = 0;
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            line = line.Trim();
            if (line.Length == 0 || line[0] == '#' || line[0] == '/') continue;

            char ch;
            string rest;
            if (line.StartsWith("U+", StringComparison.OrdinalIgnoreCase))
            {
                int colon = line.IndexOf(':');
                if (colon < 0) continue;
                if (!int.TryParse(line.Substring(2, colon - 2), System.Globalization.NumberStyles.HexNumber, null, out int cp)) continue;
                ch = char.ConvertFromUtf32(cp)[0];
                rest = line.Substring(colon + 1).Trim();
            }
            else
            {
                int sep = IndexOfAny(line, '\t', '=', ',', ' ');
                if (sep <= 0) continue;
                ch = line[0];
                rest = line.Substring(sep + 1).Trim();
            }

            var readings = rest.Split('|', ',', '/').Select(s => s.Trim()).Where(s => s.Length > 0).ToArray();
            if (readings.Length == 0) continue;
            Table[ch] = readings;
            added++;
        }
        return added;
    }

    public static int LoadTable(string path)
    {
        using var reader = new StreamReader(path, Encoding.UTF8);
        return LoadTable(reader);
    }

    private static int IndexOfAny(string s, params char[] chars)
    {
        for (int i = 0; i < s.Length; i++)
            if (Array.IndexOf(chars, s[i]) >= 0) return i;
        return -1;
    }
}
