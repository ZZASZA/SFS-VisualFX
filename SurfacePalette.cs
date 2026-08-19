using System;
using System.Collections.Generic;
using SFS.WorldBase;
using UnityEngine;

namespace SFSVisualFX
{
    /// <summary>地表材质分类：土壤 / 岩石 / 冰面（按原版星球 codeName 映射）。</summary>
    public enum SurfaceKind
    {
        Soil,
        Rock,
        Ice
    }

    /// <summary>
    /// 星球 → 地表材质。分类用于着陆烟尘与反推吹尘的"颜色 + 扩散形态"差异：
    /// 未知星球按有无大气回退（有大气=土壤，无大气=岩石）。
    /// </summary>
    public static class SurfacePalette
    {
        private static readonly HashSet<string> SoilPlanets = new HashSet<string>
        {
            "Earth", "Mars", "Ceres"
        };

        private static readonly HashSet<string> IcePlanets = new HashSet<string>
        {
            "Europa", "Enceladus", "Ganymede", "Mimas", "Tethys", "Dione", "Rhea", "Iapetus",
            "Miranda", "Ariel", "Umbriel", "Titania", "Oberon", "Puck", "Proteus", "Naiad", "Triton"
        };

        public static SurfaceKind GetKind(Planet planet)
        {
            if (planet == null)
            {
                return SurfaceKind.Rock;
            }
            string name = planet.codeName;
            if (!string.IsNullOrEmpty(name))
            {
                if (SoilPlanets.Contains(name)) return SurfaceKind.Soil;
                if (IcePlanets.Contains(name)) return SurfaceKind.Ice;
            }
            // 默认回退：有大气 → 土壤色；无大气 → 岩石色
            return planet.data != null && planet.data.hasAtmospherePhysics ? SurfaceKind.Soil : SurfaceKind.Rock;
        }

        public static Color GetColor(SurfaceKind kind)
        {
            switch (kind)
            {
                case SurfaceKind.Soil: return ModConfig.soilColor;
                case SurfaceKind.Ice: return ModConfig.iceColor;
                default: return ModConfig.rockColor;
            }
        }

        // ============ 行星表面主色表（着陆/反推烟色随星球表面颜色变化） ============

        /// <summary>SFS 原版星球 → 表面主色（参考游戏内星球地表颜色）。</summary>
        private static readonly Dictionary<string, Color> SurfaceColors = new Dictionary<string, Color>(StringComparer.OrdinalIgnoreCase)
        {
            { "Earth",     new Color(0.52f, 0.44f, 0.33f) }, // 土褐（中性土地色，避免烟色显脏黄/绿）
            { "Moon",      new Color(0.55f, 0.55f, 0.58f) }, // 灰
            { "Mars",      new Color(0.62f, 0.38f, 0.25f) }, // 红褐
            { "Mercury",   new Color(0.50f, 0.47f, 0.44f) }, // 灰棕
            { "Venus",     new Color(0.75f, 0.62f, 0.38f) }, // 黄褐
            { "Jupiter",   new Color(0.80f, 0.68f, 0.52f) }, // 橙白带
            { "Saturn",    new Color(0.82f, 0.74f, 0.58f) }, // 淡黄
            { "Uranus",    new Color(0.70f, 0.82f, 0.85f) }, // 青
            { "Neptune",   new Color(0.45f, 0.55f, 0.85f) }, // 蓝
            { "Pluto",     new Color(0.75f, 0.70f, 0.62f) }, // 浅棕
            { "Europa",    new Color(0.90f, 0.92f, 0.95f) }, // 白冰
            { "Enceladus", new Color(0.93f, 0.95f, 0.97f) }, // 亮白冰
            { "Io",        new Color(0.85f, 0.75f, 0.30f) }, // 硫黄
            { "Ceres",     new Color(0.55f, 0.50f, 0.45f) }, // 灰棕
        };

        /// <summary>
        /// 行星表面主色（着陆/反推烟色随星球表面颜色变化）：
        /// 优先按 codeName 查表（游戏内星球地表颜色），未命中回退材质分类色。
        /// </summary>
        public static Color GetSurfaceColor(Planet planet)
        {
            if (planet == null)
            {
                return GetColor(SurfaceKind.Rock);
            }
            if (!string.IsNullOrEmpty(planet.codeName) && SurfaceColors.TryGetValue(planet.codeName, out Color c))
            {
                return c;
            }
            return GetColor(GetKind(planet));
        }

        /// <summary>判定接触点是否在海面（真实地形低于水面 clamp 高度 → 该角度是海）。</summary>
        public static bool IsWaterAt(Planet planet, Double2 contactGlobal)
        {
            if (planet == null || planet.data == null || !planet.data.hasWater)
            {
                return false;
            }
            try
            {
                double terrain = planet.GetTerrainHeightAtAngle(contactGlobal.AngleRadians, false);
                double surface = planet.GetTerrainHeightAtAngle(contactGlobal.AngleRadians, true);
                return surface > terrain + 0.1;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 含海面判定的表面色：接触点在海面 → 纯白水雾（海面严禁泥土/表面色）；
        /// 陆地 → 星球表面主色。
        /// </summary>
        public static Color GetSurfaceColor(Planet planet, Double2 contactGlobal)
        {
            if (IsWaterAt(planet, contactGlobal))
            {
                return new Color(0.95f, 0.97f, 1f, 1f); // 纯白水雾
            }
            return GetSurfaceColor(planet);
        }

        /// <summary>
        /// 烟用颜色（表面色掺白灰，更真实——用户要求"带点白灰色"）：
        /// 表面色 55% + 白灰 45%；海面 → 纯白水雾。
        /// </summary>
        public static Color SmokeColor(Planet planet, Double2 contactGlobal)
        {
            Color c = GetSurfaceColor(planet, contactGlobal);
            return Color.Lerp(c, new Color(0.82f, 0.82f, 0.84f), 0.45f);
        }
    }
}
