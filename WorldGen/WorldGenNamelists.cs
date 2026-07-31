// ワールド生成用の語彙プール読込（施設/NPC/集落/ワールド/ダンジョン/クエスト/敵/イベント）。
// 供給元は FieldOptions と同じ二段構え: exe 隣 namelist\*.json（再ビルド不要で上書き）→ 埋め込み基底。
// 外部ファイルはキー単位で優先し、外部に無い・空のキーは埋め込み値のまま使う
// （スタンドアロン版など新キーを持たない古い外部ファイルでも破綻しない）。
using System.Reflection;
using System.Text;
using System.Text.Json.Nodes;

namespace InstantaleSaveEditor
{
    // namelist JSON の共通読込。外部・埋め込みの両ルートを保持し、キー単位で外部を優先する。
    internal sealed class Namelist
    {
        private readonly JsonObject _ext;   // exe 隣 namelist\*.json（無ければ null）
        private readonly JsonObject _emb;   // 埋め込みリソース

        private Namelist(JsonObject ext, JsonObject emb) { _ext = ext; _emb = emb; }

        public static Namelist Load(string fileName) => new(LoadExternal(fileName), LoadEmbedded(fileName));

        // exe 隣 namelist\{fileName} を読む（無い・壊れているときは null）。
        // 単一ファイル発行では Assembly.Location が空になるため ProcessPath を使う。
        private static JsonObject LoadExternal(string fileName)
        {
            try
            {
                string dir = Path.GetDirectoryName(Environment.ProcessPath ?? "") ?? AppContext.BaseDirectory;
                string p = Path.Combine(dir, "namelist", fileName);
                if (File.Exists(p))
                    return JsonNode.Parse(File.ReadAllText(p, Encoding.UTF8)) as JsonObject;
            }
            catch { /* 外部が壊れていても埋め込みへフォールバック */ }
            return null;
        }

        // 埋め込みリソース namelist.{fileName} を読む（無ければ null）。
        private static JsonObject LoadEmbedded(string fileName)
        {
            try
            {
                using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("namelist." + fileName);
                if (s == null) return null;
                using var r = new StreamReader(s, Encoding.UTF8);
                return JsonNode.Parse(r.ReadToEnd()) as JsonObject;
            }
            catch { return null; }
        }

        // 文字列配列キーを読む。外部の要素数が minCount 未満なら埋め込み値を使う。
        public List<string> Pool(string key, int minCount = 1)
        {
            var l = new List<string>();
            if (_ext != null) AddAll(_ext[key] as JsonArray, l);
            if (l.Count < minCount)
            {
                l.Clear();
                if (_emb != null) AddAll(_emb[key] as JsonArray, l);
            }
            return l;
        }

        // 配列キーを外部→埋め込みの順で parse にかけ、最初に非 null を返した結果を採用する
        // （構造つきキーの形式検証つき読込向け）。どちらも駄目なら null。
        public T Parse<T>(string key, Func<JsonArray, T> parse) where T : class
            => (_ext?[key] is JsonArray ea ? parse(ea) : null)
             ?? (_emb?[key] is JsonArray ba ? parse(ba) : null);

        // "categories" 配列を返す。外部 namelist はカテゴリ単位（type、facilities は type+tier）で
        // 埋め込みを上書きし、外部に書かれていないカテゴリは埋め込みの値を残す。
        // 丸ごと置換にすると、一部の施設種別だけ差し替えるつもりで外部ファイルを置いたときに、
        // 書かなかった種別の名前プールが全て消え、施設名が facility_type の文字列そのものになる。
        public JsonArray Categories()
        {
            var emb = _emb?["categories"] as JsonArray;
            var ext = _ext?["categories"] as JsonArray;
            if (ext == null || ext.Count == 0) return Copy(emb);
            if (emb == null || emb.Count == 0) return Copy(ext);

            var extKeys = new HashSet<(string type, string tier)>();
            foreach (var n in ext) if (CategoryKey(n) is { } k) extKeys.Add(k);

            var merged = Copy(ext);
            foreach (var n in emb)
                if (CategoryKey(n) is { } k && !extKeys.Contains(k)) merged.Add(n.DeepClone());
            return merged;
        }

        // カテゴリの同一性キー。facilities.json は同じ type を tier 違いで複数持つため tier も含める。
        private static (string type, string tier)? CategoryKey(JsonNode n)
            => n is JsonObject o ? (J.Str(o, "type"), J.Str(o, "tier")) : null;

        // 親付きノードは別の配列へ入れられないため複製して返す。
        private static JsonArray Copy(JsonArray a)
            => a == null ? new JsonArray() : (JsonArray)a.DeepClone();

        // "categories" 配下の names を全カテゴリ統合で集める（worlds/dungeons/quests 用）。
        public List<string> FlattenCategories()
        {
            var outp = new List<string>();
            foreach (var c in Categories())
                if (c is JsonObject o && o["names"] is JsonArray names)
                    AddAll(names, outp);
            return outp;
        }

        // 文字列配列の中身を dst へ追加する（空白のみは除外）。
        public static void AddAll(JsonArray arr, List<string> dst)
        {
            if (arr == null) return;
            foreach (var n in arr)
                if (n is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s))
                    dst.Add(s);
        }
    }

    // 施設名プール（namelist/facilities.json）。type はゲームの facility_type と一致。
    // tier(cheap/mid/high) はプール整理用で、名前選択時は同一 type の全ティア統合でも扱える。
    // ward_names は区画ロール別の名前プール（先頭から: 宿・組合／商業／行政・医療・修練／裏通り。
    // 各エリアで1つずつランダムに選ぶ）。
    internal sealed class FacilityNames
    {
        private readonly Dictionary<string, List<string>> _byType = new();
        private readonly Dictionary<(string type, string poolTier), List<string>> _byTypeTier = new();
        private List<List<string>> _wardPools;
        private static FacilityNames _cached;

        public static FacilityNames Load()
        {
            if (_cached != null) return _cached;
            var src = Namelist.Load("facilities.json");
            var inst = new FacilityNames { _wardPools = src.Parse("ward_names", ParseWardPools) };
            foreach (var c in src.Categories())
            {
                if (c is not JsonObject o) continue;
                string type = J.Str(o, "type");
                if (string.IsNullOrEmpty(type) || o["names"] is not JsonArray names) continue;
                string poolTier = J.Str(o, "tier");   // cheap/mid/high（無ければ ""）
                if (!inst._byType.TryGetValue(type, out var list))
                    inst._byType[type] = list = new List<string>();
                if (!inst._byTypeTier.TryGetValue((type, poolTier), out var tierList))
                    inst._byTypeTier[(type, poolTier)] = tierList = new List<string>();
                Namelist.AddAll(names, list);
                Namelist.AddAll(names, tierList);
            }
            _cached = inst;
            return _cached;
        }

        // 指定 facility_type の名前プール（全ティア統合）を返す（無ければ null）。
        public IReadOnlyList<string> Names(string facilityType)
            => _byType.TryGetValue(facilityType, out var l) && l.Count > 0 ? l : null;

        // 指定 facility_type ＋プールティア（cheap/mid/high）の名前プールを返す（無ければ null）。
        public IReadOnlyList<string> Names(string facilityType, string poolTier)
            => _byTypeTier.TryGetValue((facilityType, poolTier), out var l) && l.Count > 0 ? l : null;

        // 区画ロール別の名前プール（4つ以上を保証。読めなければ null）。
        public IReadOnlyList<IReadOnlyList<string>> WardNamePools => _wardPools;

        // ward_names を区画ロール別プールへ解釈する。要素がオブジェクト（{names:[...]}）なら
        // その names を1プールに、文字列なら旧形式（並び順の固定名）として1名だけのプールにする。
        // 区画プランに必要な4プールに満たなければ null（呼び出し元が埋め込みへフォールバック）。
        private static List<List<string>> ParseWardPools(JsonArray arr)
        {
            var pools = new List<List<string>>();
            foreach (var n in arr)
            {
                if (n is JsonObject o)
                {
                    var names = new List<string>();
                    Namelist.AddAll(o["names"] as JsonArray, names);
                    if (names.Count > 0) pools.Add(names);
                }
                else if (n is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s))
                    pools.Add(new List<string> { s });
            }
            return pools.Count >= 4 ? pools : null;
        }
    }

    // NPC プール（namelist/npc.json）。male/female の名前・二つ名(epithets)・
    // 成人カテゴリ(adult_categories)・外見タグ(look_tags)。
    internal sealed class NpcNames
    {
        private List<string> _male, _female, _epithets, _adultCategories, _lookTags;
        private static NpcNames _cached;

        public static NpcNames Load()
        {
            if (_cached != null) return _cached;
            var src = Namelist.Load("npc.json");
            _cached = new NpcNames
            {
                _male = src.Pool("male"),
                _female = src.Pool("female"),
                _epithets = src.Pool("epithets"),
                _adultCategories = src.Pool("adult_categories"),
                _lookTags = src.Pool("look_tags"),
            };
            return _cached;
        }

        // 性別に応じた名前プール（無ければ null）。female=true で女性名。
        public IReadOnlyList<string> Names(bool female)
        {
            var l = female ? _female : _male;
            return l.Count > 0 ? l : null;
        }

        // 二つ名プール（無ければ null）。
        public IReadOnlyList<string> Epithets => _epithets.Count > 0 ? _epithets : null;

        // 施設主・冒険者に使う成人カテゴリ。
        public IReadOnlyList<string> AdultCategories => _adultCategories;

        // look 用の外見タグ（5個程度をランダム連結して使う）。
        public IReadOnlyList<string> LookTags => _lookTags;
    }

    // 集落プール（namelist/areas.json）。type はエリアの size(city/town/village) と一致。bgm は集落用 BGM。
    internal sealed class AreaNames
    {
        private readonly Dictionary<string, List<string>> _byType = new();
        private List<string> _bgm;
        private static AreaNames _cached;

        public static AreaNames Load()
        {
            if (_cached != null) return _cached;
            var src = Namelist.Load("areas.json");
            var inst = new AreaNames { _bgm = src.Pool("bgm") };
            foreach (var c in src.Categories())
            {
                if (c is not JsonObject o) continue;
                string type = J.Str(o, "type");
                if (string.IsNullOrEmpty(type) || o["names"] is not JsonArray names) continue;
                if (!inst._byType.TryGetValue(type, out var list))
                    inst._byType[type] = list = new List<string>();
                Namelist.AddAll(names, list);
            }
            _cached = inst;
            return _cached;
        }

        // 指定 size(city/town/village) の名前プールを返す（無ければ null）。
        public IReadOnlyList<string> Names(string size)
            => _byType.TryGetValue(size, out var l) && l.Count > 0 ? l : null;

        // 集落用 BGM パスのプール。
        public IReadOnlyList<string> Bgm => _bgm;
    }

    // ワールド名プール（namelist/worlds.json）。全カテゴリ統合。
    internal sealed class WorldNames
    {
        private List<string> _names;
        private static WorldNames _cached;

        public static WorldNames Load()
            => _cached ??= new WorldNames { _names = Namelist.Load("worlds.json").FlattenCategories() };

        // 名前プールを返す（無ければ null）。
        public IReadOnlyList<string> Names() => _names.Count > 0 ? _names : null;
    }

    // ダンジョンプール（namelist/dungeons.json）。名前（カテゴリは雰囲気の分類だが全統合で扱う）・
    // BGM(bgm)・ダンジョン内地点名(locations)。
    internal sealed class DungeonNames
    {
        private List<string> _names, _bgm, _locations;
        private static DungeonNames _cached;

        public static DungeonNames Load()
        {
            if (_cached != null) return _cached;
            var src = Namelist.Load("dungeons.json");
            _cached = new DungeonNames
            {
                _names = src.FlattenCategories(),
                _bgm = src.Pool("bgm"),
                _locations = src.Pool("locations"),
            };
            return _cached;
        }

        // 名前プールを返す（無ければ null）。
        public IReadOnlyList<string> Names() => _names.Count > 0 ? _names : null;

        // ダンジョン用 BGM パスのプール。
        public IReadOnlyList<string> Bgm => _bgm;

        // ダンジョン内の場所(dungeon_location)名のプール。
        public IReadOnlyList<string> Locations => _locations;
    }

    // クエストプール（namelist/quests.json）。題名（カテゴリは討伐対象の分類だが全統合で扱う）・
    // 題名部品(title_adjectives/title_nouns)・ストーリー題名(story_titles)・依頼主名(client_names)。
    internal sealed class QuestNames
    {
        private List<string> _names, _titleAdjectives, _titleNouns, _storyTitles, _clientNames;
        private static QuestNames _cached;

        public static QuestNames Load()
        {
            if (_cached != null) return _cached;
            var src = Namelist.Load("quests.json");
            _cached = new QuestNames
            {
                _names = src.FlattenCategories(),
                _titleAdjectives = src.Pool("title_adjectives"),
                _titleNouns = src.Pool("title_nouns"),
                _storyTitles = src.Pool("story_titles"),
                _clientNames = src.Pool("client_names"),
            };
            return _cached;
        }

        // 題名プールを返す（無ければ null）。
        public IReadOnlyList<string> Names() => _names.Count > 0 ? _names : null;

        // 題名の枕詞・主題（題名プールが無いときの合成用）。
        public IReadOnlyList<string> TitleAdjectives => _titleAdjectives;
        public IReadOnlyList<string> TitleNouns => _titleNouns;

        // ストーリークエスト題名（個数がストーリークエスト数の上限になる）。
        public IReadOnlyList<string> StoryTitles => _storyTitles;

        // 依頼主名。
        public IReadOnlyList<string> ClientNames => _clientNames;
    }

    // 敵プール（namelist/enemies.json）。他の namelist と同じ categories 骨格
    // （categories: [{type, label, names: [...]}]、type は normal/miniboss/boss）だが、
    // names の各要素は名前文字列ではなく敵データ本体（name/description/look/skills/trait/drops）。
    // 読込時にゲームのクエスト敵と同形の {type, data} へラップして保持し、
    // 生成側はディープコピーしてそのまま quest へ挿す。
    internal sealed class EnemyNames
    {
        private Dictionary<string, List<JsonObject>> _byType;
        private static EnemyNames _cached;

        public static EnemyNames Load()
        {
            if (_cached != null) return _cached;
            var src = Namelist.Load("enemies.json");
            _cached = new EnemyNames { _byType = src.Parse("categories", ParseCategories) ?? new() };
            return _cached;
        }

        // categories を type → {type, data} テンプレートのリストへ集める。有効な敵が1体も
        // 無ければ null を返し、埋め込みへフォールバックさせる（categories キーを持たない
        // 旧形式の外部ファイルは Parse 側で同様に埋め込みへ落ちる）。
        private static Dictionary<string, List<JsonObject>> ParseCategories(JsonArray arr)
        {
            var byType = new Dictionary<string, List<JsonObject>>();
            int total = 0;
            foreach (var c in arr)
            {
                if (c is not JsonObject o) continue;
                string type = J.Str(o, "type");
                if (type == "" || o["names"] is not JsonArray names) continue;
                if (!byType.TryGetValue(type, out var list))
                    byType[type] = list = new List<JsonObject>();
                foreach (var n in names)
                    if (n is JsonObject d && J.Str(d, "name") != "")
                    {
                        list.Add(new JsonObject { ["type"] = type, ["data"] = d.DeepClone() });
                        total++;
                    }
            }
            return total > 0 ? byType : null;
        }

        private IReadOnlyList<JsonObject> ByType(string type)
            => _byType.TryGetValue(type, out var l) && l.Count > 0 ? l : null;

        public IReadOnlyList<JsonObject> Monsters => ByType("normal");
        public IReadOnlyList<JsonObject> Minibosses => ByType("miniboss");
        public IReadOnlyList<JsonObject> Bosses => ByType("boss");
    }

    // イベントプール（namelist/events.json）。他の namelist と同じ categories 骨格
    // （type は benefit/hazard/neutral。実データで観測された語彙はこの3種のみで danger は不正値）。
    // names の各要素はイベント本体（event_name/event_description）で、読込時に type を
    // category として付与し、ゲームのクエストイベントと同形の
    // {event_name, category, event_description} へ整形して保持する。
    internal sealed class EventNames
    {
        private List<JsonObject> _events;
        private static EventNames _cached;

        public static EventNames Load()
        {
            if (_cached != null) return _cached;
            var src = Namelist.Load("events.json");
            _cached = new EventNames { _events = src.Parse("categories", ParseCategories) ?? new() };
            return _cached;
        }

        // categories を {event_name, category, event_description} のリストへ集める。
        // 有効なイベントが1件も無ければ null を返し、埋め込みへフォールバックさせる。
        private static List<JsonObject> ParseCategories(JsonArray arr)
        {
            var events = new List<JsonObject>();
            foreach (var c in arr)
            {
                if (c is not JsonObject o) continue;
                string type = J.Str(o, "type");
                if (type == "" || o["names"] is not JsonArray names) continue;
                foreach (var n in names)
                    if (n is JsonObject d && J.Str(d, "event_name") != "")
                        events.Add(new JsonObject
                        {
                            ["event_name"] = J.Str(d, "event_name"),
                            ["category"] = type,
                            ["event_description"] = J.Str(d, "event_description"),
                        });
            }
            return events.Count > 0 ? events : null;
        }

        // イベントテンプレートのプール（無ければ null）。
        public IReadOnlyList<JsonObject> Events => _events.Count > 0 ? _events : null;
    }
}
