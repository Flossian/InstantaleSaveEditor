// free 施設（facility_type=="free"）のプログラム。ゲーム側のバージョンアップで追加された、
// セーブのルート直下（world_data の中ではなく areas/npcs と同じ階層）の 2 キーを扱う:
//   free_facility_enabled  … 機能の有効フラグ
//   free_facility_programs … "free_{施設ID}" → プログラム（entry/prices/payouts/steps/...）
// プログラムは facility.config.program_id から参照され、進行フラグは facility.config.free_flags に入る。
// steps は label/goto で分岐するシナリオ命令列で、文字列中の {price.<キー>} は prices から展開される。
using System.Text.Json.Nodes;

namespace InstantaleSaveEditor
{
    internal static class FreeFacilityProgram
    {
        public const string RootKey = "free_facility_programs";
        public const string EnabledKey = "free_facility_enabled";
        public const string TypeName = "free";      // facility_type の値

        // プログラム辞書（無ければ null）。
        public static JsonObject Programs(JsonObject root) => J.Obj(root, RootKey);

        // プログラム辞書を用意する（無ければ作る）。有効フラグはキー自体が無いときだけ true を立てる
        //（明示的に false が入っている場合はゲーム側の設定を尊重して触らない）。
        public static JsonObject Ensure(JsonObject root)
        {
            if (root == null) return null;
            if (root[RootKey] is not JsonObject progs) root[RootKey] = progs = new JsonObject();
            if (root[EnabledKey] == null) root[EnabledKey] = true;
            return progs;
        }

        public static bool IsFree(JsonObject fac) => J.Str(fac, "facility_type") == TypeName;

        // 施設が参照しているプログラム ID（未設定なら空文字）。
        public static string ProgramIdOf(JsonObject fac) => J.Str(J.Obj(fac, "config"), "program_id");

        // 施設が参照しているプログラム本体（未設定・参照切れなら null）。
        public static JsonObject Of(JsonObject root, JsonObject fac)
        {
            string id = ProgramIdOf(fac);
            return id.Length > 0 ? J.Obj(Programs(root), id) : null;
        }

        // 施設 ID からプログラム ID を作る（ゲームと同じ "free_{施設ID}"）。既存と衝突したら連番を足す。
        public static string NewId(JsonObject programs, string facilityId)
        {
            string b = "free_" + (facilityId ?? "");
            if (programs?[b] == null) return b;
            for (int i = 2; i < 1000; i++)
                if (programs[$"{b}_{i}"] == null) return $"{b}_{i}";
            return b;
        }

        // 施設の config へ program_id を設定する（config が無ければ作る）。
        public static void SetProgramId(JsonObject fac, string programId)
        {
            if (fac == null) return;
            if (fac["config"] is not JsonObject cfg) fac["config"] = cfg = new JsonObject();
            cfg["program_id"] = programId;
        }

        // steps に置ける命令型（ゲームの scripts.free_facility.STEP_TYPES の 18 種）。追加時の骨格生成と一覧表示に使う。
        // call_phase はゲームの生成仕様（_DSL_SPEC）に載っておらず、形が確かめられていない。
        public static readonly string[] StepTypes =
        {
            "label", "text", "choice", "input", "goto", "if", "var_set", "random", "calc", "flag_set", "flag_get",
            "effect", "llm", "memory", "history_clear", "elapse", "call_phase", "end",
        };

        // effect の name（ゲームの効果 9 種）。args の形が name ごとに違うため骨格ごと切り替える。
        // 追加の一覧では "effect:<name>" として個別に並べる。
        public static readonly string[] EffectNames =
        {
            "gold_add", "wait", "heal", "exp_add", "status_add", "item_add",
            "show_character_image", "remove_character_image", "play_sound",
        };

        // 追加の一覧に並べる項目（effect は name ごとに展開）。Skeleton にそのまま渡せる。
        public static IEnumerable<string> PickerTypes()
        {
            foreach (var t in StepTypes)
            {
                if (t != "effect") { yield return t; continue; }
                foreach (var e in EffectNames) yield return "effect:" + e;
            }
        }

        // 指定型の 1 命令の骨格。値は空/既定のみで、詳細は JSON 編集で詰める前提。
        // type は StepTypes の型か、PickerTypes の "effect:<name>"。
        public static JsonObject Skeleton(string type) => type switch
        {
            _ when type != null && type.StartsWith("effect:") => EffectSkeleton(type.Substring(7)),
            "label" => new JsonObject { ["type"] = "label", ["name"] = "" },
            "text" => new JsonObject { ["type"] = "text", ["value"] = "" },
            "goto" => new JsonObject { ["type"] = "goto", ["target"] = "" },
            // 自由入力。入力を store へ入れて goto へ進む。options は入力欄と並べる抜け道のボタン。
            "input" => new JsonObject
            {
                ["type"] = "input", ["store"] = "", ["goto"] = "", ["append_history"] = false,
                ["options"] = new JsonArray(),
            },
            "var_set" => new JsonObject { ["type"] = "var_set", ["key"] = "", ["value"] = 0 },
            // op は + - * // %。left / right は整数か var 名。
            "calc" => new JsonObject { ["type"] = "calc", ["op"] = "+", ["left"] = "", ["right"] = 0, ["store"] = "" },
            "flag_get" => new JsonObject { ["type"] = "flag_get", ["key"] = "", ["store"] = "" },
            // 訓練・長逗留の概念にだけ使う（1経路1回、units は 1 か 2）。
            "elapse" => new JsonObject { ["type"] = "elapse", ["units"] = 1 },
            "call_phase" => new JsonObject { ["type"] = "call_phase" },
            "choice" => new JsonObject
            {
                ["type"] = "choice",
                ["options"] = new JsonArray { new JsonObject { ["label"] = "", ["goto"] = "" } },
            },
            "if" => new JsonObject
            {
                ["type"] = "if",
                ["cond"] = new JsonObject { ["source"] = "flag", ["key"] = "", ["op"] = "==", ["value"] = 1 },
                ["then"] = "",
                ["else"] = "",
            },
            "flag_set" => new JsonObject { ["type"] = "flag_set", ["key"] = "", ["value"] = 1 },
            "random" => new JsonObject { ["type"] = "random", ["min"] = 1, ["max"] = 2, ["store"] = "" },
            "effect" => EffectSkeleton("gold_add"),
            "llm" => new JsonObject
            {
                ["type"] = "llm",
                ["context"] = new JsonArray { "facility", "player" },
                ["prompt"] = "",
                ["use_history"] = false,
                ["output"] = new JsonObject { ["mode"] = "text" },
                ["store"] = "",
            },
            "memory" => new JsonObject { ["type"] = "memory", ["mode"] = "summary", ["tag"] = "" },
            "history_clear" => new JsonObject { ["type"] = "history_clear" },
            "end" => new JsonObject { ["type"] = "end", ["farewell"] = "" },
            _ => new JsonObject { ["type"] = type ?? "" },
        };

        // effect 1 命令の骨格。args の形は実セーブとゲームの生成仕様に合わせる。
        // show_character_image / remove_character_image / play_sound は args の形が確かめられていないため空にする。
        public static JsonObject EffectSkeleton(string name)
        {
            JsonObject args = name switch
            {
                "gold_add" => new JsonObject { ["amount"] = 0 },   // 通常は "-{price.X}"（支払い）/ "{payout.X}"（受け取り）
                "wait" => new JsonObject { ["seconds"] = 0.5 },     // 0〜3 秒
                "heal" => new JsonObject { ["scale"] = 0.3 },       // 0.3 / 0.6 / 1.0
                "exp_add" => new JsonObject { ["scale"] = 0.25 },   // 0.25 / 0.5 / 1.0（1.0 で訓練およそ1年分）
                "status_add" => new JsonObject { ["name"] = "", ["description"] = "", ["duration"] = 3 },   // duration は戦闘のターン数 3 / 5 / 10
                "item_add" => new JsonObject
                {
                    ["item_name"] = "", ["item_description"] = "",
                    ["item_appearance"] = "",   // 絵の選択に使う英語の一文
                    ["value"] = "standard", ["rarity"] = "common",
                    ["item_type"] = "", ["sub_type"] = "",
                },
                _ => new JsonObject(),
            };
            return new JsonObject { ["type"] = "effect", ["name"] = name ?? "", ["args"] = args };
        }

        // プログラム 1 件の骨格（メニュー 1 つで入退室できる最小構成）。
        public static JsonObject NewProgram(string name) => new()
        {
            ["entry"] = "start",
            ["prices"] = new JsonObject(),
            ["payouts"] = new JsonObject(),
            ["steps"] = new JsonArray
            {
                new JsonObject { ["type"] = "label", ["name"] = "start" },
                new JsonObject { ["type"] = "text", ["value"] = "" },
                new JsonObject { ["type"] = "label", ["name"] = "menu" },
                new JsonObject
                {
                    ["type"] = "choice",
                    ["options"] = new JsonArray { new JsonObject { ["label"] = I18n.T("freeprog.defaultLeave"), ["goto"] = "bye" } },
                },
                new JsonObject { ["type"] = "label", ["name"] = "bye" },
                new JsonObject { ["type"] = "end", ["farewell"] = "" },
            },
            ["version"] = 1,
            ["name"] = name ?? "",
            ["max_llm_calls"] = 12,
        };

        // steps 一覧に出す 1 行分の要約（"型: 詳細"）。型ごとに要点だけを拾う。
        public static string StepSummary(JsonNode step)
        {
            if (step is not JsonObject o) return J.Preview(step);
            string type = J.Str(o, "type", "?");
            string detail = type switch
            {
                "label" => J.Str(o, "name"),
                "goto" => "→ " + J.Str(o, "target"),
                "text" => Cut(J.Str(o, "value"), 48),
                "flag_set" or "var_set" => $"{J.Str(o, "key")} = {o["value"]}",
                "flag_get" => $"{J.Str(o, "key")} → {J.Str(o, "store")}",
                "calc" => $"{o["left"]} {J.Str(o, "op")} {o["right"]} → {J.Str(o, "store")}",
                "input" => $"→ {J.Str(o, "store")} / {J.Str(o, "goto")}",
                "elapse" => $"{o["units"]}",
                "random" => $"{o["min"]}-{o["max"]} → {J.Str(o, "store")}",
                "effect" => J.Str(o, "name") + ArgsHint(J.Obj(o, "args")),
                "llm" => (J.Str(o, "store") is string s && s.Length > 0 ? s + ": " : "") + Cut(J.Str(o, "prompt"), 40),
                "memory" => J.Str(o, "tag"),
                "end" => Cut(J.Str(o, "farewell"), 48),
                "choice" => I18n.T("freeprog.choiceCount", J.Arr(o, "options")?.Count ?? 0),
                "if" => CondHint(J.Obj(o, "cond")) + $" ? {J.Str(o, "then")} : {J.Str(o, "else")}",
                _ => "",
            };
            return detail.Length > 0 ? $"{type}: {detail}" : type;
        }

        // if の cond（{source,key,op,value}）を "source.key op value" の 1 行にする。
        private static string CondHint(JsonObject cond)
            => cond == null ? "" : $"{J.Str(cond, "source")}.{J.Str(cond, "key")} {J.Str(cond, "op")} {cond["value"]}";

        // effect の args から先頭 1 件だけを "(key=value)" で添える（一覧の情報密度を上げる用）。
        private static string ArgsHint(JsonObject args)
        {
            if (args == null || args.Count == 0) return "";
            var first = args.First();
            return $" ({first.Key}={Cut(first.Value?.ToString() ?? "", 20)})";
        }

        // 一覧表示用に改行を潰して長さを詰める。
        private static string Cut(string s, int n)
        {
            s = (s ?? "").Replace("\r", " ").Replace("\n", " ");
            return s.Length <= n ? s : s.Substring(0, n) + "…";
        }
    }
}
