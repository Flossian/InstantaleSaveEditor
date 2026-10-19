// index（採番カウンタ）が実在のレコード id に追いついているかの検査と修復。
//
// ゲームは area / node / facility / npc / quest / item の新しい id を index から連番で振り、
// 同じ id のレコードが既にあっても構わず上書きする（新しい町の店主が既存 NPC を潰す実例がある）。
// index が実在の最大 id 以下のまま残っていると、次の生成でゲームが既存のレコードを上書きする。
// 過去の版のエディタや MOD が index を進めずに足したレコードがあるとこの状態になるため、
// 読み込み時に検査し、遅れているキーを「実在の最大 id + 1」へ進める。
// story_quests は quests と別の id 空間（0〜4 の固定）なので数えない。
using System.Text.Json.Nodes;

namespace InstantaleSaveEditor
{
    internal static class IndexAudit
    {
        // 遅れているキー 1 件（index の値と、実在する最大の id）。
        public readonly record struct Lag(string Key, long Index, long Max);

        // index が実在の最大 id 以下になっているキーを列挙する。index に無いキーは見ない。
        public static List<Lag> Find(JsonObject root)
        {
            var lags = new List<Lag>();
            if (J.Obj(root, "index") is not JsonObject index) return lags;
            foreach (var (key, max) in MaxIds(root))
            {
                if (!index.ContainsKey(key) || max < 0) continue;
                long cur = J.Int(index, key, -1);
                if (cur < 0) continue;   // 数値でない値は触らない
                if (max >= cur) lags.Add(new Lag(key, cur, max));
            }
            return lags;
        }

        // 遅れているキーを「実在の最大 id + 1」へ進める（戻しはしない）。
        public static void Repair(JsonObject root, IEnumerable<Lag> lags)
        {
            if (J.Obj(root, "index") is not JsonObject index) return;
            foreach (var l in lags)
                if (J.Int(index, l.Key, -1) <= l.Max) index[l.Key] = l.Max + 1;
        }

        // 種類ごとの実在する最大の数値 id（無ければ -1）。
        private static IEnumerable<(string key, long max)> MaxIds(JsonObject root)
        {
            long area = -1, node = -1, fac = -1;
            if (J.Obj(root, "areas") is JsonObject areas)
                foreach (var akv in areas)
                {
                    area = Math.Max(area, Num(akv.Key));
                    foreach (var (nid, n) in WorldSync.EnumNodes(akv.Value as JsonObject))
                    {
                        node = Math.Max(node, Num(nid));
                        foreach (var (fid, _) in WorldSync.EnumFacilities(n)) fac = Math.Max(fac, Num(fid));
                    }
                }
            yield return ("area", area);
            yield return ("node", node);
            yield return ("facility", fac);
            yield return ("npc", MaxKey(J.Obj(root, "npcs")));
            yield return ("quest", MaxKey(J.Obj(root, "quests")));
            var items = InventoryPanel.TakenItemNumbers(root, null);
            yield return ("item", items.Count == 0 ? -1 : items.Max());
        }

        private static long MaxKey(JsonObject dict)
        {
            long m = -1;
            if (dict != null) foreach (var kv in dict) m = Math.Max(m, Num(kv.Key));
            return m;
        }

        private static long Num(string id) => long.TryParse(id, out long n) && n >= 0 ? n : -1;
    }
}
