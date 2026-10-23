// InstantaleModLoader（MOD ローダ）との連携。設定の ModLoaderRoot（ローダのフォルダ）から次を読む。
//
// ・アイテム画像の探し先（128_item_image_variety）
//   MOD が足した絵はゲームのフォルダには置かれず、`runtime\mods\<128>\images\<分類>\Assets\images\item_candidates_dark\<品種>\`
//   にある。ゲーム（Kivy）は image_src の相対パス（Assets/images/item_candidates_dark/<品種>/<鍵>.png）が
//   ゲームのフォルダに無いと、MOD が足した探し先（images\<分類>\ と state\item_image_cross\）を順に見る。
//   エディタも同じ順で探す（ゲーム導入先 → 分類フォルダ → state\item_image_cross）。
//
// ・装備欄の控え（333_equipment_slots）
//   装備欄に入れた品はセーブでは inventory に混ざっていて、どの品がどの部位に居るかは
//   `state\equipment_slots\<世界>×<主人公>.json`（{持ち主: {品の鍵: [x, y]}}。主人公は名前、仲間は "npc:<id>"。
//   座標は 6列×10行の装備欄のマスで y は上から）にだけある。
//   装備中の品の grid_pos は装備欄の座標で書かれるので、所持品のグリッドに並べると重なる。
//
// MOD の番号（128_ / 333_）は変わりうるため、フォルダ名の末尾（番号を除いた名前）で探す。
// ローダが無い・MOD が無いときは何もしない（従来どおりゲーム導入先だけを見る）。
using System.Text.Json.Nodes;

namespace InstantaleSaveEditor
{
    internal static class ModLoaderLink
    {
        public const string ItemImageMod = "item_image_variety";
        public const string EquipmentSlotsMod = "equipment_slots";

        private const string ItemDir = "Assets/images/item_candidates_dark";

        // 装備欄（333_equipment_slots の slots.REGIONS と同じ）。6列×10行、(x, y, 幅, 高さ)、y は上から。
        public const int SlotCols = 6, SlotRows = 10;
        public static readonly (string name, Rectangle cells)[] SlotRegions =
        {
            ("head", new Rectangle(2, 0, 2, 2)),
            ("right_hand", new Rectangle(4, 2, 2, 4)),
            ("left_hand", new Rectangle(0, 2, 2, 4)),
            ("body", new Rectangle(2, 2, 2, 4)),
            ("arms", new Rectangle(0, 6, 2, 2)),
            ("legs", new Rectangle(2, 6, 2, 2)),
            ("accessory1", new Rectangle(4, 6, 2, 2)),
            ("accessory2", new Rectangle(4, 8, 2, 2)),
            ("free1", new Rectangle(0, 8, 2, 2)),
            ("free2", new Rectangle(2, 8, 2, 2)),
        };

        // 設定のローダのフォルダ（runtime\ や runtime\mods\ を選んでいてもローダの直下へ寄せる）。無効なら null。
        public static string Root => Normalize(Settings.Current?.ModLoaderRoot);

        public static string Normalize(string dir)
        {
            if (string.IsNullOrWhiteSpace(dir)) return null;
            try
            {
                var d = new DirectoryInfo(dir.Trim());
                for (int i = 0; i < 3 && d != null; i++, d = d.Parent)
                    if (Directory.Exists(Path.Combine(d.FullName, "runtime", "mods"))) return d.FullName;
            }
            catch { }
            return null;
        }

        public static string StateDir => Root is string r ? Path.Combine(r, "state") : null;

        // runtime\mods\ の中で、名前が "<番号>_<name>" か "<name>" の MOD フォルダ。無ければ null。
        // root を渡すとそのローダで探す（設定画面の入力中の確認用）。
        public static string FindMod(string name, string root = null)
        {
            if ((root ?? Root) is not string r) return null;
            try
            {
                foreach (var d in Directory.EnumerateDirectories(Path.Combine(r, "runtime", "mods")))
                {
                    string n = Path.GetFileName(d);
                    int us = n.IndexOf('_');
                    if (n == name || (us > 0 && n.Substring(us + 1) == name && n[..us].All(char.IsDigit))) return d;
                }
            }
            catch { }
            return null;
        }

        // ---------------- アイテム画像 ----------------

        private static string _rootsKey;
        private static List<string> _roots = new();

        // image_src を解決する基準フォルダの並び（ゲーム導入先 → 128_ の分類フォルダ → state\item_image_cross）。
        // 設定が変わるまでは作り直さない。
        public static IReadOnlyList<string> AssetRoots()
        {
            string game = Settings.Current?.GameAssetRoot ?? "";
            string key = game + "|" + (Settings.Current?.ModLoaderRoot ?? "");
            if (key == _rootsKey) return _roots;
            var list = new List<string>();
            if (!string.IsNullOrWhiteSpace(game)) list.Add(game);
            if (FindMod(ItemImageMod) is string mod)
            {
                try
                {
                    string images = Path.Combine(mod, "images");
                    if (Directory.Exists(images))
                        foreach (var d in Directory.EnumerateDirectories(images).OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
                            list.Add(d);
                }
                catch { }
                if (StateDir is string st && Directory.Exists(Path.Combine(st, "item_image_cross")))
                    list.Add(Path.Combine(st, "item_image_cross"));
            }
            _roots = list; _rootsKey = key;
            return _roots;
        }

        // 基準フォルダのうち、ゲーム導入先以外（MOD の絵の置き場）か。
        public static bool IsModRoot(string root)
            => !string.Equals(root, Settings.Current?.GameAssetRoot ?? "", StringComparison.OrdinalIgnoreCase);

        // image_src（相対パス）を実ファイルへ。見つからなければ null。基準フォルダの外を指すパスは扱わない。
        public static string ResolveAsset(string rel)
        {
            if (string.IsNullOrEmpty(rel)) return null;
            foreach (var root in AssetRoots())
            {
                string full = SafePath.ResolveUnder(root, rel);
                if (full != null && File.Exists(full)) return full;
            }
            return null;
        }

        // 画像一覧（ImagePickerDialog）用: 各基準フォルダの item_candidates_dark（無ければ Assets\images）。
        public static IEnumerable<(string root, string itemsDir)> ItemImageDirs()
        {
            foreach (var root in AssetRoots())
            {
                string items = Path.Combine(root, ItemDir.Replace('/', Path.DirectorySeparatorChar));
                if (Directory.Exists(items)) { yield return (root, items); continue; }
                if (!IsModRoot(root))
                {
                    string imgs = Path.Combine(root, "Assets", "images");
                    yield return (root, Directory.Exists(imgs) ? imgs : root);
                }
            }
        }

        // ---------------- 装備欄 ----------------

        // 装備欄 MOD が使えるか（ローダとその MOD のフォルダがある）。
        public static bool EquipmentSlotsAvailable => FindMod(EquipmentSlotsMod) != null;

        // 主人公の持ち主の鍵（名前）。
        public static string PlayerOwner(JsonObject root)
        {
            string n = J.Str(J.Obj(root, "player_data"), "name").Trim();
            return n.Length > 0 ? n : null;
        }

        public static string NpcOwner(string npcId) => string.IsNullOrEmpty(npcId) ? null : "npc:" + npcId;

        // ローダが控えに使う世界名（world_data の world_name → name → title）。
        public static string WorldKey(JsonObject root)
        {
            var wd = J.Obj(root, "world_data");
            foreach (var k in new[] { "world_name", "name", "title" })
            {
                string v = J.Str(wd, k);
                if (v.Length > 0) return v;
            }
            return null;
        }

        // MOD の控えのうち、開いているセーブの周回のもの（`state\<folder>\<世界>×<主人公>.json`）。
        // 無ければ以前の版のローダが作った世界名だけの控え（`<世界>.json`）。どちらも無い・読めないときは null。
        // 世界名にファイル名へ使えない文字があるとき、ローダは名前に印を付けるので読めない（null を返す）。
        public static JsonObject ReadPlaythroughState(JsonObject root, string folder, bool allowWorldOnly = true)
            => OpenPlaythroughState(root, folder, allowWorldOnly).state;

        // ReadPlaythroughState と同じ控えを、ファイルの場所と一緒に返す。エディタで編集中（未保存）の控えがあればそれを返す
        //（同じ実体を返すので、そのまま書き換えて SetPendingState に渡せる）。控えが無いときは (周回の控えの場所, null)。
        public static (string path, JsonObject state) OpenPlaythroughState(JsonObject root, string folder, bool allowWorldOnly = true)
        {
            string first = null;
            foreach (var p in PlaythroughStatePaths(root, folder, allowWorldOnly))
            {
                first ??= p;
                if (_pendingState.TryGetValue(p, out var editing)) return (p, editing);
                if (!File.Exists(p)) continue;
                try { if (JsonNode.Parse(File.ReadAllText(p)) is JsonObject o) return (p, o); }
                catch { }
            }
            return (first, null);
        }

        // ---------------- MOD の控えの編集（保存時に書く） ----------------
        // MOD タブで編集した控えは、セーブを上書き保存したときにまとめて書く（装備欄の操作と同じ）。
        private static readonly Dictionary<string, JsonObject> _pendingState = new(StringComparer.OrdinalIgnoreCase);

        public static bool HasPendingState => _pendingState.Count > 0;
        public static bool IsPendingState(string path) => path != null && _pendingState.ContainsKey(path);
        public static void SetPendingState(string path, JsonObject state) { if (path != null && state != null) _pendingState[path] = state; }
        public static void ClearPendingState() => _pendingState.Clear();

        // 編集した控えをファイルへ書く。書けなかった控えの名前を返す（全部書けたら空）。
        public static List<string> CommitPendingState()
        {
            var failed = new List<string>();
            foreach (var (path, state) in _pendingState.ToList())
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(path));
                    Codec.WriteAtomic(path, new System.Text.UTF8Encoding(false).GetBytes(state.ToJsonString(StateJson)));
                    _pendingState.Remove(path);
                }
                catch { failed.Add(Path.GetFileName(path)); }
            }
            return failed;
        }

        // 控えの書き方（字下げあり・日本語はそのまま。ローダの書き方に近い形）。
        private static readonly System.Text.Json.JsonSerializerOptions StateJson = new()
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };

        private static IEnumerable<string> PlaythroughStatePaths(JsonObject root, string folder, bool allowWorldOnly)
        {
            if (StateDir is not string st) yield break;
            string world = WorldKey(root);
            if (world == null) yield break;
            string player = PlayerOwner(root);
            var stems = new List<string>();
            if (player != null) stems.Add(world + "×" + player);
            if (allowWorldOnly || player == null) stems.Add(world);
            foreach (var stem in stems)
                if (stem.IndexOfAny(Path.GetInvalidFileNameChars()) < 0)
                    yield return Path.Combine(st, folder, stem + ".json");
        }

        // この持ち主の装備欄（品の鍵 → (x, y) y は上から）。控えが無い・読めないときは空。
        // 控えは周回（世界×主人公）ごと。以前の版のローダが作った世界名だけの控えも見る。
        // エディタで装備した・外した（まだ保存していない）操作を重ねた結果を返す。
        public static Dictionary<string, Point> EquippedOf(JsonObject root, string owner)
        {
            var (path, map) = FindEquipped(root, owner);
            path ??= TargetPath(root);
            foreach (var op in _pending.Where(o => o.path == path && o.owner == owner))
            {
                if (op.pos is Point p) map[op.key] = p;
                else map.Remove(op.key);
            }
            return map;
        }

        private static (string path, Dictionary<string, Point> map) FindEquipped(JsonObject root, string owner)
        {
            var result = new Dictionary<string, Point>(StringComparer.Ordinal);
            if (string.IsNullOrEmpty(owner) || !EquipmentSlotsAvailable) return (null, result);
            foreach (var p in PlaythroughStatePaths(root, "equipment_slots", true))
            {
                if (!File.Exists(p)) continue;
                try
                {
                    if (JsonNode.Parse(File.ReadAllText(p)) is not JsonObject all || all[owner] is not JsonObject mine) continue;
                    foreach (var kv in mine)
                        if (kv.Value is JsonArray a && a.Count >= 2)
                            result[kv.Key] = new Point((int)ToLong(a[0]), (int)ToLong(a[1]));
                    if (result.Count > 0) return (p, result);
                }
                catch { }
            }
            return (null, result);
        }

        // まだ装備の無い持ち主の控えの置き場。周回の控え（<世界>×<主人公>.json）。無ければ保存時に作る。
        // 仲間（npc:<id>）も主人公と同じファイルに入る（333_ と同じ）。
        private static string TargetPath(JsonObject root)
            => PlaythroughStatePaths(root, "equipment_slots", false).FirstOrDefault();

        // ---------------- 装備する・外す（保存時に控えへ反映） ----------------
        // 操作は、セーブを保存するまで控え（state\equipment_slots）へは書かない。
        // 先に控えだけ書くと、セーブを保存しなかったときに控えとセーブの品の位置が食い違うため。
        // pos が null の操作は外す、値があればその位置（部位の左上。y は上から）へ装備する。
        private static readonly List<(string path, string owner, string key, Point? pos)> _pending = new();

        public static bool HasPendingEquipment => _pending.Count > 0;

        // 別のファイルを開いた・保存せずに閉じたときは捨てる。
        public static void ClearPendingEquipment() => _pending.Clear();

        // 持ち主の装備欄から品を外す予定にする。装備欄に居なければ false。
        public static bool Unequip(JsonObject root, string owner, string key)
        {
            if (!EquippedOf(root, owner).ContainsKey(key)) return false;
            string path = FindEquipped(root, owner).path ?? TargetPath(root);
            if (path == null) return false;
            _pending.Add((path, owner, key, null));
            return true;
        }

        // 持ち主の装備欄の pos（部位の左上）へ品を入れる予定にする。控えの置き場が決まらなければ false。
        public static bool Equip(JsonObject root, string owner, string key, Point pos)
        {
            if (string.IsNullOrEmpty(owner) || !EquipmentSlotsAvailable) return false;
            string path = FindEquipped(root, owner).path ?? TargetPath(root);
            if (path == null) return false;
            _pending.Add((path, owner, key, pos));
            return true;
        }

        // 予定の操作を控えへ書く（セーブの保存が成った後に呼ぶ）。書けなかった控えの名前を返す（全部書けたら空）。
        public static List<string> CommitPendingEquipment()
        {
            var failed = new List<string>();
            foreach (var group in _pending.GroupBy(u => u.path).ToList())
            {
                try
                {
                    JsonObject all = File.Exists(group.Key)
                        ? JsonNode.Parse(File.ReadAllText(group.Key)) as JsonObject ?? throw new InvalidDataException()
                        : new JsonObject();
                    foreach (var (_, owner, key, pos) in group)
                    {
                        if (all[owner] is not JsonObject mine) all[owner] = mine = new JsonObject();
                        if (pos is Point p) mine[key] = new JsonArray(p.X, p.Y);
                        else mine.Remove(key);
                    }
                    var bytes = new System.Text.UTF8Encoding(false).GetBytes(all.ToJsonString(StateJson));
                    Directory.CreateDirectory(Path.GetDirectoryName(group.Key));
                    Codec.WriteAtomic(group.Key, bytes);
                    _pending.RemoveAll(u => u.path == group.Key);
                }
                catch { failed.Add(Path.GetFileName(group.Key)); }
            }
            return failed;
        }

        // ---------------- 部位の決まり（333_equipment_slots の slots.py と同じ） ----------------

        // 部位 → 入る種類。free は手（武器・盾）以外の防具なら何でも入る。
        private static readonly Dictionary<string, string> RegionKind = new()
        {
            ["head"] = "head", ["body"] = "body", ["arms"] = "arms", ["legs"] = "legs",
            ["left_hand"] = "hand", ["right_hand"] = "hand",
            ["accessory1"] = "accessory", ["accessory2"] = "accessory",
            ["free1"] = "free", ["free2"] = "free",
        };

        // attributes.item_detail → 種類。武器は種類を問わず手、盾も手。知らない防具は装飾。
        private static readonly Dictionary<string, string> WearableKind = new()
        {
            ["shield"] = "hand", ["headgear"] = "head", ["clothing"] = "body", ["body_armor"] = "body",
            ["gauntlets"] = "arms", ["legwear"] = "legs", ["leg_armor"] = "legs", ["accessory"] = "accessory",
        };

        // この品が入る種類（装備できない品は null）。
        public static string KindOf(JsonObject item)
        {
            string type = J.Str(item, "item_type");
            if (type == "weapon") return "hand";
            if (type != "wearable") return null;
            return WearableKind.TryGetValue(J.Str(J.Obj(item, "attributes"), "item_detail"), out var k) ? k : "accessory";
        }

        // 部位 name に種類 kind の品が入るか。
        public static bool Takes(string region, string kind)
        {
            if (kind == null || !RegionKind.TryGetValue(region, out var rk)) return false;
            return rk == "free" ? kind != "hand" : rk == kind;
        }

        public static bool IsFreeRegion(string region) => RegionKind.TryGetValue(region, out var rk) && rk == "free";

        // 本体に渡す装備（333_ と同じ選び方）: 手にある武器のうち攻撃力が最高の1つ、
        // 自由枠を除く全部位の防具（盾を含む）のうち防御力が最高の1つ。同点は部位の並び（右手が先）。
        public static (string weapon, string wearable) ChooseGameEquipments(JsonObject inv, Dictionary<string, Point> map)
        {
            string weapon = null, wearable = null;
            double bestA = double.MinValue, bestD = double.MinValue;
            foreach (var (name, cells) in SlotRegions)
            {
                if (IsFreeRegion(name)) continue;
                foreach (var kv in map.Where(kv => cells.Contains(kv.Value)))
                {
                    if (inv?[kv.Key] is not JsonObject it) continue;
                    var attrs = J.Obj(it, "attributes");
                    string type = J.Str(it, "item_type");
                    if (type == "weapon" && RegionKind[name] == "hand")
                    {
                        double a = Stat(attrs, "攻撃力");
                        if (a > bestA) { bestA = a; weapon = kv.Key; }
                    }
                    else if (type == "wearable")
                    {
                        double d = Stat(attrs, "防御力");
                        if (d > bestD) { bestD = d; wearable = kv.Key; }
                    }
                }
            }
            return (weapon, wearable);
        }

        private static double Stat(JsonObject attrs, string key)
        {
            if (attrs?[key] is not JsonValue v) return 0;
            if (v.TryGetValue<double>(out var d)) return d;
            if (v.TryGetValue<long>(out var l)) return l;
            return v.TryGetValue<string>(out var s) && double.TryParse(s, out var p) ? p : 0;
        }

        private static long ToLong(JsonNode n)
        {
            if (n is JsonValue v)
            {
                if (v.TryGetValue<long>(out var l)) return l;
                if (v.TryGetValue<double>(out var d)) return (long)d;
            }
            return 0;
        }

        // 部位の表示名（i18n キー equip.<部位>）。
        public static string SlotLabel(string name) => I18n.T("equip." + name);
    }
}
