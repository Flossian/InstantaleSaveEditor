// world_data.json を生成する。
// 実データと同様、開始集落だけでなく全集落を
// 同じ規則でフル生成する（ダンジョンは dungeon_location 施設のノード）。
// シードで再現可能に組み立てる。
using System.Text.Json.Nodes;

namespace InstantaleSaveEditor
{
    // 生成パラメータ。
    internal sealed class GenOptions
    {
        public string Name = "";       // 空ならランダム
        public int Seed;
        public int Settlements = 8;    // 集落エリア数（>=1）
        public double OwnerEpithetChance = 0.20;        // 施設主NPCへの二つ名付与率
        public double AdventurerEpithetChance = 0.60;   // 冒険者NPCへの二つ名付与率
    }

    // シード固定の乱数ラッパ（再現性のため System.Random を一元化）。
    internal sealed class Rng
    {
        private readonly Random _r;
        public Rng(int seed) { _r = new Random(seed); }
        public int Next(int maxExclusive) => _r.Next(maxExclusive);
        public int Range(int minInclusive, int maxInclusive) => _r.Next(minInclusive, maxInclusive + 1);
        public T Pick<T>(IReadOnlyList<T> list) => list[_r.Next(list.Count)];
        public bool Chance(double p) => _r.NextDouble() < p;
        // 重複なしで n 個取り出す（プールが足りなければ全件）。
        public List<T> Sample<T>(IReadOnlyList<T> list, int n)
        {
            var pool = new List<T>(list);
            var outp = new List<T>();
            n = Math.Min(n, pool.Count);
            for (int i = 0; i < n; i++)
            {
                int idx = _r.Next(pool.Count);
                outp.Add(pool[idx]);
                pool.RemoveAt(idx);
            }
            return outp;
        }
    }

    internal sealed class WorldGenerator
    {
        private const int DungeonsPerSettlement = 3;   // 集落ごとに固定で生成するダンジョン数
        private Rng _rng = null!;
        private int _node, _fac, _npc, _quest;   // グローバル ID カウンタ
        private JsonObject _npcs = null!;
        private FacilityNames _names = null!;     // namelist/facilities.json の名前プール
        private NpcNames _npcNames = null!;       // namelist/npc.json の名前・二つ名プール
        private AreaNames _areaNames = null!;     // namelist/areas.json の集落名プール
        private WorldNames _worldNames = null!;   // namelist/worlds.json のワールド名プール
        private DungeonNames _dungeonNames = null!; // namelist/dungeons.json のダンジョン名プール
        private QuestNames _questNames = null!;   // namelist/quests.json のクエスト題名プール
        private EnemyNames _enemyNames = null!;   // namelist/enemies.json の敵/中ボス/ボスのプール
        private EventNames _eventNames = null!;   // namelist/events.json のクエストイベントのプール
        private double _ownerEpithetChance = 0.20;
        private double _adventurerEpithetChance = 0.60;
        private int[] _difficultyBase = null!;    // area index ごとの難易度基準値（開始集落からの距離で決まる）

        public JsonObject Generate(GenOptions opt)
        {
            _rng = new Rng(opt.Seed);
            _names = FacilityNames.Load();
            _npcNames = NpcNames.Load();
            _areaNames = AreaNames.Load();
            _worldNames = WorldNames.Load();
            _dungeonNames = DungeonNames.Load();
            _questNames = QuestNames.Load();
            _enemyNames = EnemyNames.Load();
            _eventNames = EventNames.Load();
            _node = _fac = _npc = _quest = 0;
            _ownerEpithetChance = opt.OwnerEpithetChance;
            _adventurerEpithetChance = opt.AdventurerEpithetChance;

            int settlements = Math.Max(1, opt.Settlements);
            int dungeons = settlements * DungeonsPerSettlement;   // 集落ごとに固定 3 つ
            string worldName = string.IsNullOrWhiteSpace(opt.Name) ? _rng.Pick(Require(_worldNames.Names(), "worlds.json")) : opt.Name.Trim();

            var areas = new JsonObject();
            _npcs = new JsonObject();
            var quests = new JsonObject();
            var storyQuests = new JsonObject();

            // --- 集落サイズ・接続グラフを先に決め、サイズごとに areas.json から名前を選ぶ ---
            string[] sizes;
            Dictionary<int, List<string>> conns;
            if (settlements == FixedSizes.Length)
            {
                // 9 集落の標準レイアウト（固定）。
                sizes = (string[])FixedSizes.Clone();
                conns = BuildFixedSettlementGraph();
            }
            else
            {
                sizes = new string[settlements];
                sizes[0] = "town";                         // 開始集落
                for (int i = 1; i < settlements; i++) sizes[i] = _rng.Pick(new[] { "village", "village", "town", "town", "city" });
                conns = BuildSettlementGraph(settlements);
            }
            var setNames = PickSettlementNames(sizes);
            _difficultyBase = ComputeAreaDifficultyBase(settlements, conns);

            // --- 集落（全てフル生成。0 は開始集落） ---
            for (int i = 0; i < settlements; i++)
                areas[i.ToString()] = BuildSettlement(i.ToString(), setNames[i], sizes[i], conns[i], isStart: i == 0, areaDifficulty: _difficultyBase[i]);

            // --- ダンジョンとクエスト（集落ごとに固定 3 つのダンジョン＋対応クエストを発行） ---
            var dungeonNames = PickDistinct(Require(_dungeonNames.Names(), "dungeons.json"), dungeons);
            int dungeonNameIdx = 0;
            for (int s = 0; s < settlements; s++)
            {
                string ownerId = s.ToString();
                var questIds = new JsonArray();
                for (int d = 0; d < DungeonsPerSettlement; d++)
                {
                    string aid = (settlements + s * DungeonsPerSettlement + d).ToString();
                    areas[aid] = BuildDungeon(aid, dungeonNames[dungeonNameIdx++]);

                    string qid = (_quest++).ToString();
                    quests[qid] = BuildQuest(qid, aid, ownerId, difficulty: d + 1);
                    questIds.Add(qid);
                }
                ((JsonObject)areas[ownerId])["quests"] = questIds;
            }

            // --- ストーリークエスト（開始集落以外を昇順難易度で巡る） ---
            int storyCount = Math.Min(settlements - 1, _questNames.StoryTitles.Count);
            var storyTitles = PickDistinct(_questNames.StoryTitles, storyCount);
            for (int i = 0; i < storyCount; i++)
            {
                string sid = i.ToString();
                // 難易度テーブルの上限(100)を超えるとゲーム側が KeyError で落ちるため頭打ちにする。
                storyQuests[sid] = BuildStoryQuest(sid, storyTitles[i], (i + 1).ToString(), Math.Min((i + 1) * 14, 100));
            }

            // --- ルート ---
            var root = new JsonObject
            {
                ["world_data"] = BuildWorldData(worldName),
                ["areas"] = areas,
                ["npcs"] = _npcs,
                ["quests"] = quests,
                ["story_quests"] = storyQuests,
                ["index"] = new JsonObject
                {
                    ["area"] = settlements + dungeons,
                    ["node"] = _node,
                    ["facility"] = _fac,
                    ["npc"] = _npc,
                    ["item"] = 0,
                    ["quest"] = _quest,
                },
                ["language"] = "ja",
                ["version"] = 0,
            };
            return root;
        }

        // ---------------- world_data ----------------
        private static JsonObject BuildWorldData(string name) => new()
        {
            ["name"] = name,
            ["overview"] = $"「{name}」は、古き記憶と新たな冒険が交錯する世界。各地の集落を起点に、外縁の遺跡やダンジョンへと旅路が伸びている。",
            ["structure_description"] = "世界は中央の集落を起点に放射状に広がり、街道で互いに結ばれている。外縁へ進むほど危険なダンジョンが点在する。",
            ["story"] = new JsonObject
            {
                ["world_situation"] = "世界は穏やかな日常の裏で、外縁から忍び寄る脅威に晒されつつある。",
                ["story_flow"] = "冒険者は各地の依頼をこなし、ダンジョンの主を討ち倒しながら、世界の核心へと近づいていく。",
                ["current_rumor"] = "近頃、外縁のダンジョンから漏れ出す異変が、近隣の集落を脅かし始めているという。",
                ["current_story_phase"] = 0,
            },
            ["days_elapsed"] = 0,
        };

        // ---------------- 集落の固定レイアウト（9 集落） ----------------
        // area id 順のサイズ。0:town 1:village 2:city 3:town 4:village 5:city 6:village 7:town 8:city
        private static readonly string[] FixedSizes =
            { "town", "village", "city", "town", "village", "city", "village", "town", "city" };

        // 0 を起点に 3 本の枝で構成: 0-1-2-3 / 0-4-5-6 / 0-7-8。
        private static readonly int[][] FixedBranches =
            { new[] { 0, 1, 2, 3 }, new[] { 0, 4, 5, 6 }, new[] { 0, 7, 8 } };

        private static Dictionary<int, List<string>> BuildFixedSettlementGraph()
        {
            var adj = new Dictionary<int, SortedSet<int>>();
            for (int i = 0; i < FixedSizes.Length; i++) adj[i] = new SortedSet<int>();
            foreach (var branch in FixedBranches)
                for (int k = 1; k < branch.Length; k++)
                {
                    adj[branch[k - 1]].Add(branch[k]);
                    adj[branch[k]].Add(branch[k - 1]);
                }
            return adj.ToDictionary(p => p.Key, p => p.Value.Select(x => x.ToString()).ToList());
        }

        // ---------------- 集落グラフ ----------------
        // 連結を保証する全域木 ＋ 少数の追加辺。各 area の接続先 id 一覧を返す。
        private Dictionary<int, List<string>> BuildSettlementGraph(int n)
        {
            var adj = new Dictionary<int, HashSet<int>>();
            for (int i = 0; i < n; i++) adj[i] = new HashSet<int>();
            for (int i = 1; i < n; i++)
            {
                int j = _rng.Next(i);          // 既存ノードへ接続
                adj[i].Add(j); adj[j].Add(i);
            }
            // 追加辺（多くても n/3 程度）。
            int extra = n / 3;
            for (int e = 0; e < extra; e++)
            {
                int a = _rng.Next(n), b = _rng.Next(n);
                if (a != b) { adj[a].Add(b); adj[b].Add(a); }
            }
            var outp = new Dictionary<int, List<string>>();
            for (int i = 0; i < n; i++)
                outp[i] = adj[i].OrderBy(x => x).Select(x => x.ToString()).ToList();
            return outp;
        }

        // 実データ(data\worlds 配下)で観測された、標準 9 集落レイアウトの area index ごとの
        // NPC 難易度基準値（config.difficulty_level / experience_level のベース）。
        // 0(開始集落)から遠い集落ほど値が大きい。個々の NPC にはこの基準値へランダムな
        // ぶれ（BuildNpc 側）を加える。
        private static readonly int[] FixedDifficultyBase = { 3, 7, 22, 52, 15, 31, 63, 43, 73 };

        // area index ごとの難易度基準値を決める。
        // 標準 9 集落レイアウトでは実データの値をそのまま使い、それ以外の集落数では
        // 開始集落(0)からのグラフ距離(BFS深さ)に応じて同程度のスケールで近似する。
        private int[] ComputeAreaDifficultyBase(int settlements, Dictionary<int, List<string>> conns)
        {
            if (settlements == FixedDifficultyBase.Length)
                return (int[])FixedDifficultyBase.Clone();

            var depth = new int[settlements];
            for (int i = 0; i < settlements; i++) depth[i] = -1;
            depth[0] = 0;
            var queue = new Queue<int>();
            queue.Enqueue(0);
            while (queue.Count > 0)
            {
                int cur = queue.Dequeue();
                foreach (var nStr in conns[cur])
                {
                    int next = int.Parse(nStr);
                    if (depth[next] != -1) continue;
                    depth[next] = depth[cur] + 1;
                    queue.Enqueue(next);
                }
            }
            var outp = new int[settlements];
            for (int i = 0; i < settlements; i++)
                outp[i] = 3 + Math.Max(0, depth[i]) * 19;   // 実データの深さ平均値に合わせた近似
            return outp;
        }

        // ---------------- 集落（フル生成） ----------------
        // 開始集落(0)を含む全集落に同じ規則を適用する（実データも各集落が同様に構築されている）。
        private JsonObject BuildSettlement(string areaId, string name, string size, List<string> connections, bool isStart, int areaDifficulty)
        {
            var facilities = new JsonObject();

            // 施設 ID を予約しながら木構造（入口→区画→サービス施設＋名所→出口）を作る。
            int entranceId = _fac++;                       // 0
            // 区画ごとの構成（サービス施設タイプ）。はろーわーるどの配置を踏襲。
            // 区画名はロール別プールからエリアごとにランダムに選ぶ（全エリアで同名になるのを避ける）。
            var wardPools = _names.WardNamePools
                ?? throw new InvalidOperationException("namelist の名前プールが空です: facilities.json (ward_names)");
            var wardPlans = new (string ward, string[] services, bool withLocation)[]
            {
                (_rng.Pick(wardPools[0]), new[]{ "inn", "guild" },                       true),
                (_rng.Pick(wardPools[1]), new[]{ "general_store", "blacksmith", "specialty_shop" }, false),
                (_rng.Pick(wardPools[2]), new[]{ "administrative_office", "medical_facility", "training_facility" }, true),
            };

            var wardIds = new List<int>();
            var entranceConns = new JsonArray();

            foreach (var plan in wardPlans)
            {
                int wardId = _fac++;
                wardIds.Add(wardId);
                entranceConns.Add(wardId.ToString());
                var wardConns = new JsonArray { entranceId.ToString() };

                foreach (var svc in plan.services)
                {
                    int fid = _fac++;
                    string ownerId = (_npc++).ToString();
                    var (fname, tier) = ServiceNameTier(svc, name);
                    facilities[fid.ToString()] = Facility(fname, fid, FacilityDesc(svc), svc, tier, ownerId,
                        new JsonArray { wardId.ToString() });
                    AddOwnerNpc(ownerId, svc, areaId, fid.ToString(), areaDifficulty);
                    wardConns.Add(fid.ToString());
                }
                if (plan.withLocation)
                {
                    int lid = _fac++;
                    facilities[lid.ToString()] = Facility(PickFacilityName("location", null, name), lid, "趣のある名所。",
                        "location", null, null, new JsonArray { wardId.ToString() });
                    wardConns.Add(lid.ToString());
                }
                facilities[wardId.ToString()] = Facility(plan.ward, wardId, "区画。", "ward", null, null, wardConns);
            }

            // --- 裏施設（v9.1 で追加。確率で各集落に配置。オーナーNPC付き） ---
            var extraSvcs = new List<string>();
            if (_rng.Chance(0.6)) extraSvcs.Add("underworld_office");
            if (_rng.Chance(0.4)) extraSvcs.Add("colosseum");
            if (extraSvcs.Count > 0)
            {
                int wardId = _fac++;
                wardIds.Add(wardId);
                entranceConns.Add(wardId.ToString());
                var wardConns = new JsonArray { entranceId.ToString() };
                foreach (var svc in extraSvcs)
                {
                    int fid = _fac++;
                    string ownerId = (_npc++).ToString();
                    var (fname, tier) = ServiceNameTier(svc, name);
                    facilities[fid.ToString()] = Facility(fname, fid, FacilityDesc(svc), svc, tier, ownerId,
                        new JsonArray { wardId.ToString() });
                    AddOwnerNpc(ownerId, svc, areaId, fid.ToString(), areaDifficulty);
                    wardConns.Add(fid.ToString());
                }
                facilities[wardId.ToString()] = Facility(_rng.Pick(wardPools[3]), wardId, "区画。", "ward", null, null, wardConns);
            }

            int exitId = _fac++;
            entranceConns.Add(exitId.ToString());
            facilities[exitId.ToString()] = FacilityNoTier($"{name} - 出口", exitId, "エリアの出口。", "exit", null,
                new JsonArray { entranceId.ToString() });
            // 入口（tier なし）。
            facilities[entranceId.ToString()] = FacilityNoTier($"{name} - 入口", entranceId, "", "entrance", null, entranceConns);

            // 冒険者 NPC（ギルドに配置）。
            string guildFid = FindFacilityIdByType(facilities, "guild");
            var adventurerIds = new JsonArray();
            int advCount = _rng.Range(2, 3);
            for (int i = 0; i < advCount; i++)
            {
                string aid = (_npc++).ToString();
                AddAdventurerNpc(aid, areaId, guildFid, areaDifficulty);
                adventurerIds.Add(aid);
            }

            int nodeId = _node++;
            var node = new JsonObject
            {
                ["name"] = "",
                ["id"] = nodeId.ToString(),
                ["overview"] = "",
                ["facilities"] = facilities,
                ["connections"] = new JsonArray(),
                ["entrance_facility"] = entranceId.ToString(),
                ["config"] = new JsonObject { ["level_of_detail"] = 0 },
            };

            return new JsonObject
            {
                ["name"] = name,
                ["id"] = areaId,
                ["descriptions"] = new JsonObject
                {
                    ["overview"] = isStart
                        ? $"世界の起点となる{SizeJp(size)}。多くの旅人が行き交う。"
                        : $"{SizeJp(size)}「{name}」。宿や商店が並び、旅人や冒険者が行き交う。",
                    ["facilities"] = "宿・冒険者組合・各種商店・行政や医療の施設が揃う。",
                    ["area_description"] = isStart
                        ? "穏やかな光に満ち、活気のある通りが続く。"
                        : "落ち着いた佇まいの中にも、旅人を迎える活気がある。",
                },
                ["size"] = size,
                ["resident_npcs"] = new JsonArray(),
                ["adventurer_npcs"] = adventurerIds,
                ["connections"] = J.StrArr(connections),
                ["nodes"] = new JsonObject { [nodeId.ToString()] = node },
                ["quests"] = new JsonArray(),     // 後で通常クエスト ID を入れる
                ["labors"] = new JsonObject { ["created_date"] = 0, ["posts"] = new JsonArray() },
                ["entrance_node"] = nodeId.ToString(),
                ["bgm"] = _rng.Pick(Require(_areaNames.Bgm, "areas.json (bgm)")),
                ["config"] = new JsonObject { ["level_of_detail"] = 1 },
            };
        }

        // ---------------- ダンジョン ----------------
        private JsonObject BuildDungeon(string areaId, string name)
        {
            var facilities = new JsonObject();
            int count = _rng.Range(3, 5);
            var locNames = PickDistinct(Require(_dungeonNames.Locations, "dungeons.json (locations)"), count);
            int firstFid = _fac;
            for (int i = 0; i < count; i++)
            {
                int fid = _fac++;
                facilities[fid.ToString()] = new JsonObject
                {
                    ["name"] = locNames[i],
                    ["id"] = fid.ToString(),
                    ["description"] = "",
                    ["facility_type"] = "dungeon_location",
                    ["tier"] = null,
                    ["owner"] = null,
                    ["connections"] = new JsonArray(),
                    ["config"] = new JsonObject { ["level_of_detail"] = 0 },
                };
            }

            int nodeId = _node++;
            var node = new JsonObject
            {
                ["name"] = "",
                ["id"] = nodeId.ToString(),
                ["overview"] = "",
                ["facilities"] = facilities,
                ["connections"] = new JsonArray(),
                ["entrance_facility"] = firstFid.ToString(),
                ["config"] = new JsonObject { ["level_of_detail"] = 0 },
            };

            return new JsonObject
            {
                ["name"] = name,
                ["id"] = areaId,
                ["descriptions"] = new JsonObject
                {
                    ["overview"] = null,
                    ["facilities"] = null,
                    ["area_description"] = $"「{name}」。薄暗く湿った空気が満ち、奥には何かが潜む気配がある。",
                },
                ["size"] = "dungeon",
                ["resident_npcs"] = new JsonArray(),
                ["adventurer_npcs"] = new JsonArray(),
                ["connections"] = new JsonArray(),
                ["nodes"] = new JsonObject { [nodeId.ToString()] = node },
                ["quests"] = new JsonArray(),
                ["labors"] = new JsonObject { ["created_date"] = 0, ["posts"] = new JsonArray() },
                ["entrance_node"] = nodeId.ToString(),
                ["config"] = new JsonObject { ["level_of_detail"] = "1" },   // ゲーム出力の仕様（文字列）に合わせる
                ["bgm"] = _rng.Pick(Require(_dungeonNames.Bgm, "dungeons.json (bgm)")),
            };
        }

        // ---------------- 施設ヘルパ ----------------
        // tier を持つ施設（ward/location/サービス）。
        private static JsonObject Facility(string name, int id, string desc, string type, string tier, string owner, JsonArray connections) => new()
        {
            ["name"] = name,
            ["id"] = id.ToString(),
            ["description"] = desc,
            ["facility_type"] = type,
            ["tier"] = tier is null ? null : tier,
            ["owner"] = owner is null ? null : owner,
            ["connections"] = connections,
            ["config"] = new JsonObject { ["level_of_detail"] = 0 },
        };

        // tier キーを持たない施設（entrance/exit）。
        private static JsonObject FacilityNoTier(string name, int id, string desc, string type, string owner, JsonArray connections) => new()
        {
            ["name"] = name,
            ["id"] = id.ToString(),
            ["description"] = desc,
            ["facility_type"] = type,
            ["owner"] = owner is null ? null : owner,
            ["connections"] = connections,
            ["config"] = new JsonObject { ["level_of_detail"] = 0 },
        };

        // facility の id は文字列("3"等)で保存されているため、J.Int ではなく J.Str で取り出す。
        // J.Int は数値の JsonValue しか読めず、文字列だと常定値の 0 を返してしまい、
        // 開始集落(id=0の入口施設と一致)以外では存在しない施設ID "0" を指す不整合を生んでいた。
        private static string FindFacilityIdByType(JsonObject facilities, string type)
        {
            foreach (var kv in facilities)
                if (kv.Value is JsonObject f && J.Str(f, "facility_type") == type)
                    return J.Str(f, "id");
            return "0";
        }

        // 施設名・tier を決める。tier は SaveEditor v9.1 の語彙 basic/standard/advanced から
        // 重み付きで選び、名前はその tier に対応する namelist プールから取る（無ければ統合プール）。
        private (string name, string tier) ServiceNameTier(string svc, string townName)
        {
            string tier = PickTier();
            return (PickFacilityName(svc, tier, townName), tier);
        }

        // tier を basic/standard/advanced から重み付きで選ぶ（開始集落は標準が多め）。
        private string PickTier()
        {
            int r = _rng.Next(100);
            return r < 35 ? "basic" : r < 80 ? "standard" : "advanced";
        }

        // ゲーム tier → namelist/facilities.json のプールティア（cheap/mid/high）。
        private static string PoolTier(string gameTier) => gameTier switch
        {
            "basic" => "cheap", "standard" => "mid", "advanced" => "high", _ => "mid",
        };

        // facility_type（＋tier）の名前プールから 1 件選び、{0} を町名で置換する。
        // tier 指定があればそのプールティアを優先し、無ければ統合プール。どちらも無ければタイプ名をそのまま返す。
        private string PickFacilityName(string facilityType, string gameTier, string townName)
        {
            var pool = (gameTier != null ? _names.Names(facilityType, PoolTier(gameTier)) : null)
                ?? _names.Names(facilityType);
            if (pool == null || pool.Count == 0) return facilityType;
            string template = _rng.Pick(pool);
            return template.Contains("{0}") ? string.Format(template, townName) : template;
        }

        private static string FacilityDesc(string svc) => svc switch
        {
            "inn" => "旅人が体を休める宿。",
            "guild" => "依頼の受発注を行う冒険者の拠点。",
            "general_store" => "日用品や食料を扱う店。",
            "blacksmith" => "武具の製作と修理を行う鍛冶場。",
            "specialty_shop" => "珍しい品を扱う専門店。",
            "administrative_office" => "街の運営を司る役所。",
            "medical_facility" => "傷病者の治療を行う施設。",
            "training_facility" => "技を磨くための修練場。",
            "underworld_office" => "表の組合では扱えない裏の依頼を仲介する拠点。",
            "colosseum" => "腕自慢が集い、力を競い合う闘技場。",
            _ => "",
        };

        // ---------------- NPC ヘルパ ----------------
        // カテゴリ(young woman 等)から性別を判定し、npc.json の性別別プールから名前を選ぶ。
        private string PickNpcName(string category)
        {
            bool female = category.Contains("woman") || category.Contains("girl");
            return _rng.Pick(Require(_npcNames.Names(female), female ? "npc.json (female)" : "npc.json (male)"));
        }

        // 二つ名を確率 epithetChance で名前の前に付与（例:「死神」アーサー / 紅蓮のアーサー）。
        // 既定は冒険者(adventure)60%・施設主20%（設定画面で変更可）。
        private string PickNpcName(string category, double epithetChance)
        {
            string name = PickNpcName(category);
            var epithets = _npcNames.Epithets;
            if (epithets != null && _rng.Chance(epithetChance))
                name = _rng.Pick(epithets) + name;
            return name;
        }

        private void AddOwnerNpc(string id, string job, string areaId, string facilityId, int areaDifficulty)
        {
            string cat = _rng.Pick(Require(_npcNames.AdultCategories, "npc.json (adult_categories)"));
            _npcs[id] = BuildNpc(id, PickNpcName(cat, _ownerEpithetChance), cat, job,
                profile: $"{FacilityDesc(job).TrimEnd('。')}の主。",
                personality: "実直で、訪れる者に親身に接する。",
                lookDesc: "落ち着いた物腰の人物。",
                initArea: areaId, initFacility: facilityId, cat, areaDifficulty);
        }

        private void AddAdventurerNpc(string id, string areaId, string guildFacilityId, int areaDifficulty)
        {
            string cat = _rng.Pick(Require(_npcNames.AdultCategories, "npc.json (adult_categories)"));
            _npcs[id] = BuildNpc(id, PickNpcName(cat, _adventurerEpithetChance), cat, "adventure",
                profile: "組合に登録された冒険者。",
                personality: "経験を積もうと各地の依頼に挑む。",
                lookDesc: "身軽な装いで素早く動く。",
                initArea: areaId, initFacility: guildFacilityId, cat, areaDifficulty);
        }

        // config.difficulty_level / experience_level に使う基準値へランダムなぶれを加える（下限 1）。
        private int JitterDifficulty(int baseValue) => Math.Max(1, baseValue + _rng.Range(-4, 5));

        // カテゴリから年齢のおおよその範囲を決めてランダムに値を入れる。
        private int PickAgeForCategory(string category) => category switch
        {
            "young_boy" or "young_girl" => _rng.Range(6, 12),
            "teenage boy" or "teenage girl" => _rng.Range(13, 17),
            "young man" or "young woman" => _rng.Range(18, 29),
            "middle-aged man" or "middle-aged woman" => _rng.Range(30, 54),
            "old man" or "old woman" => _rng.Range(55, 80),
            _ => _rng.Range(18, 60),
        };

        private JsonObject BuildNpc(string id, string name, string category, string job,
            string profile, string personality, string lookDesc, string initArea, string initFacility, string lookHead,
            int areaDifficulty)
        {
            // 実データ(config.level_of_detail == 1)と同様、difficulty_level と experience_level は
            // 集落の難易度基準値にランダムなぶれを加えた値を入れる（ability_scores 等は実データでも
            // LOD1 では null のままなので変更しない）。
            int difficultyLevel = JitterDifficulty(areaDifficulty);
            int experienceLevel = JitterDifficulty(areaDifficulty);
            var look = new JsonArray { lookHead };
            foreach (var t in _rng.Sample(_npcNames.LookTags, 5)) look.Add(t);

            return new JsonObject
            {
                ["name"] = name,
                ["id"] = id,
                ["category"] = category,
                ["profile"] = profile,
                ["personality"] = personality,
                ["look_description"] = lookDesc,
                ["speech_style"] = null,
                ["job"] = job,
                ["state"] = "",
                ["ability_scores"] = new JsonObject
                {
                    ["strength"] = null,
                    ["dexterity"] = null,
                    ["constitution"] = null,
                    ["intelligence"] = null,
                    ["wisdom"] = null,
                    ["charisma"] = null,
                },
                ["experience_level"] = experienceLevel,
                ["experience_point"] = 0,
                ["original_max_hp"] = null,
                ["max_hp"] = null,
                ["current_hp"] = null,
                ["age"] = PickAgeForCategory(category),
                ["skills"] = new JsonObject(),
                ["equipments"] = new JsonObject(),
                ["weakness"] = null,
                ["location"] = new JsonObject { ["area"] = null, ["node"] = null, ["facility"] = null },
                ["inventory"] = new JsonObject(),
                ["image_src"] = new JsonObject
                {
                    ["base_normal"] = null,
                    ["base_upscaled"] = null,
                    ["fullbody"] = null,
                    ["opponent"] = null,
                    ["face"] = null,
                },
                ["look"] = look,
                ["memory"] = new JsonObject
                {
                    ["life_log"] = "",
                    ["memory_archive"] = new JsonArray(),
                    ["session_log"] = new JsonArray(),
                    ["prior_area_summary"] = "",
                    ["brief_summary"] = "ゲーム開始",
                },
                ["life_log"] = new JsonArray(),
                ["current_log"] = new JsonArray(),
                ["relationship"] = null,
                ["current_area"] = initArea,
                ["current_location"] = initFacility,
                ["initial_location"] = new JsonObject
                {
                    ["area"] = initArea,
                    ["node"] = null,
                    ["facility"] = initFacility,
                },
                ["config"] = new JsonObject
                {
                    ["level_of_detail"] = 1,
                    ["is_player"] = false,
                    ["is_dead"] = false,
                    ["difficulty_level"] = difficultyLevel,
                },
            };
        }

        // ---------------- クエスト ----------------
        private JsonObject BuildQuest(string id, string dungeonAreaId, string settlementId, int difficulty)
        {
            var questPool = _questNames.Names();
            string questTitle = questPool != null
                ? _rng.Pick(questPool)
                : _rng.Pick(Require(_questNames.TitleAdjectives, "quests.json (title_adjectives)"))
                  + _rng.Pick(Require(_questNames.TitleNouns, "quests.json (title_nouns)"));
            var locs = J.StrArr(PickDistinct(Require(_dungeonNames.Locations, "dungeons.json (locations)"), 3));

            var enemies = new JsonArray();
            int enemyCount = _rng.Range(2, 3);
            for (int i = 0; i < enemyCount; i++) enemies.Add(BuildMonster());
            // 難易度2以上のクエストには中ボスを1体加える
            if (difficulty >= 2) enemies.Add(BuildMiniboss());

            var events = new JsonArray();
            var eventPool = Require(_eventNames.Events, "events.json (events)");
            foreach (var e in _rng.Sample(eventPool, _rng.Range(1, 2)))
                events.Add(e.DeepClone());

            return new JsonObject
            {
                ["quest_title"] = questTitle,
                ["client_name"] = _rng.Pick(Require(_questNames.ClientNames, "quests.json (client_names)")),
                ["request_summary"] = "外縁で確認された異変の調査と、原因の排除を求める依頼。",
                ["client_statement"] = "最近、おかしな気配を感じる。\nどうか調べてもらえないだろうか。",
                ["area"] = new JsonObject
                {
                    ["name"] = $"{questTitle}の現場",
                    ["layout_description"] = "入り組んだ通路の奥に、開けた空間がいくつか点在している。",
                    ["locations"] = locs,
                    ["atomosphere"] = "normal",
                },
                ["events"] = events,
                ["enemies"] = enemies,
                ["boss"] = BuildBoss(),
                ["difficulty"] = difficulty,
                ["neighboring_settlement_id"] = settlementId,
                ["id"] = id,
                ["quest_type"] = "normal_quest",
                ["config"] = new JsonObject { ["status"] = "incomplete", ["level_of_detail"] = 1 },
                ["quest_area_id"] = dungeonAreaId,
            };
        }

        // enemies.json の完成テンプレート {type, data} をディープコピーして返す。
        private JsonObject BuildMonster()
            => (JsonObject)_rng.Pick(Require(_enemyNames.Monsters, "enemies.json (monsters)")).DeepClone();

        private JsonObject BuildMiniboss()
            => (JsonObject)_rng.Pick(Require(_enemyNames.Minibosses, "enemies.json (minibosses)")).DeepClone();

        private JsonObject BuildBoss()
            => (JsonObject)_rng.Pick(Require(_enemyNames.Bosses, "enemies.json (bosses)")).DeepClone();

        // ---------------- ストーリークエスト ----------------
        private static JsonObject BuildStoryQuest(string id, string title, string settlementId, int difficulty) => new()
        {
            ["quest_title"] = title,
            ["client_name"] = null,
            ["request_summary"] = $"{title}に関わる、世界の根幹に触れる使命。",
            ["client_statement"] = null,
            ["difficulty"] = difficulty,
            ["area"] = null,
            ["quest_area_id"] = null,
            ["events"] = new JsonArray(),
            ["enemies"] = new JsonArray(),
            ["boss"] = new JsonObject(),
            ["neighboring_settlement_id"] = settlementId,
            ["id"] = id,
            ["quest_type"] = "story_quest",
            ["config"] = new JsonObject { ["status"] = "incomplete", ["level_of_detail"] = 0 },
        };

        // ---------------- 共通 ----------------
        // プールから重複なしで n 個。足りなければ番号付きで補完する。
        private List<string> PickDistinct(IReadOnlyList<string> pool, int n)
        {
            var picked = _rng.Sample(pool, Math.Min(n, pool.Count));
            for (int i = picked.Count; i < n; i++) picked.Add($"{_rng.Pick(pool)} {i + 1}");
            return picked;
        }

        // 集落名を size(city/town/village) ごとに areas.json の名前プールから重複なしで選ぶ。
        private List<string> PickSettlementNames(string[] sizes)
        {
            var queues = new Dictionary<string, Queue<string>>();
            foreach (var grp in sizes.Select((size, i) => (size, i)).GroupBy(x => x.size))
            {
                var pool = Require(_areaNames.Names(grp.Key), $"areas.json ({grp.Key})");
                queues[grp.Key] = new Queue<string>(PickDistinct(pool, grp.Count()));
            }
            return sizes.Select(s => queues[s].Dequeue()).ToList();
        }

        private static string SizeJp(string size) => size switch
        {
            "village" => "村", "town" => "町", "city" => "都市", _ => "集落",
        };

        // プールが空（namelist の破損・キー欠落など）の場合は、原因が分かるメッセージで生成を止める。
        private static IReadOnlyList<T> Require<T>(IReadOnlyList<T> pool, string source)
            => pool is { Count: > 0 } ? pool
             : throw new InvalidOperationException($"namelist の名前プールが空です: {source}");
    }
}
