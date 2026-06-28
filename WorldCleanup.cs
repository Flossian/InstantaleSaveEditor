// ワールドデータのクリーンアップ（ツールメニューから呼ぶ一括処理）。
// プレイ進行の痕跡を初期状態へ戻す用途:
//   - story_quests の config.status が incomplete 以外（completed 等）なら incomplete へ
//   - 全 NPC の life_log を空配列に
//   - 全 NPC の current_log を空配列に
//   - 全 NPC の relationship を初期値（player のみ）に置き換え
// 保存対象モデル（root）を直接書き換える。呼び出し側で world タブを再バインドすること。
using System.Text.Json.Nodes;

namespace InstantaleSaveEditor
{
    internal static class WorldCleanup
    {
        // クリーンアップ結果の件数。
        public readonly struct Result
        {
            public readonly int Quests;   // status を incomplete に戻したストーリークエスト数
            public readonly int Npcs;      // 初期化した NPC 数
            public Result(int quests, int npcs) { Quests = quests; Npcs = npcs; }
        }

        // root（savedata / world_data）に対してクリーンアップを実行し、件数を返す。
        public static Result Run(JsonObject root)
        {
            int quests = 0, npcs = 0;

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

            if (J.Obj(root, "npcs") is JsonObject ns)
            {
                foreach (var kv in ns)
                {
                    if (kv.Value is not JsonObject npc) continue;
                    npc["life_log"] = new JsonArray();
                    npc["current_log"] = new JsonArray();
                    npc["relationship"] = NewInitialRelationship();
                    npcs++;
                }
            }

            return new Result(quests, npcs);
        }

        // NPC の初期 relationship（player のみ・初対面状態）。
        // ノードは 1 親しか持てないため、NPC ごとに新規生成する。
        private static JsonObject NewInitialRelationship() => new()
        {
            ["player"] = new JsonObject
            {
                ["affinity"] = 0,
                ["affinity_text"] = new JsonArray { "警戒心がある" },
                ["relationship"] = new JsonArray { "初対面" },
                ["conversation_count"] = 0,
            }
        };
    }
}
