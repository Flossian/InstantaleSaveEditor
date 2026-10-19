// パーティ（game_variables.party）へ NPC を出し入れするときの、名簿以外の整合。
//
// ゲームの会話・移動の分岐は、同行者かどうかを名簿ではなく NPC の
// relationship.player.relationship の配列（雇用した仲間は "同行中"、家族は "家族"）で見る。
// 名簿だけを書き換えると、仲間なのに「雇いたい」が出る／外したのに「ここで別れる」が出る等の
// 食い違いになるため、出し入れに合わせて "同行中" を付け外しする（"家族" には触らない）。
//
// また、ゲームは NPC の詳細（スキル・HP・立ち絵）を会話の直前にだけ作る（config.level_of_detail が 1 のとき）。
// 会話を経ずに名簿へ入れた詳細未生成の NPC は、戦闘で空のスキル一覧を組んで落ちることがある。
// 立ち絵のパスが別の PC を指したままの NPC は、パーティ欄の顔が白い枠になる（ゲームは会話の始まりに直す）。
// 追加前にこれらを知らせる。
using System.Text.Json.Nodes;

namespace InstantaleSaveEditor
{
    internal static class PartyMembers
    {
        // 雇用した仲間の印。ゲームの翻訳表に無く、ワールドの言語によらず日本語のまま書かれる。
        public const string Accompany = "同行中";
        // 家族の印（家族は "同行中" を付けずにパーティへ入る）。訳されて入りうるため GameText.IsAnyOf で照合する。
        public const string Family = "家族";

        // 画面のパーティ欄の枠数（主人公を除く仲間の人数で、これを超えた分は表示されない）。
        public const int HudSlots = 3;

        // 追加前に知らせることの文言キー（無ければ空）。
        public static List<string> JoinWarnings(JsonObject npc)
        {
            var w = new List<string>();
            if (npc == null) return w;
            if (!DetailGenerated(npc)) w.Add("party.warn.notGenerated");
            if (HasForeignImage(npc)) w.Add("party.warn.foreignImage");
            return w;
        }

        // 詳細が作られているか。ゲームは level_of_detail が 1 のときだけ会話の直前に詳細を作り、2 で「埋まっている」とみなす。
        public static bool DetailGenerated(JsonObject npc)
        {
            if (J.Int(J.Obj(npc, "config"), "level_of_detail", 2) == 1) return false;
            return J.Obj(npc, "skills") is JsonObject sk && sk.Count > 0;
        }

        // image_src のどれかが、この PC に存在しない絶対パスを指しているか。
        public static bool HasForeignImage(JsonObject npc)
        {
            if (J.Obj(npc, "image_src") is not JsonObject src) return false;
            foreach (var kv in src)
                if (kv.Value is JsonValue v && v.TryGetValue<string>(out var p) && !string.IsNullOrEmpty(p)
                    && Path.IsPathRooted(p) && !File.Exists(p))
                    return true;
            return false;
        }

        // パーティに入れたときの印付け（家族でなければ "同行中" を足す）。
        // lang: ワールドの言語。relationship が無い NPC に初期値を作るときだけ使う。
        public static void MarkJoined(JsonObject npc, string lang = "ja")
        {
            var arr = PlayerRelationship(npc, create: true, lang);
            if (arr == null || arr.Any(n => GameText.IsAnyOf(Family, n?.ToString())) || Contains(arr, Accompany)) return;
            arr.Add(Accompany);
        }

        // パーティから外したとき・パーティにいない場所へ写したときの印外し（"同行中" だけを除く）。
        public static void MarkLeft(JsonObject npc)
        {
            var arr = PlayerRelationship(npc, create: false, "ja");
            if (arr == null) return;
            for (int i = arr.Count - 1; i >= 0; i--)
                if ((arr[i]?.ToString() ?? "") == Accompany) arr.RemoveAt(i);
        }

        // relationship.player.relationship の配列。配列でない（文字列等）ときは触らない。
        private static JsonArray PlayerRelationship(JsonObject npc, bool create, string lang)
        {
            if (npc == null) return null;
            if (npc["relationship"] is not JsonObject rel)
            {
                if (!create) return null;
                npc["relationship"] = rel = new JsonObject();
            }
            if (rel["player"] is not JsonObject p)
            {
                if (!create) return null;
                rel["player"] = p = (JsonObject)GameText.InitialRelationship(lang, affinityAsArray: false)["player"].DeepClone();
                ((JsonArray)p["relationship"]).Clear();
            }
            if (p["relationship"] is JsonArray a) return a;
            if (p["relationship"] == null && create) { var na = new JsonArray(); p["relationship"] = na; return na; }
            return null;
        }

        private static bool Contains(JsonArray arr, string s) => arr.Any(n => (n?.ToString() ?? "") == s);
    }
}
