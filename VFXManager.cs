using System;
using System.Collections.Generic;
using SFS.Parts.Modules;
using SFS.World;
using SFS.WorldBase;
using UnityEngine;

namespace SFSVisualFX
{
    /// <summary>
    /// 视觉增强主管理器（每飞行场景一个）——粒子架构（v3.7 基线）。
    /// 分层粒子（每台推进器同时驱动多层）：
    ///   Core  —— 白热羽流：大气窄锥白橙；真空宽锥蓝白膨胀；喷口光晕 + Mach 钻石；近地撞地扇形
    ///   Smoke —— 内层浓烟：白灰、快速膨胀、贴地反弹翻滚 + 涡旋
    ///   Haze  —— 外层淡烟幕：亮白灰、低 drag、长寿命
    ///   Steam —— 点火蒸汽云 / 上升烟柱 / 烟海 / 大烟柱
    ///   Dust  —— 地表尘雾（土壤/岩石/冰面分色）
    ///   Blast —— 无大气天体的喷流冲击碎屑
    ///   Shock —— 贴地冲击波环（点火/触地/反推持续弱环）
    /// 物理模拟与原版 WorldParticle 一致（重力/大气阻尼/时间加速/世界偏移）。
    /// 零侵入：不修改任何物理量、不添加碰撞体、不触碰原版判定。
    /// </summary>
    public sealed class VFXManager : MonoBehaviour
    {
        public static VFXManager Instance { get; private set; }

        // —— 性能分级（粒子预算 / 发射速率系数）——
        public int Budget { get; private set; }
        public float RateScale { get; private set; }

        public FXSystem Core { get; private set; }
        public FXSystem Smoke { get; private set; }
        public FXSystem Haze { get; private set; }
        public FXSystem Steam { get; private set; }
        public FXSystem Dust { get; private set; }
        public FXSystem Blast { get; private set; }
        public FXSystem Shock { get; private set; }

        public int TotalCount => Core.Count + Smoke.Count + Haze.Count + Steam.Count + Dust.Count + Blast.Count + Shock.Count;

        private bool subscribed;
        private bool layerSynced; // 烟层级是否已同步到火箭部件层
        private double simAcc;
        private Shader cachedShader;
        private Shader additiveShader;
        private Material originalBaseMaterial; // 克隆的原版粒子材质（shader 一定正确且透明）
        private Texture2D softTex;
        private Texture2D noiseTex;
        private Texture2D ringTex;

        // 特效发射距离门（场景局部单位）
        private const float EmitDistance = 4000f;

        /// <summary>
        /// 羽流风格（RealPlume 手法：按燃料类型区分尾焰颜色/形态）。
        /// 由引擎燃料 ResourceType.name 判定：液氢/甲烷=淡蓝白、煤油=亮黄白、
        /// 固体=橙黄、自燃推进剂=蓝紫、默认（SFS Fuel）=白橙。
        /// </summary>
        private enum PlumeStyle
        {
            Default,   // SFS 液体燃料：白橙
            LH2,       // 液氢/甲烷：淡蓝白、透明
            RP1,       // 煤油：亮黄白
            SRB,       // 固体燃料：橙黄
            Hypergolic // 自燃：蓝紫
        }

        private static PlumeStyle GetPlumeStyle(SFS.Parts.Modules.ResourceType rt)
        {
            if (rt == null)
            {
                return PlumeStyle.Default;
            }
            string n = rt.name;
            if (string.IsNullOrEmpty(n))
            {
                return PlumeStyle.Default;
            }
            string l = n.ToLowerInvariant();
            if (l.Contains("hydrogen") || l.Contains("methane") || l.Contains("lh2") || l.Contains("ch4"))
            {
                return PlumeStyle.LH2;
            }
            if (l.Contains("kerosene") || l.Contains("rp-1") || l.Contains("rp1"))
            {
                return PlumeStyle.RP1;
            }
            if (l.Contains("solid"))
            {
                return PlumeStyle.SRB;
            }
            if (l.Contains("hydrazine") || l.Contains("hypergolic") || l.Contains("nto") || l.Contains("mmh"))
            {
                return PlumeStyle.Hypergolic;
            }
            return PlumeStyle.Default;
        }

        /// <summary>按风格取大气/真空羽流配色（RealPlume 燃料差异）。</summary>
        private static void GetPlumeColors(PlumeStyle style, bool atmosphere, out Color c0, out Color c1)
        {
            switch (style)
            {
                case PlumeStyle.LH2:
                    c0 = atmosphere ? new Color(0.92f, 0.96f, 1f, 0.5f) : new Color(0.88f, 0.93f, 1f, 0.5f);
                    c1 = atmosphere ? new Color(0.72f, 0.84f, 0.98f, 0f) : new Color(0.7f, 0.8f, 0.96f, 0f);
                    break;
                case PlumeStyle.RP1:
                    c0 = atmosphere ? new Color(1f, 0.95f, 0.75f, 0.55f) : new Color(1f, 0.95f, 0.85f, 0.5f);
                    c1 = atmosphere ? new Color(1f, 0.72f, 0.35f, 0f) : new Color(0.9f, 0.8f, 0.65f, 0f);
                    break;
                case PlumeStyle.SRB:
                    c0 = atmosphere ? new Color(1f, 0.9f, 0.6f, 0.55f) : new Color(1f, 0.9f, 0.65f, 0.5f);
                    c1 = atmosphere ? new Color(1f, 0.6f, 0.25f, 0f) : new Color(0.95f, 0.68f, 0.35f, 0f);
                    break;
                case PlumeStyle.Hypergolic:
                    c0 = atmosphere ? new Color(0.85f, 0.88f, 1f, 0.5f) : new Color(0.8f, 0.85f, 1f, 0.5f);
                    c1 = atmosphere ? new Color(0.6f, 0.55f, 0.95f, 0f) : new Color(0.55f, 0.5f, 0.9f, 0f);
                    break;
                default:
                    // SFS 液体燃料：亮白核心（淡蓝白渐变）——避免 additive 叠加后出现刺眼的黄色
                    c0 = atmosphere ? new Color(1f, 0.99f, 0.96f, 0.55f) : new Color(0.95f, 0.98f, 1f, 0.55f);
                    c1 = atmosphere ? new Color(0.98f, 0.99f, 1f, 0f) : new Color(0.82f, 0.9f, 1f, 0f);
                    break;
            }
        }

        private void Awake()
        {
            Instance = this;
            ApplyQuality();
            if (!BuildTextures())
            {
                Debug.LogWarning("[SFSVisualFX] Material creation failed, mod disabled");
                enabled = false;
                return;
            }
            int b = Budget;
            // 分层预算（借鉴 RealPlume 多 emitter + Junon 冲击环）；
            // Core 用 additive 发光 + Stretch 条状拉伸（KSP 尾焰质感），Shock 用 additive 环纹
            // 粒子纹理：KSP RealPlume 下载的 PNG（Mods/SFSVisualFX/Textures/），缺失时回退程序化纹理
            Core = new FXSystem(this, "Core", Mathf.RoundToInt(b * 0.12f), 26f, LoadKspTexture("smoke1.png", softTex), true, 0.7f);
            Smoke = new FXSystem(this, "Smoke", Mathf.RoundToInt(b * 0.28f), ModConfig.smokeDrag, LoadKspTexture("smoke1.png", noiseTex));
            Haze = new FXSystem(this, "Haze", Mathf.RoundToInt(b * 0.14f), 3f, LoadKspTexture("smoke3.png", noiseTex));
            Steam = new FXSystem(this, "Steam", Mathf.RoundToInt(b * 0.14f), ModConfig.steamDrag, LoadKspTexture("smoke2.png", softTex));
            Dust = new FXSystem(this, "Dust", Mathf.RoundToInt(b * 0.16f), ModConfig.dustDrag, LoadKspTexture("smoke4.png", noiseTex));
            Blast = new FXSystem(this, "Blast", Mathf.RoundToInt(b * 0.06f), 0f, softTex);
            Shock = new FXSystem(this, "Shock", Mathf.RoundToInt(b * 0.06f), 0f, LoadKspTexture("shock.png", ringTex), true);
            Debug.Log("[SFSVisualFX] Ready: budget=" + Budget + " rate=" + RateScale);
        }

        /// <summary>
        /// 从 ModFolder/Textures 加载 KSP RealPlume 粒子纹理（PNG），缺失/失败回退程序化纹理。
        /// </summary>
        private static Texture2D LoadKspTexture(string name, Texture2D fallback)
        {
            try
            {
                if (string.IsNullOrEmpty(MainMod.ModFolderPath)) return fallback;
                string path = System.IO.Path.Combine(MainMod.ModFolderPath, "Textures", name);
                if (!System.IO.File.Exists(path)) return fallback;
                byte[] bytes = System.IO.File.ReadAllBytes(path);
                var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                if (tex.LoadImage(bytes))
                {
                    tex.wrapMode = TextureWrapMode.Clamp;
                    tex.filterMode = FilterMode.Bilinear;
                    return tex;
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning("[SFSVisualFX] Texture load failed (" + name + "): " + e.Message);
            }
            return fallback;
        }

        private void OnDestroy()
        {
            Unsubscribe();
            if (softTex != null) Destroy(softTex);
            if (noiseTex != null) Destroy(noiseTex);
            if (ringTex != null) Destroy(ringTex);
            if (Instance == this) Instance = null;
        }

        private void OnDisable()
        {
            Unsubscribe();
        }

        // ================= 质量分级 =================

        private void ApplyQuality()
        {
            Quality q = ModConfig.quality;
            if (q == Quality.Auto)
            {
                q = (Application.platform == RuntimePlatform.Android || Application.platform == RuntimePlatform.IPhonePlayer)
                    ? Quality.Low
                    : Quality.High;
            }
            switch (q)
            {
                case Quality.Low: Budget = 300; RateScale = 0.5f; break;   // 移动端（规格：300）
                case Quality.Medium: Budget = 500; RateScale = 0.8f; break;
                default: Budget = 800; RateScale = 1.0f; break;            // PC（规格：800）
            }
        }

        // ================= 程序化纹理/材质（无 AssetBundle） =================

        private bool BuildTextures()
        {
            try
            {
                // 优先克隆原版 WorldParticle 粒子材质：
                // SFS 打包剥离了内置粒子 shader，Shader.Find("Sprites/Default") 会命中不透明 Sprite shader
                // → 粒子显示为正方形/不可见。原版引擎烟材质（SFS/Weird shader）一定透明正确。
                originalBaseMaterial = CloneOriginalMaterial();
                if (originalBaseMaterial != null)
                {
                    cachedShader = originalBaseMaterial.shader;
                }
                else
                {
                    cachedShader = Shader.Find("Legacy Shaders/Particles/Alpha Blended");
                    if (cachedShader == null) cachedShader = Shader.Find("Sprites/Default");
                    if (cachedShader == null) return false;
                }

                // additive 发光材质（尾焰/冲击波）：存在则用，不存在回退 alpha 混合
                additiveShader = Shader.Find("Legacy Shaders/Particles/Additive");
                if (additiveShader == null) additiveShader = Shader.Find("Particles/Additive");
                if (additiveShader == null) additiveShader = Shader.Find("Hidden/Internal-ParticleAdditive");

                // 软圆（径向平方衰减）——蒸汽/白热/碎屑
                softTex = new Texture2D(32, 32, TextureFormat.RGBA32, false);
                softTex.wrapMode = TextureWrapMode.Clamp;
                for (int y = 0; y < 32; y++)
                {
                    for (int x = 0; x < 32; x++)
                    {
                        float d = Mathf.Sqrt((x - 15.5f) * (x - 15.5f) + (y - 15.5f) * (y - 15.5f)) / 15.5f;
                        float a = Mathf.Clamp01(1f - d);
                        softTex.SetPixel(x, y, new Color(1f, 1f, 1f, a * a));
                    }
                }
                softTex.Apply();

                // 噪点软圆（3-octave 值噪声 × 径向衰减）——烟/尘的明暗质感
                noiseTex = new Texture2D(64, 64, TextureFormat.RGBA32, false);
                noiseTex.wrapMode = TextureWrapMode.Clamp;
                var rng = new System.Random(20260818);
                float[,] grid = new float[8, 8];
                for (int gy = 0; gy < 8; gy++)
                    for (int gx = 0; gx < 8; gx++)
                        grid[gx, gy] = (float)rng.NextDouble();
                for (int y = 0; y < 64; y++)
                {
                    for (int x = 0; x < 64; x++)
                    {
                        float n = 0f;
                        float amp = 0.62f, freq = 8f / 64f;
                        for (int oct = 0; oct < 3; oct++)
                        {
                            float u = x * freq, v = y * freq;
                            int x0 = (int)u % 8, y0 = (int)v % 8;
                            if (x0 < 0) x0 += 8; if (y0 < 0) y0 += 8;
                            int x1 = (x0 + 1) % 8, y1 = (y0 + 1) % 8;
                            float tx = u - Mathf.Floor(u), ty = v - Mathf.Floor(v);
                            tx = tx * tx * (3f - 2f * tx);
                            ty = ty * ty * (3f - 2f * ty);
                            float v00 = grid[x0, y0], v10 = grid[x1, y0], v01 = grid[x0, y1], v11 = grid[x1, y1];
                            float v0 = Mathf.Lerp(v00, v10, tx);
                            float v1 = Mathf.Lerp(v01, v11, tx);
                            n += Mathf.Lerp(v0, v1, ty) * amp;
                            amp *= 0.5f;
                            freq *= 2f;
                        }
                        n = Mathf.Clamp01(n);
                        float d = Mathf.Sqrt((x - 31.5f) * (x - 31.5f) + (y - 31.5f) * (y - 31.5f)) / 31.5f;
                        float a = Mathf.Clamp01(1f - d);
                        a = a * a * (0.55f + 0.45f * n);
                        noiseTex.SetPixel(x, y, new Color(1f, 1f, 1f, a));
                    }
                }
                noiseTex.Apply();

                // 环状纹理（冲击波环）：内软外硬的甜甜圈
                ringTex = new Texture2D(64, 64, TextureFormat.RGBA32, false);
                ringTex.wrapMode = TextureWrapMode.Clamp;
                for (int y = 0; y < 64; y++)
                {
                    for (int x = 0; x < 64; x++)
                    {
                        float d = Mathf.Sqrt((x - 31.5f) * (x - 31.5f) + (y - 31.5f) * (y - 31.5f)) / 31.5f;
                        float inner = Mathf.SmoothStep(0.35f, 0.55f, d);
                        float outer = 1f - Mathf.SmoothStep(0.78f, 0.95f, d);
                        float a = inner * outer;
                        ringTex.SetPixel(x, y, new Color(1f, 1f, 1f, a * a));
                    }
                }
                ringTex.Apply();
                return true;
            }
            catch (Exception e)
            {
                Debug.LogWarning("[SFSVisualFX] BuildTextures error: " + e);
                return false;
            }
        }

        /// <summary>克隆原版 WorldParticle 预制体的粒子材质（保证 shader 透明正确）。</summary>
        private static Material CloneOriginalMaterial()
        {
            try
            {
                WorldParticle[] templates = ResourcesLoader.GetFiles_Array<WorldParticle>("");
                if (templates != null)
                {
                    foreach (WorldParticle t in templates)
                    {
                        if (t == null || t.effect == null) continue;
                        ParticleSystemRenderer r = t.effect.GetComponent<ParticleSystemRenderer>();
                        if (r != null && r.sharedMaterial != null)
                        {
                            var m = new Material(r.sharedMaterial);
                            return m;
                        }
                    }
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning("[SFSVisualFX] Original material clone failed: " + e.Message);
            }
            return null;
        }

        /// <summary>按纹理创建材质（优先克隆原版材质；additive 发光材质存在则用，否则回退 alpha 混合）。</summary>
        public Material MakeMaterial(Texture2D texture, bool additive = false)
        {
            Material mat;
            if (originalBaseMaterial != null)
            {
                mat = new Material(originalBaseMaterial);
            }
            else
            {
                Shader sh = (additive && additiveShader != null) ? additiveShader : cachedShader;
                mat = new Material(sh);
            }
            mat.mainTexture = texture;
            // 粒子 shader 的 tint 属性名兼容（Legacy Particles 用 _TintColor，其他用 _Color）
            if (mat.HasProperty("_TintColor")) mat.SetColor("_TintColor", Color.white);
            else if (mat.HasProperty("_Color")) mat.SetColor("_Color", Color.white);
            return mat;
        }

        // ================= 世界偏移订阅（防高速/时间加速漂移） =================

        private void EnsureSubscribed()
        {
            if (subscribed) return;
            WorldView wv = WorldView.main;
            if (wv == null) return;
            wv.onPositionOffset += OnPositionOffset;
            wv.onVelocityOffset += OnVelocityOffset;
            subscribed = true;
        }

        private void Unsubscribe()
        {
            if (!subscribed) return;
            WorldView wv = WorldView.main;
            if (wv != null)
            {
                wv.onPositionOffset -= OnPositionOffset;
                wv.onVelocityOffset -= OnVelocityOffset;
            }
            subscribed = false;
        }

        private void OnPositionOffset(Vector2 offset)
        {
            Core?.ShiftPosition(offset);
            Smoke?.ShiftPosition(offset);
            Haze?.ShiftPosition(offset);
            Steam?.ShiftPosition(offset);
            Dust?.ShiftPosition(offset);
            Blast?.ShiftPosition(offset);
            Shock?.ShiftPosition(offset);
        }

        private void OnVelocityOffset(Vector2 offset)
        {
            Core?.ShiftVelocity(offset);
            Smoke?.ShiftVelocity(offset);
            Haze?.ShiftVelocity(offset);
            Steam?.ShiftVelocity(offset);
            Dust?.ShiftVelocity(offset);
            Blast?.ShiftVelocity(offset);
            Shock?.ShiftVelocity(offset);
        }

        // ================= 主循环 =================

        private void Update()
        {
            if (GameManager.main == null || WorldView.main == null)
            {
                if (TotalCount > 0)
                {
                    ClearAll();
                }
                return; // 非飞行场景
            }

            EnsureSubscribed();

            bool realtime = WorldTime.main.realtimePhysics.Value;

            // —— 模拟（固定步长累加器）——
            double rawDt = realtime
                ? Time.fixedDeltaTime
                : Time.deltaTime * WorldTime.main.timewarpSpeed;
            if (rawDt > 0.5) rawDt = 0.5; // 卡顿后防大跳
            simAcc += rawDt;

            Location viewLoc = WorldView.main.ViewLocation;
            Planet planet = viewLoc.planet;
            Double2 gOffset = WorldView.main.positionOffset.Value;
            Vector2 vOffset = WorldView.main.velocityOffset.Value.ToVector2;
            float density = planet != null ? (float)planet.GetAtmosphericDensity(viewLoc.Height) : 0f;

            int maxSteps = realtime ? 4 : 1;
            int steps = 0;
            double stepSize = realtime ? Time.fixedDeltaTime : simAcc;
            while (simAcc >= stepSize && steps < maxSteps && stepSize > 1e-6)
            {
                float dt = (float)stepSize;
                Core.Simulate(dt, planet, gOffset, vOffset, density);
                Smoke.Simulate(dt, planet, gOffset, vOffset, density);
                Haze.Simulate(dt, planet, gOffset, vOffset, density);
                Steam.Simulate(dt, planet, gOffset, vOffset, density);
                Dust.Simulate(dt, planet, gOffset, vOffset, density);
                Blast.Simulate(dt, planet, gOffset, vOffset, density);
                Shock.Simulate(dt, planet, gOffset, vOffset, density);
                simAcc -= stepSize;
                steps++;
            }
            if (!realtime && simAcc > 0.5) simAcc = 0.0;
            if (simAcc < 1e-6) simAcc = 0.0;

            Core.Flush();
            Smoke.Flush();
            Haze.Flush();
            Steam.Flush();
            Dust.Flush();
            Blast.Flush();
            Shock.Flush();

            // —— 发射（时间加速下不发射；游戏中加速时推进器会被强制关闭）——
            if (realtime)
            {
                Emit();
            }
        }

        private void ClearAll()
        {
            Core?.Clear();
            Smoke?.Clear();
            Haze?.Clear();
            Steam?.Clear();
            Dust?.Clear();
            Blast?.Clear();
            Shock?.Clear();
        }

        // ================= 特效发射 =================

        private void Emit()
        {
            float distSqr = EmitDistance * EmitDistance;
            Vector2 viewPos = WorldView.ToLocalPosition(WorldView.main.ViewLocation.position);
            List<Rocket> rockets = GameManager.main.rockets;
            for (int r = rockets.Count - 1; r >= 0; r--)
            {
                Rocket rocket = rockets[r];
                if (rocket == null || rocket.rb2d == null || rocket.partHolder == null || !rocket.gameObject.activeInHierarchy)
                {
                    continue;
                }
                // 一次性：烟设到最高的 SortingLayer + 大 sortingOrder（确保盖过火箭部件）
                if (!layerSynced)
                {
                    layerSynced = true;
                    string layer = "Default";
                    var layers = UnityEngine.SortingLayer.layers;
                    if (layers != null && layers.Length > 0)
                    {
                        layer = layers[layers.Length - 1].name; // 最高层
                    }
                    const int order = 30000; // 同层内最高排序（超过部件/火焰/其他特效）
                    Smoke.SetSortingLayer(layer, order);
                    Steam.SetSortingLayer(layer, order);
                    Dust.SetSortingLayer(layer, order);
                    Haze.SetSortingLayer(layer, order);
                    Blast.SetSortingLayer(layer, order);
                    Shock.SetSortingLayer(layer, order);
                }
                Vector2 rPos = rocket.rb2d.position;
                if ((rPos - viewPos).sqrMagnitude > distSqr)
                {
                    continue;
                }

                var loc = rocket.location.Value;
                Planet planet = loc.planet;
                if (planet == null || planet.data == null || !planet.data.hasTerrain)
                {
                    continue;
                }

                double groundAlt = loc.GetTerrainHeight(true);
                double density = planet.GetAtmosphericDensity(loc.Height);
                double densityRef = planet.GetAtmosphericDensity(0.0);
                float densityFactor = densityRef > 1e-9
                    ? Mathf.Clamp01((float)(density / densityRef))
                    : 0f;

                RocketFXTracker tracker = EnsureTracker(rocket);
                if (tracker == null)
                {
                    continue;
                }
                tracker.UpdateState(Time.deltaTime);

                // 地面状态：接触检测（发射台/地形支撑面）+ 支撑点（地面特效中心）
                bool onGround = tracker.IsOnSurface;
                Vector3 surfacePoint = tracker.LastSurfacePoint;

                // 火箭场景速度：粒子出生时继承（ParticleModule.cs:32 同款），
                // 否则高速飞行时羽流/烟被"拉长"成难看的长尾巴
                Vector2 rocketVel = rocket.rb2d.linearVelocity;

                // 液体发动机
                EngineModule[] engines = rocket.partHolder.GetModules<EngineModule>();
                for (int e = 0; e < engines.Length; e++)
                {
                    EngineModule engine = engines[e];
                    if (engine == null)
                    {
                        continue;
                    }
                    // 燃料类型：FlowModule.sources（ResourceModule[]）→ resourceType
                    // （游戏 DLL 的 FlowModule 无 resourceType 字段，经 sources 间接获取）
                    SFS.Parts.Modules.ResourceType fuel = null;
                    if (engine.source != null && engine.source.sources != null && engine.source.sources.Length > 0)
                    {
                        fuel = engine.source.sources[0].resourceType;
                    }
                    // 喷流方向用 thrustPosition 精确定义（部件局部喷口坐标 → 世界）：
                    // 推力方向 = thrustPosition 方向（从喷口指向部件），喷流方向 = 其反方向。
                    // 不依赖 thrustNormal（部分发动机配置方向不同会导致粒子"反方向喷"）。
                    // 喷口世界位置 = TransformPoint(thrustPosition)（精确，不再估算偏移）
                    Vector2 nzLocal = engine.thrustPosition.Value;
                    Vector2 thrustDirWorld = nzLocal.sqrMagnitude > 0.0001f
                        ? engine.transform.TransformVector(nzLocal.normalized)
                        : engine.transform.TransformVector(engine.thrustNormal.Value);
                    Vector3 nozzle = engine.transform.TransformPoint(nzLocal);
                    ProcessThruster(tracker, planet,
                        engine,
                        engine.transform.position,
                        nozzle,
                        thrustDirWorld,
                        engine.thrust.Value,
                        engine.throttle_Out.Value,
                        groundAlt, densityFactor, rocketVel,
                        GetPlumeStyle(fuel), onGround, surfacePoint);
                }

                // 固体助推器
                BoosterModule[] boosters = rocket.partHolder.GetModules<BoosterModule>();
                for (int b = 0; b < boosters.Length; b++)
                {
                    BoosterModule booster = boosters[b];
                    if (booster == null)
                    {
                        continue;
                    }
                    // 喷流方向同液体发动机：thrustPosition 反方向优先
                    Vector2 nzLocal = booster.thrustPosition.Value;
                    Vector2 thrustDir = nzLocal.sqrMagnitude > 0.0001f
                        ? booster.transform.TransformVector(nzLocal.normalized)
                        : booster.transform.TransformVector(booster.thrustVector.Value);
                    Vector3 nozzle = booster.transform.TransformPoint(nzLocal);
                    ProcessThruster(tracker, planet,
                        booster,
                        booster.transform.position,
                        nozzle,
                        thrustDir,
                        booster.thrustVector.Value.magnitude,
                        booster.throttle_Out.Value,
                        groundAlt, densityFactor, rocketVel,
                        GetPlumeStyle(booster.resourceType), onGround, surfacePoint);
                }
            }
        }

        /// <summary>
        /// 统一推进器特效入口。
        /// thrustDir 为"推力方向"（背离喷口，Rocket.cs:239-254 用其计算火箭朝向），
        /// 喷流方向与喷口位置取其反方向（FlameModule.cs:62 火焰沿 local -y 延伸）。
        /// </summary>
        private void ProcessThruster(RocketFXTracker tracker, Planet planet, object key,
            Vector3 thrusterPos, Vector3 nozzle, Vector2 thrustDir, float thrust, float throttle,
            double groundAlt, float densityFactor, Vector2 rocketVel, PlumeStyle style,
            bool onGround, Vector3 surfacePoint)
        {
            if (thrustDir.sqrMagnitude < 0.001f)
            {
                thrustDir = Vector2.up;
            }
            Vector2 plumeDir = -thrustDir; // 喷流方向（指向喷口外）
            bool on = throttle > 0.01f;
            bool prev = tracker.GetPrev(key);
            float sizeF = Mathf.Clamp(thrust / 100f, 0.35f, 2.5f);
            float intensity = ModConfig.intensity;

            // 喷口位置：由调用方精确传入（TransformPoint(thrustPosition)）

            // 地面中心与反弹面：
            // 在地面上 → 用支撑面接触点（发射台/地形表面，火箭脚下）；
            // 离地低空（<80m）→ 用地形投影（反推吹尘/尾烟落地反弹用）
            float groundY = -1e7f;
            Vector3 groundLocal = Vector3.zero;
            if (onGround)
            {
                groundLocal = surfacePoint;
                groundY = groundLocal.y;
            }
            else if (groundAlt < 80.0)
            {
                Double2 thrusterGlobal = WorldView.ToGlobalPosition(thrusterPos);
                groundLocal = GroundLocal(planet, thrusterGlobal);
                groundY = groundLocal.y;
            }

            if (ModConfig.launchSmoke)
            {
                // 白热羽流（Core 层）已按用户要求移除——离地后从喷口喷出突兀（原为橙黄/亮白羽流）。
                // 保留：尾烟 Smoke / 蒸汽 Steam / 尘雾 Dust / 烟幕 Haze / 激波 Shock。
                // if (on) { EmitPlumeCore(...); }

                // 烟雾层：仅大气（真空自动关闭）
                if (on && densityFactor > 0.02f)
                {
                    // 点火瞬间：地面蒸汽云爆发（含冲击波环）+ 启动持续烟云计时器。
                    // **只在地面（接触支撑面）触发**——火箭离地后不再生成地面烟云
                    if (!prev && onGround)
                    {
                        EmitSteamBurst(planet, nozzle, plumeDir, groundLocal, groundY, sizeF, densityFactor, intensity, rocketVel);
                        tracker.SetIgniteTimer(key, 4f);
                    }
                    // 点火后 4 秒：发射台烟云持续翻涌（同样只在地面）
                    float ignite = tracker.GetIgniteTimer(key);
                    if (ignite > 0f)
                    {
                        ignite -= Time.deltaTime;
                        if (onGround)
                        {
                            Double2 nozzleGlobal = WorldView.ToGlobalPosition(nozzle);
                            Double2 radial = nozzleGlobal.normalized;
                            int puffs = UnityEngine.Random.Range(1, 3);
                            for (int pi = 0; pi < puffs; pi++)
                            {
                                float ang = UnityEngine.Random.Range(0f, Mathf.PI * 2f);
                                Vector2 dir = SurfaceDir(radial, ang);
                                Vector3 pos = groundLocal + (Vector3)(dir * UnityEngine.Random.Range(2f, 16f) * sizeF);
                                Vector3 vel = (Vector3)rocketVel +
                                              (Vector3)(dir * UnityEngine.Random.Range(0.8f, 2.6f) * sizeF) +
                                              Vector3.up * UnityEngine.Random.Range(0.6f, 2.4f);
                                Color c0 = SurfacePalette.SmokeColor(planet, WorldView.ToGlobalPosition(groundLocal)); c0.a = 0.5f * densityFactor;
                                Color c1 = SurfacePalette.SmokeColor(planet, WorldView.ToGlobalPosition(groundLocal)); c1.a = 0f;
                                Steam.Add(pos, vel, UnityEngine.Random.Range(6f, 10f),
                                    UnityEngine.Random.Range(4f, 8f) * sizeF * intensity,
                                    c0, c1, 1.8f, 2.6f, groundY, 0.3f, false,
                                    UnityEngine.Random.Range(1.2f, 2.4f), UnityEngine.Random.Range(0.8f, 1.6f));
                            }
                        }
                        tracker.SetIgniteTimer(key, ignite);
                    }
                    // 持续尾烟：双层（浓烟 + 烟幕）——仅近地面（<15m），离地后发动机不再产生烟（用户要求）
                    if (groundAlt < 15.0)
                    {
                        EmitThrusterPlume(planet, nozzle, plumeDir, throttle, sizeF, densityFactor, intensity, tracker, key, groundY, rocketVel);
                    }
                }
            }

            // 反推吹尘：近地面 + 有大气 → 尘雾；无大气 → 仅喷流冲击
            if (ModConfig.reverseDust && on && groundAlt < 30.0)
            {
                EmitReverseDust(planet, thrusterPos, throttle, sizeF, densityFactor, intensity, tracker, key, groundY);
            }

            tracker.SetPrev(key, on);
        }

        private RocketFXTracker EnsureTracker(Rocket rocket)
        {
            RocketFXTracker tracker = rocket.GetComponent<RocketFXTracker>();
            if (tracker == null)
            {
                tracker = rocket.gameObject.AddComponent<RocketFXTracker>();
                tracker.Init(this);
            }
            return tracker;
        }

        // ---------- 工具 ----------

        /// <summary>全局位置 → 该角度上的地表点（场景局部坐标）。</summary>
        private static Vector3 GroundLocal(Planet planet, Double2 globalPos)
        {
            double terrainH = planet.GetTerrainHeightAtAngle(globalPos.AngleRadians, true);
            Double2 groundGlobal = globalPos.normalized * (planet.Radius + terrainH);
            return WorldView.ToLocalPosition(groundGlobal);
        }

        /// <summary>地表切面方向：radial 为星球径向单位向量，angle 为切面内角度。</summary>
        private static Vector2 SurfaceDir(Double2 radial, float angle)
        {
            return radial.Rotate(Math.PI / 2.0).ToVector2 * Mathf.Cos(angle) + radial.ToVector2 * Mathf.Sin(angle);
        }

        /// <summary>2D 向量旋转（羽流锥形扩散用）。</summary>
        private static Vector2 RotateDir(Vector2 v, float rad)
        {
            float c = Mathf.Cos(rad);
            float s = Mathf.Sin(rad);
            return new Vector2(v.x * c - v.y * s, v.x * s + v.y * c);
        }

        private static Color DustColor0(Planet planet, Double2 contactGlobal)
        {
            // 着陆/反推烟色随星球表面主色变化；海面 → 纯白水雾
            Color c = SurfacePalette.SmokeColor(planet, contactGlobal); // 表面色掺白灰
            c.a = 0.85f;
            return c;
        }

        private static Color DustColor1(Planet planet, Double2 contactGlobal)
        {
            // 不白化：烟全程保持星球表面色（alpha 渐隐）；海面 → 纯白水雾
            Color c = SurfacePalette.SmokeColor(planet, contactGlobal);
            c.a = 0f;
            return c;
        }

        private static Color BlastColor0(Planet planet, Double2 contactGlobal)
        {
            Color c = SurfacePalette.GetSurfaceColor(planet, contactGlobal) * 0.6f;
            c.a = 0.85f;
            return c;
        }

        private static Color BlastColor1(Planet planet, Double2 contactGlobal)
        {
            Color c = SurfacePalette.GetSurfaceColor(planet, contactGlobal) * 0.4f;
            c.a = 0f;
            return c;
        }

        /// <summary>贴地冲击波环（Junon 标志性效果）：环状粒子沿地表快速径向扩散 + 大幅膨胀 + 不受重力下拉。</summary>
        private void EmitShockRing(Planet planet, Vector3 centerLocal, float speed, float scale, float alphaScale, Color c0, Color c1)
        {
            Double2 centerGlobal = WorldView.ToGlobalPosition(centerLocal);
            Double2 radial = centerGlobal.normalized;
            int n = Mathf.Max(8, Mathf.RoundToInt(16f * RateScale * ModConfig.intensity * scale));
            for (int i = 0; i < n; i++)
            {
                float ang = (Mathf.PI * 2f * i) / n + UnityEngine.Random.Range(-0.12f, 0.12f);
                Vector2 dir = SurfaceDir(radial, ang);
                Vector3 pos = centerLocal + (Vector3)(dir * UnityEngine.Random.Range(0.3f, 1.6f));
                Vector3 vel = (Vector3)(dir * speed * UnityEngine.Random.Range(0.8f, 1.2f)) + Vector3.up * 0.5f;
                Color a0 = c0; a0.a *= alphaScale;
                Color a1 = c1; a1.a *= alphaScale;
                // 增强：更大尺寸（1.6~3.0 × scale）、更长寿命（1.0~1.8）、更快生长
                Shock.Add(pos, vel, UnityEngine.Random.Range(1.0f, 1.8f), UnityEngine.Random.Range(1.6f, 3.0f) * scale,
                    a0, a1, 11f + 7f * scale, 1.8f, -1e7f, 0f, true);
            }
        }

        // ---------- 起飞烟雾（白热羽流 / 浓烟 / 烟幕） ----------


        /// <summary>持续尾烟：从喷口沿喷流方向发射 Smoke/Haze 两层（RealPlume 多 emitter 结构，仅大气）。</summary>
        private void EmitThrusterPlume(Planet planet, Vector3 nozzle, Vector2 plumeDir, float throttle,
            float sizeF, float densityFactor, float intensity, RocketFXTracker tracker, object key, float groundY, Vector2 rocketVel)
        {
            // 基率 45：起飞阶段滚滚浓烟；随高度/密度衰减，真空自动关闭
            float rate = 45f * throttle * sizeF * densityFactor * RateScale * intensity;
            float acc = tracker.GetSmokeAcc(key) + rate * Time.deltaTime;
            int spawned = 0;
            int maxPerFrame = Mathf.Max(6, Mathf.RoundToInt(38f * RateScale * intensity));

            float alphaFactor = 0.4f + 0.6f * densityFactor;

            while (acc >= 1f && spawned < maxPerFrame)
            {
                acc -= 1f;
                spawned++;
                Vector2 jitter = UnityEngine.Random.insideUnitCircle * 0.6f;
                Vector3 pos = nozzle + (Vector3)jitter;
                // 粒子速度 = 火箭速度 + 喷流速度 + 强上浮（烟云向上翻卷、包裹箭体，如真实发射）
                Vector3 vel = (Vector3)rocketVel +
                              (Vector3)(plumeDir * UnityEngine.Random.Range(3f, 7.5f) * throttle) +
                              Vector3.up * UnityEngine.Random.Range(1.5f, 4.5f) +
                              (Vector3)(jitter.normalized * UnityEngine.Random.Range(0f, 1.6f));

                // 尾烟颜色对齐星球表面（海面 → 纯白水雾）
                Color surf = SurfacePalette.SmokeColor(planet, WorldView.ToGlobalPosition(nozzle));
                Color s0 = surf; s0.a = 0.6f * alphaFactor;
                Color s1 = surf; s1.a = 0f;
                Smoke.Add(pos, vel, UnityEngine.Random.Range(2.4f, 4.4f), UnityEngine.Random.Range(2.5f, 5.0f) * sizeF,
                    s0, s1, 2.8f, 2.4f, groundY, 0.55f, false, UnityEngine.Random.Range(2.5f, 4.5f), UnityEngine.Random.Range(1.5f, 3f));

                // 外层淡烟幕：星球表面色淡化（每 5 颗 1 颗，占 20%）+ 慢速翻滚
                if (spawned % 5 == 0)
                {
                    Color h0 = surf; h0.a = 0.36f * alphaFactor;
                    Color h1 = surf; h1.a = 0f;
                    Vector3 hvel = (Vector3)rocketVel +
                                   (Vector3)(plumeDir * UnityEngine.Random.Range(1f, 3f)) +
                                   Vector3.up * UnityEngine.Random.Range(2f, 5f) +
                                   (Vector3)(jitter.normalized * UnityEngine.Random.Range(0f, 2.2f));
                    Haze.Add(pos, hvel, UnityEngine.Random.Range(5.5f, 10f), UnityEngine.Random.Range(6f, 10f) * sizeF,
                        h0, h1, 2.0f, 3.5f, groundY, 0.55f, false, UnityEngine.Random.Range(1.5f, 3f), UnityEngine.Random.Range(0.8f, 1.8f));
                }
            }
            if (acc > 3f) acc = 3f;
            tracker.SetSmokeAcc(key, acc);
        }

        /// <summary>点火瞬间：冲击波环 + 地面扩散蒸汽环 + 上升烟柱 + 烟幕 + 烟海 + 大烟柱（发射台爆发）。</summary>
        private void EmitSteamBurst(Planet planet, Vector3 nozzle, Vector2 plumeDir,
            Vector3 groundLocal, float groundY, float sizeF, float densityFactor, float intensity, Vector2 rocketVel)
        {
            // 径向基准：地面中心（支撑面接触点），蒸汽环/烟海围绕火箭脚下展开
            Double2 groundGlobal = WorldView.ToGlobalPosition(groundLocal);
            Double2 radial = groundGlobal.normalized;
            float alphaFactor = 0.4f + 0.6f * densityFactor;
            // 所有烟形态随星球表面颜色（海面 → 纯白水雾）
            Color surf = SurfacePalette.SmokeColor(planet, WorldView.ToGlobalPosition(groundLocal));

            // 0) 点火冲击波环（Junon：发射瞬间贴地快速扩散）
            Color ring0 = surf; ring0.a = 0.5f;
            Color ring1 = surf; ring1.a = 0f;
            EmitShockRing(planet, groundLocal, 13f * intensity, sizeF, 1f, ring0, ring1);

            // 1) 贴地蒸汽环：水平扩散 + 反弹翻滚（发射台水雾/蒸汽，量足才"吞没"发射台）
            int n = Mathf.RoundToInt(60f * RateScale * intensity * sizeF * densityFactor);
            for (int i = 0; i < n; i++)
            {
                float ang = UnityEngine.Random.Range(0f, Mathf.PI * 2f);
                Vector2 dir = SurfaceDir(radial, ang);
                Vector3 pos = groundLocal + (Vector3)(dir * UnityEngine.Random.Range(0.3f, 3.2f) * sizeF);
                Vector3 vel = (Vector3)rocketVel +
                              (Vector3)(dir * UnityEngine.Random.Range(3f, 9f) * sizeF) +
                              Vector3.up * UnityEngine.Random.Range(0.6f, 2.6f);
                Color c0 = surf; c0.a = 0.6f * alphaFactor;
                Color c1 = surf; c1.a = 0f;
                Steam.Add(pos, vel, UnityEngine.Random.Range(7f, 12f), UnityEngine.Random.Range(4f, 9f) * sizeF * intensity,
                    c0, c1, 1.8f, 2.8f, groundY, 0.3f, false, UnityEngine.Random.Range(1.5f, 3f), UnityEngine.Random.Range(1f, 2f));
            }

            // 2) 上升蒸汽柱（点火瞬间的卷起烟柱，从喷口下方涌出）
            int m = Mathf.Max(4, Mathf.RoundToInt(22f * RateScale * intensity * sizeF));
            for (int j = 0; j < m; j++)
            {
                Vector3 pos = nozzle + (Vector3)(plumeDir * UnityEngine.Random.Range(0.3f, 1.2f) * sizeF) +
                              (Vector3)((Vector2)UnityEngine.Random.insideUnitCircle * 0.6f * sizeF);
                Vector3 vel = (Vector3)rocketVel +
                              Vector3.up * UnityEngine.Random.Range(3f, 8f) * sizeF +
                              (Vector3)SurfaceDir(radial, UnityEngine.Random.Range(0f, Mathf.PI * 2f)) * UnityEngine.Random.Range(0.4f, 1.8f);
                Color c0 = surf; c0.a = 0.55f * alphaFactor;
                Color c1 = surf; c1.a = 0f;
                Steam.Add(pos, vel, UnityEngine.Random.Range(3.5f, 7f), UnityEngine.Random.Range(2.5f, 5f) * sizeF * intensity,
                    c0, c1, 2.2f, 2.4f, groundY, 0.3f);
            }

            // 3) 外围淡烟幕（点火爆发的大范围背景烟，包围发射台）
            int k = Mathf.Max(3, Mathf.RoundToInt(15f * RateScale * intensity * sizeF));
            for (int q = 0; q < k; q++)
            {
                float ang = UnityEngine.Random.Range(0f, Mathf.PI * 2f);
                Vector2 dir = SurfaceDir(radial, ang);
                Vector3 pos = groundLocal + (Vector3)(dir * UnityEngine.Random.Range(2f, 13f) * sizeF);
                Vector3 vel = (Vector3)rocketVel +
                              (Vector3)(dir * UnityEngine.Random.Range(1.5f, 4f)) +
                              Vector3.up * UnityEngine.Random.Range(2f, 5f);
                Color c0 = surf; c0.a = 0.3f * alphaFactor;
                Color c1 = surf; c1.a = 0f;
                Haze.Add(pos, vel, UnityEngine.Random.Range(7f, 12f), UnityEngine.Random.Range(10f, 20f) * sizeF * intensity,
                    c0, c1, 2.0f, 3.0f, groundY, 0.55f, false, UnityEngine.Random.Range(1.2f, 2.4f), UnityEngine.Random.Range(0.6f, 1.2f));
            }

            // 4) 发射台烟海（Junon 标志性效果）：大范围低空烟云铺开，吞没整个发射台
            int sea = Mathf.Max(10, Mathf.RoundToInt(30f * RateScale * intensity * sizeF));
            for (int s = 0; s < sea; s++)
            {
                float ang = UnityEngine.Random.Range(0f, Mathf.PI * 2f);
                Vector2 dir = SurfaceDir(radial, ang);
                Vector3 pos = groundLocal + (Vector3)(dir * UnityEngine.Random.Range(8f, 40f) * sizeF);
                Vector3 vel = (Vector3)rocketVel +
                              (Vector3)(dir * UnityEngine.Random.Range(0.5f, 2.2f)) +
                              Vector3.up * UnityEngine.Random.Range(0.5f, 2.2f);
                Color c0 = surf; c0.a = 0.4f * alphaFactor;
                Color c1 = surf; c1.a = 0f;
                Haze.Add(pos, vel, UnityEngine.Random.Range(10f, 16f), UnityEngine.Random.Range(14f, 28f) * sizeF * intensity,
                    c0, c1, 1.6f, 2.6f, groundY, 0.5f, false, UnityEngine.Random.Range(1f, 2.2f), UnityEngine.Random.Range(0.5f, 1f));
            }

            // 5) 大烟柱（Junon 标志性效果）：垂直预生成连续烟柱，拔地而起覆盖箭体
            int col = Mathf.Max(6, Mathf.RoundToInt(12f * RateScale * intensity * sizeF));
            for (int ci = 0; ci < col; ci++)
            {
                float h = UnityEngine.Random.Range(3f, 22f) * sizeF;
                Vector3 pos = groundLocal + Vector3.up * h +
                              (Vector3)((Vector2)UnityEngine.Random.insideUnitCircle * UnityEngine.Random.Range(0f, 3.5f) * sizeF);
                Vector3 vel = (Vector3)rocketVel +
                              Vector3.up * UnityEngine.Random.Range(2f, 5.5f) +
                              (Vector3)((Vector2)UnityEngine.Random.insideUnitCircle * UnityEngine.Random.Range(0f, 1.5f));
                Color c0 = surf; c0.a = 0.55f * alphaFactor;
                Color c1 = surf; c1.a = 0f;
                Smoke.Add(pos, vel, UnityEngine.Random.Range(5f, 9f), UnityEngine.Random.Range(3.5f, 7f) * sizeF * intensity,
                    c0, c1, 2.2f, 2.6f, -1e7f, 0.4f, false, UnityEngine.Random.Range(2f, 4f), UnityEngine.Random.Range(1f, 2f));
            }
        }

        // ---------- 着陆反推吹尘 ----------

        /// <summary>有大气：反推在地表吹起浓尘（贴地翻滚 + 大尘团）；无大气：仅喷流冲击碎屑。</summary>
        private void EmitReverseDust(Planet planet, Vector3 thrusterPos, float throttle, float sizeF,
            float densityFactor, float intensity, RocketFXTracker tracker, object key, float groundY)
        {
            Double2 thrusterGlobal = WorldView.ToGlobalPosition(thrusterPos);
            Double2 radial = thrusterGlobal.normalized;
            Vector3 groundLocal = GroundLocal(planet, thrusterGlobal);
            bool atmosphere = densityFactor > 0.02f;
            Color d0 = DustColor0(planet, thrusterGlobal);
            Color d1 = DustColor1(planet, thrusterGlobal);
            Color b0 = BlastColor0(planet, thrusterGlobal);
            Color b1 = BlastColor1(planet, thrusterGlobal);

            // 反推持续弱冲击环（Junon：悬停反推时地面持续冲击）
            float ringChance = atmosphere ? 0.07f : 0.05f;
            if (UnityEngine.Random.value < ringChance * RateScale * intensity)
            {
                Color rc0 = SurfacePalette.GetSurfaceColor(planet); rc0.a = atmosphere ? 0.22f : 0.30f;
                EmitShockRing(planet, groundLocal, 6f, 0.5f, 0.6f, rc0, new Color(rc0.r, rc0.g, rc0.b, 0f));
            }

            float acc = tracker.GetDustAcc(key);
            float rate = atmosphere
                ? 36f * throttle * sizeF * RateScale * densityFactor * intensity
                : 12f * throttle * sizeF * RateScale * intensity;
            acc += rate * Time.deltaTime;
            int spawned = 0;
            int maxPerFrame = Mathf.Max(5, Mathf.RoundToInt(14f * RateScale * intensity));

            while (acc >= 1f && spawned < maxPerFrame)
            {
                acc -= 1f;
                spawned++;
                float ang = UnityEngine.Random.Range(0f, Mathf.PI * 2f);
                Vector2 dir = SurfaceDir(radial, ang);
                Vector3 pos = groundLocal + (Vector3)(dir * UnityEngine.Random.Range(0.2f, 2.0f) * sizeF);
                if (atmosphere)
                {
                    // 浓尘：贴地水平扩散为主（上浮减小——修正"往上飘"）
                    Vector3 vel = (Vector3)(dir * UnityEngine.Random.Range(2f, 6f) * sizeF) +
                                  Vector3.up * UnityEngine.Random.Range(0.2f, 0.9f) * sizeF;
                    Dust.Add(pos, vel, UnityEngine.Random.Range(1.5f, 3.5f), UnityEngine.Random.Range(1.6f, 3.5f) * sizeF,
                        d0, d1, 1.8f, 2.2f, groundY, 0.35f, false, UnityEngine.Random.Range(2f, 4f), UnityEngine.Random.Range(1.2f, 2.4f));
                    // 少量大尘团（烟幕感）
                    if (spawned % 7 == 0)
                    {
                        Vector3 hvel = (Vector3)(dir * UnityEngine.Random.Range(0.6f, 1.6f)) +
                                      Vector3.up * UnityEngine.Random.Range(1.5f, 3.5f);
                        Color h0 = d0; h0.a *= 0.5f;
                        Haze.Add(pos, hvel, UnityEngine.Random.Range(4f, 7f), UnityEngine.Random.Range(5f, 9f) * sizeF,
                            h0, d1, 1.8f, 2.6f, groundY, 0.4f, false, UnityEngine.Random.Range(1.5f, 2.5f), UnityEngine.Random.Range(0.8f, 1.6f));
                    }
                }
                else
                {
                    // 真空：仅喷流冲击（快速飞散、寿命短、颜色暗）
                    Vector3 vel = (Vector3)(dir * UnityEngine.Random.Range(6f, 15f) * sizeF) +
                                  Vector3.up * UnityEngine.Random.Range(0f, 2f) * sizeF;
                    Blast.Add(pos, vel, UnityEngine.Random.Range(0.4f, 1.1f), UnityEngine.Random.Range(0.3f, 0.65f) * sizeF,
                        b0, b1, 0.6f, 0.4f);
                }
            }
            if (acc > 2f) acc = 2f;
            tracker.SetDustAcc(key, acc);
        }

        // ---------- 触地冲击 ----------

        /// <summary>
        /// 触地瞬间：冲击波环 + 径向冲击烟尘环 + 扬尘云 + 尘幕。
        /// 有大气 → 浓尘翻滚；无大气 → 仅高速冲击碎屑。
        /// </summary>
        public void TriggerImpact(Rocket rocket, Planet planet, float impactIntensity)
        {
            if (!ModConfig.landingImpact)
            {
                return;
            }
            Double2 g = rocket.location.Value.position;
            Double2 radial = g.normalized;
            Vector3 groundLocal = GroundLocal(planet, g);
            float groundY = groundLocal.y;
            double density = planet.GetAtmosphericDensity(rocket.location.Value.Height);
            double densityRef = planet.GetAtmosphericDensity(0.0);
            bool atmosphere = densityRef > 1e-9 && density / densityRef > 0.02;
            float ix = ModConfig.intensity;
            Color d0 = DustColor0(planet, g);
            Color d1 = DustColor1(planet, g);
            Color b0 = BlastColor0(planet, g);
            Color b1 = BlastColor1(planet, g);

            if (atmosphere)
            {
                // 触地冲击波环（Junon：着陆瞬间贴地扩散的白色环，更快更大）
                Color tr0 = SurfacePalette.GetSurfaceColor(planet); tr0.a = 0.55f;
                Color tr1 = SurfacePalette.GetSurfaceColor(planet); tr1.a = 0f;
                EmitShockRing(planet, groundLocal, 14f * ix, 1.5f, 1f, tr0, tr1);

                // 径向冲击烟尘环（贴地水平扩散为主，几乎不上飘——修正"应该沿地面扩散却往上飘"）
                int ring = Mathf.RoundToInt(56f * RateScale * impactIntensity * ix);
                for (int i = 0; i < ring; i++)
                {
                    float ang = UnityEngine.Random.Range(0f, Mathf.PI * 2f);
                    Vector2 dir = SurfaceDir(radial, ang);
                    Vector3 pos = groundLocal + (Vector3)(dir * UnityEngine.Random.Range(0.4f, 3.2f));
                    Vector3 vel = (Vector3)(dir * UnityEngine.Random.Range(3f, 11f) * impactIntensity) +
                                  Vector3.up * UnityEngine.Random.Range(0f, 0.5f) * impactIntensity;
                    Dust.Add(pos, vel, UnityEngine.Random.Range(1.6f, 3.6f), UnityEngine.Random.Range(2.4f, 4.8f) * ix,
                        d0, d1, 2.0f, 2.2f, groundY, 0.35f, false, UnityEngine.Random.Range(2f, 3.5f), UnityEngine.Random.Range(1.5f, 2.5f));
                }
                // 扬尘云（向上卷起）
                int loft = Mathf.Max(2, ring / 2);
                for (int j = 0; j < loft; j++)
                {
                    Vector3 pos = groundLocal + (Vector3)SurfaceDir(radial, UnityEngine.Random.Range(0f, Mathf.PI * 2f)) * UnityEngine.Random.Range(0f, 1.8f);
                    Vector3 vel = Vector3.up * UnityEngine.Random.Range(2f, 5f) * impactIntensity +
                                  (Vector3)SurfaceDir(radial, UnityEngine.Random.Range(0f, Mathf.PI * 2f)) * UnityEngine.Random.Range(0f, 2.2f) * impactIntensity;
                    Dust.Add(pos, vel, UnityEngine.Random.Range(2.5f, 4.5f), UnityEngine.Random.Range(3f, 6f) * ix,
                        d0, d1, 2.2f, 2.4f, groundY, 0.35f, false, UnityEngine.Random.Range(1.8f, 3f), UnityEngine.Random.Range(1.2f, 2f));
                }
                // 外围尘幕（大范围贴地扩散，低上飘）
                int veil = Mathf.Max(2, ring / 3);
                for (int v = 0; v < veil; v++)
                {
                    float ang = UnityEngine.Random.Range(0f, Mathf.PI * 2f);
                    Vector2 dir = SurfaceDir(radial, ang);
                    Vector3 pos = groundLocal + (Vector3)(dir * UnityEngine.Random.Range(1f, 5f));
                    Vector3 vel = (Vector3)(dir * UnityEngine.Random.Range(0.6f, 2.2f)) + Vector3.up * UnityEngine.Random.Range(0.3f, 1.2f);
                    Color h0 = d0; h0.a *= 0.45f;
                    Haze.Add(pos, vel, UnityEngine.Random.Range(5f, 8f), UnityEngine.Random.Range(6f, 11f),
                        h0, d1, 2.0f, 2.8f, groundY, 0.4f, false, UnityEngine.Random.Range(1.2f, 2.2f), UnityEngine.Random.Range(0.8f, 1.5f));
                }
            }
            else
            {
                // 真空触地：暗色小冲击环（喷流激波，无尘雾）
                EmitShockRing(planet, groundLocal, 8f * ix, 0.6f, 0.7f,
                    new Color(0.60f, 0.60f, 0.62f, 0.45f), new Color(0.55f, 0.55f, 0.58f, 0f));

                // 无大气天体：仅高速冲击碎屑
                int n = Mathf.RoundToInt(20f * RateScale * impactIntensity * ix);
                for (int i = 0; i < n; i++)
                {
                    float ang = UnityEngine.Random.Range(0f, Mathf.PI * 2f);
                    Vector2 dir = SurfaceDir(radial, ang);
                    Vector3 pos = groundLocal + (Vector3)(dir * UnityEngine.Random.Range(0.2f, 1.6f));
                    Vector3 vel = (Vector3)(dir * UnityEngine.Random.Range(8f, 18f) * impactIntensity) +
                                  Vector3.up * UnityEngine.Random.Range(0f, 1.5f) * impactIntensity;
                    Blast.Add(pos, vel, UnityEngine.Random.Range(0.4f, 1.0f), UnityEngine.Random.Range(0.35f, 0.7f),
                        b0, b1, 0.6f, 0.4f);
                }
            }
        }
    }
}
