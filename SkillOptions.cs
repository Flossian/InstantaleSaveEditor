// スキル/効果の編集ダイアログで使う候補値を供給する。
// 既定候補（実データに無くても出す基本セット）に、読み込んだセーブから実際に使われている値を
// 抽出してマージする。抽出元は player_data.skills と全 npcs[*].skills（effects / effects_per_turn を再帰）。
// Collect はファイル読込（WorldTab.Bind）時に呼ぶ。これによりサンプルに無い値（escape_from_battle 等）も
// プルダウンに自動で現れる。
using System.Text.Json.Nodes;

namespace InstantaleSaveEditor
{
    internal static class SkillOptions
    {
        // キー → 既定候補。実データに無くても常に出す基本セット（順序を保つ）。
        private static readonly Dictionary<string, string[]> Defaults = new(StringComparer.Ordinal)
        {
            ["element"] = new[] { "火", "水", "土", "風", "光", "闇", "雷", "氷", "無" },
            ["skill_type"] = new[] { "physical", "magical", "hybrid", "other" },
            ["effect.type"] = new[] { "instant_damage", "instant_heal", "buff", "debuff", "text_status", "escape_from_battle" },
            ["target_type"] = new[] { "single_enemy", "all_enemies", "self", "single_ally", "all_allies", "everyone" },
            ["power"] = new[] { "weak", "normal", "strong", "very_strong", "extreme" },
            ["attribute_type"] = new[] { "str", "dex", "con", "int", "wis", "cha" },
        };

        // セーブから抽出した値（キー → 出現順の集合）。Collect で作り直す。
        private static Dictionary<string, List<string>> _extracted = new(StringComparer.Ordinal);

        // キーの候補一覧（既定 → 抽出の順。重複は除く）。未定義キーは抽出分のみ。
        public static string[] Get(string key)
        {
            var list = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            if (Defaults.TryGetValue(key, out var def))
                foreach (var v in def) if (seen.Add(v)) list.Add(v);
            if (_extracted.TryGetValue(key, out var ex))
                foreach (var v in ex) if (seen.Add(v)) list.Add(v);
            return list.ToArray();
        }

        // セーブ全体（player_data.skills と全 npcs[*].skills）からスキル/効果の値を抽出し直す。
        public static void Collect(JsonObject root)
        {
            var acc = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            void Add(string key, string val)
            {
                if (string.IsNullOrEmpty(val)) return;
                if (!acc.TryGetValue(key, out var lst)) acc[key] = lst = new List<string>();
                if (!lst.Contains(val)) lst.Add(val);
            }
            void WalkEffect(JsonObject e)
            {
                if (e == null) return;
                Add("effect.type", J.Str(e, "type"));
                Add("target_type", J.Str(e, "target_type"));
                Add("power", J.Str(e, "power"));
                Add("attribute_type", J.Str(e, "attribute_type"));
                if (e["effects_per_turn"] is JsonArray pt)
                    foreach (var n in pt) WalkEffect(n as JsonObject);
            }
            void WalkSkills(JsonObject skills)
            {
                if (skills == null) return;
                foreach (var kv in skills)
                    if (kv.Value is JsonObject s)
                    {
                        Add("element", J.Str(s, "element"));
                        Add("skill_type", J.Str(s, "skill_type"));
                        if (s["effects"] is JsonArray ef)
                            foreach (var n in ef) WalkEffect(n as JsonObject);
                    }
            }

            if (root != null)
            {
                WalkSkills(J.Obj(J.Obj(root, "player_data"), "skills"));
                if (root["npcs"] is JsonObject npcs)
                    foreach (var kv in npcs)
                        if (kv.Value is JsonObject npc) WalkSkills(J.Obj(npc, "skills"));
            }
            _extracted = acc;
        }
    }
}
