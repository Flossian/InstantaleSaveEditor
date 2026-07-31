// アイテム倉庫。ツール側でアイテムを保持し、exe 隣の item\ 配下へ
// 1アイテム=1 zip（{id}_{名前}.zip、エクスポートと同じ形式）で永続化する。
// 2系統を扱う:
//   ・キャラクター個別倉庫: item\{ワールド名}\{キャラ名}\  （キャラ単位。InventoryPanel が Bind 時に生成）
//   ・全キャラ・ワールド共通倉庫: item\shared\          （静的シングルトン ItemWarehouse.Shared）
// 1インスタンス=1ディレクトリ。移動ロジック（インベントリ⇄倉庫）は InventoryPanel が担う。
using System.Text.Json.Nodes;

namespace InstantaleSaveEditor
{
    internal sealed class ItemWarehouse
    {
        // 倉庫グリッドの寸法（収まらない分はスクロール）。インベントリ2つ分を横並びにするため控えめにする。
        public const int Cols = 6;
        public const int Rows = 12;

        private readonly string _dir;                                                    // この倉庫の zip 保存先（null/空なら無効）
        private readonly JsonObject _items = new();                                       // id -> アイテム本体
        private readonly Dictionary<string, string> _files = new(StringComparer.Ordinal); // id -> zip パス
        private bool _loaded;

        public ItemWarehouse(string dir) { _dir = dir; }

        // 全キャラ・ワールド共通の倉庫（item\shared）。
        private static ItemWarehouse _shared;
        public static ItemWarehouse Shared => _shared ??= new ItemWarehouse(Path.Combine(BaseDir(), "shared"));

        // exe 隣の item ディレクトリ。単一ファイル発行では Assembly.Location が空になるため ProcessPath を使う。
        public static string BaseDir()
        {
            string dir = Path.GetDirectoryName(Environment.ProcessPath ?? "") ?? AppContext.BaseDirectory;
            return Path.Combine(dir, "item");
        }

        // キャラクター個別倉庫のディレクトリ（item\{world}\{char}）。world/char が空なら null（無効）。
        public static string CharDir(string worldName, string charName)
        {
            if (string.IsNullOrWhiteSpace(worldName) || string.IsNullOrWhiteSpace(charName)) return null;
            return Path.Combine(BaseDir(), Safe(worldName), Safe(charName));
        }

        // この倉庫が利用可能か（保存先ディレクトリが定まっているか）。
        public bool Available => !string.IsNullOrEmpty(_dir);

        // 倉庫アイテム辞書（グリッド表示用。InventoryPanel が Bind する）。
        public JsonObject Items => _items;

        // 初回のみ保存先 zip を読み込む（id は読み込み順に採番。grid_pos は左上から詰める）。
        public void EnsureLoaded()
        {
            if (_loaded) return;
            _loaded = true;
            if (!Available || !Directory.Exists(_dir)) return;
            foreach (var zip in Directory.EnumerateFiles(_dir, "*.zip"))
            {
                try
                {
                    var pkg = ItemPortability.ReadPackage(zip);
                    string id = NextId();
                    PlaceInGrid(pkg.Item);
                    _items[id] = pkg.Item;
                    _files[id] = zip;
                }
                catch { /* 壊れた zip は無視して読み込みを継続 */ }
            }
        }

        // インベントリから受け取ったアイテムを倉庫へ追加し、zip を書き出す。返り値=新 id（無効なら null）。
        public string Add(JsonObject item, string assetRoot)
        {
            if (!Available) return null;
            EnsureLoaded();
            var clone = (JsonObject)item.DeepClone();
            string id = NextId();
            PlaceInGrid(clone);

            Directory.CreateDirectory(_dir);
            string path = Path.Combine(_dir, $"{id}_{Safe(J.Str(clone, "name", "item"))}.zip");
            ItemPortability.Export(clone, assetRoot ?? "", id, path);

            _items[id] = clone;
            _files[id] = path;
            return id;
        }

        // 倉庫からアイテムを取り出す。zip から再読込して画像を assetRoot へ復元し、辞書/zip から取り除く。
        // 返り値=インベントリへ挿入するアイテム本体（grid_pos は呼び出し側で再設定する）。無ければ null。
        public JsonObject TakeOut(string id, string assetRoot)
        {
            EnsureLoaded();
            if (!_items.ContainsKey(id)) return null;

            JsonObject result = null;
            if (_files.TryGetValue(id, out var path) && File.Exists(path))
            {
                try
                {
                    var pkg = ItemPortability.ReadPackage(path);
                    try { ItemPortability.PlaceImage(assetRoot ?? "", pkg.ImageRelPath, pkg.ImageBytes); } catch { }
                    result = pkg.Item;
                }
                catch { /* zip 読込失敗時は in-memory を使う */ }
            }
            result ??= (JsonObject)((JsonObject)_items[id]).DeepClone();
            Remove(id);
            return result;
        }

        // 倉庫からアイテムを削除し、対応する zip も消す。
        public void Remove(string id)
        {
            if (_files.TryGetValue(id, out var path))
            {
                try { if (File.Exists(path)) File.Delete(path); } catch { /* 削除失敗は無視 */ }
                _files.Remove(id);
            }
            _items.Remove(id);
        }

        // 倉庫内の空き位置を grid_pos へ設定する（画面の左上から読み順に詰める）。
        private void PlaceInGrid(JsonObject item)
        {
            int w = Math.Max(1, (int)J.Int(item, "width_slots", 1));
            int h = Math.Max(1, (int)J.Int(item, "height_slots", 1));
            item["grid_pos"] = ItemPortability.FindFreeGridPosTopLeft(_items, w, h, Cols, Rows);
        }

        // 倉庫に追加する新 id（item_N の N=既存最大+1。インベントリと同じ採番）。
        private string NextId() => InventoryPanel.NextItemId(_items);

        // ファイル/フォルダ名に使えない文字を '_' に置き換える（".." や予約デバイス名も潰す）。
        private static string Safe(string s) => SafePath.FileName(s, "item");
    }
}
