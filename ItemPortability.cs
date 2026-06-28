// アイテムのエクスポート/インポート。NPC 移植(NpcPortability)のアイテム版。
// エクスポート: 1アイテムのレコード(JSON)＋ image_src が指す画像を 1 つの zip にまとめる。
// インポート: zip から取り込み、対象インベントリへ新しい item ID として追加する。
//   画像は GameAssetRoot 配下の元の相対パスへ復元し（既存があれば触らない）、image_src はそのまま使う。
using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;

namespace InstantaleSaveEditor
{
    // zip から読み出したアイテムパッケージ。
    internal sealed class ItemPackage
    {
        public JsonObject Item;            // アイテム本体（クローン済み）
        public string OriginalName = "";   // 出力時の名前
        public string ImageRelPath = "";   // image_src（GameAssetRoot からの相対パス）
        public byte[] ImageBytes;          // 同梱画像（無ければ null）
    }

    // アイテム移植（エクスポート/インポート）の入出力ロジック。UI は InventoryPanel 側。
    internal static class ItemPortability
    {
        public const string Format = "instantale_item";

        // ---------------- エクスポート ----------------
        // アイテム本体 + image_src が指す画像(あれば)を destZip に書き出す。
        public static void Export(JsonObject item, string assetRoot, string originalId, string destZip)
        {
            var clone = (JsonObject)item.DeepClone();
            string imageRel = J.Str(clone, "image_src");

            var wrapper = new JsonObject
            {
                ["format"] = Format,
                ["version"] = 1,
                ["original_id"] = originalId ?? "",
                ["original_name"] = J.Str(item, "name"),
                ["image_src"] = imageRel,
                ["item"] = clone,
            };

            if (File.Exists(destZip)) File.Delete(destZip);
            using var zip = ZipFile.Open(destZip, ZipArchiveMode.Create);
            var entry = zip.CreateEntry("item.json");
            using (var w = new StreamWriter(entry.Open(), new UTF8Encoding(false)))
                w.Write(wrapper.ToJsonString(Codec.Pretty));

            if (!string.IsNullOrEmpty(imageRel) && !string.IsNullOrEmpty(assetRoot))
            {
                string full = Path.Combine(assetRoot, imageRel.Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(full))
                    zip.CreateEntryFromFile(full, "image/" + Path.GetFileName(full));
            }
        }

        // ---------------- インポート(読込) ----------------
        // zip を解析して ItemPackage を返す。形式が不正なら例外。
        public static ItemPackage ReadPackage(string zipPath)
        {
            using var zip = ZipFile.OpenRead(zipPath);
            var jsonEntry = zip.GetEntry("item.json")
                ?? throw new InvalidDataException(I18n.T("itempkg.err.noJson"));
            JsonObject wrapper;
            using (var r = new StreamReader(jsonEntry.Open(), Encoding.UTF8))
                wrapper = JsonNode.Parse(r.ReadToEnd())?.AsObject();
            if (wrapper == null || J.Str(wrapper, "format") != Format)
                throw new InvalidDataException(I18n.T("itempkg.err.badFormat"));
            if (wrapper["item"] is not JsonObject item)
                throw new InvalidDataException(I18n.T("itempkg.err.noItemData"));

            var pkg = new ItemPackage
            {
                Item = (JsonObject)item.DeepClone(),
                OriginalName = J.Str(wrapper, "original_name", J.Str(item, "name")),
                ImageRelPath = J.Str(wrapper, "image_src", J.Str(item, "image_src")),
            };
            foreach (var e in zip.Entries)
            {
                if (!e.FullName.StartsWith("image/") || e.Name.Length == 0) continue;
                using var s = e.Open();
                using var ms = new MemoryStream();
                s.CopyTo(ms);
                pkg.ImageBytes = ms.ToArray();
                break;   // アイテムの画像は1枚のみ
            }
            return pkg;
        }

        // 同梱画像を GameAssetRoot 配下の元の相対パスへ書き出す。
        // 既に同名ファイルがある（ゲーム標準アセット等）場合は触らない。
        public static void PlaceImage(string assetRoot, string imageRel, byte[] bytes)
        {
            if (string.IsNullOrEmpty(assetRoot) || string.IsNullOrEmpty(imageRel) || bytes == null) return;
            string full = Path.Combine(assetRoot, imageRel.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(full)) return;
            string dir = Path.GetDirectoryName(full);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllBytes(full, bytes);
        }

        // 指定インベントリで footprint(w×h) が収まる空き位置(grid_pos)を返す。
        // grid_pos はゲームと同じモデル座標のまま扱う（表示の上下反転は無関係。重なり判定だけ行う）。
        // 空きが無ければ (0,0) を返す（ユーザーがドラッグで移動できる）。
        public static JsonArray FindFreeGridPos(JsonObject inv, int w, int h, int cols, int rows)
        {
            cols = Math.Max(1, cols); rows = Math.Max(1, rows);
            w = Math.Clamp(w, 1, cols); h = Math.Clamp(h, 1, rows);

            var occ = new bool[cols, rows];
            if (inv != null)
                foreach (var kv in inv)
                {
                    if (kv.Value is not JsonObject o) continue;
                    int iw = Math.Max(1, (int)J.Int(o, "width_slots", 1));
                    int ih = Math.Max(1, (int)J.Int(o, "height_slots", 1));
                    var gp = J.Arr(o, "grid_pos");
                    int x = 0, y = 0;
                    if (gp != null && gp.Count >= 2) { x = (int)ParseInt(gp[0]); y = (int)ParseInt(gp[1]); }
                    for (int cx = x; cx < x + iw && cx < cols; cx++)
                        for (int cy = y; cy < y + ih && cy < rows; cy++)
                            if (cx >= 0 && cy >= 0) occ[cx, cy] = true;
                }

            for (int y = 0; y <= rows - h; y++)
                for (int x = 0; x <= cols - w; x++)
                    if (IsFree(occ, x, y, w, h)) return new JsonArray(x, y);
            return new JsonArray(0, 0);
        }

        // FindFreeGridPos の「画面の左上から詰める」版。返り値はゲームのモデル座標(grid_pos)。
        // 表示は上下反転（ゲームは grid_pos の y を下端起点とする）のため、画面の読み順
        // （上段から、各段は左→右、埋まったら一つ下の段の左端）で空きを探し、見つけた画面座標を
        // モデル座標へ戻して返す。占有判定は InventoryGridControl.CellRect と同じクランプで合わせる。
        public static JsonArray FindFreeGridPosTopLeft(JsonObject inv, int w, int h, int cols, int rows)
        {
            cols = Math.Max(1, cols); rows = Math.Max(1, rows);
            w = Math.Clamp(w, 1, cols); h = Math.Clamp(h, 1, rows);

            // 画面座標（上端起点）での占有マップ。
            var occ = new bool[cols, rows];
            if (inv != null)
                foreach (var kv in inv)
                {
                    if (kv.Value is not JsonObject o) continue;
                    int iw = Math.Min(cols, Math.Max(1, (int)J.Int(o, "width_slots", 1)));
                    int ih = Math.Min(rows, Math.Max(1, (int)J.Int(o, "height_slots", 1)));
                    var gp = J.Arr(o, "grid_pos");
                    int x = 0, y = 0;
                    if (gp != null && gp.Count >= 2) { x = (int)ParseInt(gp[0]); y = (int)ParseInt(gp[1]); }
                    x = Math.Clamp(x, 0, Math.Max(0, cols - iw));
                    y = Math.Clamp(y, 0, Math.Max(0, rows - ih));
                    int sy0 = rows - ih - y;   // モデル y(下端起点) → 画面 y(上端起点)
                    for (int cx = x; cx < x + iw; cx++)
                        for (int cy = sy0; cy < sy0 + ih; cy++)
                            if (cx >= 0 && cy >= 0 && cx < cols && cy < rows) occ[cx, cy] = true;
                }

            // 画面の読み順（上段→下段、各段は左→右）で空きを探す。
            for (int sy = 0; sy <= rows - h; sy++)
                for (int sx = 0; sx <= cols - w; sx++)
                    if (IsFree(occ, sx, sy, w, h)) return new JsonArray(sx, rows - h - sy);
            return new JsonArray(0, rows - h);   // 空き無し時は画面左上（モデル座標）へ
        }

        private static bool IsFree(bool[,] occ, int x, int y, int w, int h)
        {
            for (int cx = x; cx < x + w; cx++)
                for (int cy = y; cy < y + h; cy++)
                    if (occ[cx, cy]) return false;
            return true;
        }

        // grid_pos 要素を安全に long として読む（JsonElement 格納と書き戻し int 格納の双方に対応）。
        private static long ParseInt(JsonNode n)
        {
            if (n is JsonValue v)
            {
                if (v.TryGetValue<long>(out long l)) return l;
                if (v.TryGetValue<int>(out int i)) return i;
                if (v.TryGetValue<double>(out double d)) return (long)d;
                if (v.TryGetValue<decimal>(out decimal m)) return (long)m;
                if (v.TryGetValue<string>(out string s) && long.TryParse(s, out long ls)) return ls;
            }
            return 0;
        }
    }
}
