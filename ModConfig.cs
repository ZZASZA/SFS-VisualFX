using System;
using System.Globalization;
using System.IO;
using UnityEngine;

namespace SFSVisualFX
{
    public enum Quality
    {
        Auto,
        Low,
        Medium,
        High
    }

    /// <summary>
    /// 配置：从模组文件夹下的 config.txt 读取（key = value）。
    /// 解析失败/缺失一律回退默认值，任何读文件异常都不影响游戏运行。
    /// </summary>
    public static class ModConfig
    {
        // —— 质量分级（性能约束核心）——
        public static Quality quality = Quality.Auto;

        // —— 全局浓度/规模旋钮（0.5=减半 … 2.0=加倍；同时作用于发射率与粒子尺寸）——
        public static float intensity = 1f;

        // —— 各特效开关 ——
        public static bool launchSmoke = true;   // 起飞烟雾（点火蒸汽云 + 持续尾烟）
        public static bool reverseDust = true;   // 着陆反推吹尘（无大气天体自动退化为喷流冲击）
        public static bool landingImpact = true; // 触地径向冲击烟尘

        // —— 粒子阻尼（越大烟尘越容易被大气"粘住"；公式与原版 WorldParticle 一致）——
        public static float smokeDrag = 6f;
        public static float steamDrag = 5f;
        public static float dustDrag = 7f;

        // —— 地表烟尘颜色（土壤 / 岩石 / 冰面）——
        public static Color soilColor = new Color(0.52f, 0.38f, 0.22f, 1f);
        public static Color rockColor = new Color(0.48f, 0.48f, 0.50f, 1f);
        public static Color iceColor = new Color(0.82f, 0.88f, 0.98f, 1f);

        public static void Load(string modFolder)
        {
            try
            {
                string path = Path.Combine(modFolder, "config.txt");
                if (!File.Exists(path))
                {
                    return;
                }

                foreach (string raw in File.ReadAllLines(path))
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith("#") || line.StartsWith("//"))
                    {
                        continue;
                    }

                    int eq = line.IndexOf('=');
                    if (eq < 0)
                    {
                        continue;
                    }

                    string key = line.Substring(0, eq).Trim().ToLowerInvariant();
                    string val = line.Substring(eq + 1).Trim();
                    switch (key)
                    {
                        case "quality":
                            string q = val.ToLowerInvariant();
                            if (q == "low") quality = Quality.Low;
                            else if (q == "medium") quality = Quality.Medium;
                            else if (q == "high") quality = Quality.High;
                            else quality = Quality.Auto;
                            break;
                        case "intensity":
                            intensity = Mathf.Clamp(ParseFloat(val, 1f), 0.2f, 3f);
                            break;
                        case "launch_smoke": launchSmoke = ParseBool(val, true); break;
                        case "reverse_dust": reverseDust = ParseBool(val, true); break;
                        case "landing_impact": landingImpact = ParseBool(val, true); break;
                        case "smoke_drag": smokeDrag = ParseFloat(val, smokeDrag); break;
                        case "steam_drag": steamDrag = ParseFloat(val, steamDrag); break;
                        case "dust_drag": dustDrag = ParseFloat(val, dustDrag); break;
                        case "soil_color": soilColor = ParseColor(val, soilColor); break;
                        case "rock_color": rockColor = ParseColor(val, rockColor); break;
                        case "ice_color": iceColor = ParseColor(val, iceColor); break;
                    }
                }
            }
            catch
            {
                // 任何读文件/解析错误：保持默认值
            }
        }

        private static bool ParseBool(string v, bool def)
        {
            v = v.ToLowerInvariant();
            if (v == "true" || v == "1" || v == "on") return true;
            if (v == "false" || v == "0" || v == "off") return false;
            return def;
        }

        private static float ParseFloat(string v, float def)
        {
            return float.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out float f) ? f : def;
        }

        private static Color ParseColor(string v, Color def)
        {
            string[] parts = v.Split(',');
            if (parts.Length != 3)
            {
                return def;
            }
            if (float.TryParse(parts[0].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float r) &&
                float.TryParse(parts[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float g) &&
                float.TryParse(parts[2].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float b))
            {
                return new Color(r, g, b, 1f);
            }
            return def;
        }
    }
}
