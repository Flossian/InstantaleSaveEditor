// ワールドデータのクリーンアップ（ツールメニューから呼ぶ一括処理）。
// プレイ進行の痕跡を初期状態へ戻す用途:
//   - story_quests の config.status が incomplete 以外（completed 等）なら incomplete へ
//   - 全 NPC の life_log / current_log を空配列に
//   - 全 NPC の memory（記憶の要約）と knowledge を初期値に
//   - 全 NPC の relationship を初期値（player のみ）に置き換え
//   - 全 NPC の config から雇用の記録（employed_price / companion_quest_growth）を外す
//   - パーティ（game_variables.party / original_party）を解散し、仲間を初期配置へ戻す
//     （relationship の "同行中" が消えるため、名簿だけ残すと会話の分岐と食い違う）
// 保存対象モデル（root）を直接書き換える。呼び出し側で world / 変数タブを再バインドすること。
// 初期値（好感度・関係・記憶の要約）はワールドの言語（GameText）で書く。
// MOD（InstantaleModLoader）が state\ に持つ世界ごとの記録はセーブの外にあり、ここでは消えない。
using System.Text.Json.Nodes;

namespace InstantaleSaveEditor
{
    internal static class WorldCleanup
    {
        // config から外す、雇用・同行で付く項目（ゲームが仲間に付ける）。
        private static readonly string[] EmploymentConfigKeys = { "employed_price", "companion_quest_growth" };

        // クリーンアップ結果の件数。
        public readonly struct Result
        {
            public readonly int Quests;   // status を incomplete に戻したストーリークエスト数
            public readonly int Npcs;      // 初期化した NPC 数
            public readonly int Party;     // パーティから外した仲間の数
            public Result(int quests, int npcs, int party) { Quests = quests; Npcs = npcs; Party = party; }
        }

        // root（savedata / world_data）に対してクリーンアップを実行し、件数を返す。
        public static Result Run(JsonObject root)
        {
            int quests = 0, npcs = 0, party = 0;

            if (J.Obj(root, "story_quests") is JsonObject sq)
            {
                foreach (var kv in sq)
                {
                    if (kv.Value is not JsonObject q) continue;
                    var cfg = J.Obj(q, "config");
                    if (cfg == null) continue;
                    if (J.Str(cfg, "status") != "incomplete")
                    {
                        cfg["status"] = "incomplete";
                        quests++;
                    }
                }
            }

            string lang = GameText.LanguageOf(root);   // 初期値はゲームと同じくワールドの言語で書く
            var ns = J.Obj(root, "npcs");
            if (ns != null)
            {
                foreach (var kv in ns)
                {
                    if (kv.Value is not JsonObject npc) continue;
                    npc["life_log"] = new JsonArray();
                    npc["current_log"] = new JsonArray();
                    ResetMemory(J.Obj(npc, "memory"), lang);
                    if (npc.ContainsKey("knowledge")) npc["knowledge"] = new JsonArray();
                    npc["relationship"] = GameText.InitialRelationship(lang, affinityAsArray: true);
                    if (J.Obj(npc, "config") is JsonObject cfg)
                        foreach (var k in EmploymentConfigKeys) cfg.Remove(k);
                    npcs++;
                }
            }

            if (J.Obj(root, "game_variables") is JsonObject gv)
            {
                foreach (var key in new[] { "party", "original_party" })
                {
                    if (J.Arr(gv, key) is not JsonArray arr) continue;
                    for (int i = arr.Count - 1; i >= 0; i--)
                    {
                        string id = arr[i]?.ToString() ?? "";
                        if (id == "player") continue;
                        arr.RemoveAt(i);
                        if (key == "party")
                        {
                            party++;
                            ReturnHome(ns?[id] as JsonObject);
                        }
                    }
                }
            }

            return new Result(quests, npcs, party);
        }

        // memory の各項目を初期値へ戻す（項目の並びは変えない。配列は空、文字列は空、brief_summary は「ゲーム開始」）。
        private static void ResetMemory(JsonObject memory, string lang)
        {
            if (memory == null) return;
            foreach (var key in memory.Select(p => p.Key).ToList())
            {
                if (key == "brief_summary") memory[key] = GameText.GameStart(lang);
                else if (memory[key] is JsonArray) memory[key] = new JsonArray();
                else if (memory[key] is JsonValue v && v.TryGetValue<string>(out _)) memory[key] = "";
            }
        }

        // 外した仲間を初期配置（雇われた場所）へ戻す。ゲームのパーティ解散と同じ置き先。
        private static void ReturnHome(JsonObject npc)
        {
            if (J.Obj(npc, "initial_location") is not JsonObject il) return;
            string area = J.Str(il, "area");
            if (string.IsNullOrEmpty(area)) return;
            npc["current_area"] = area;
            npc["current_location"] = il["facility"]?.DeepClone();
        }

    }
}
