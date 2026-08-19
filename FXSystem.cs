using SFS.World;
using SFS.WorldBase;
using UnityEngine;
using UnityEngine.Rendering;

namespace SFSVisualFX
{
    /// <summary>
    /// 单条程序化粒子流（借鉴 KSP RealPlume/SmokeScreen 手法）：
    /// - 纯代码创建（无 AssetBundle），可选软圆/噪点/环状纹理
    /// - 手动模拟：重力 + 大气阻尼 + 时间加速 + 世界偏移
    /// - 双阶段尺寸生长（先快后慢，近似 logGrowScale）+ 颜色随寿命渐变
    /// - 地面反弹（stickiness/贴地翻滚）：粒子记录发射时的地表 y，落地反弹一次并水平扩散
    /// - 涡旋扰动（wobble）：烟团翻滚而非直线飘散（Junon/KSP turbulence）
    /// - 每帧一次性 SetParticles 提交，无逐粒子 Emit
    /// </summary>
    public sealed class FXSystem
    {
        private struct P
        {
            public Vector3 pos;
            public Vector3 vel;
            public float life;      // 剩余寿命
            public float maxLife;   // 初始寿命
            public float size;      // 初始尺寸
            public float growFast;  // 第一阶段（前 35% 寿命）膨胀倍率
            public float growSlow;  // 第二阶段（35% 之后）膨胀倍率
            public Color color0;    // 出生色
            public Color color1;    // 消散色
            public float groundY;   // 地表局部 y；>= -1e6 时启用地面反弹（仅一次）
            public float bounceVel; // 反弹保留的竖向速度比例
            public bool noGravity;  // 冲击波环等贴地特效：不受行星引力（保持贴地扩散）
            public float wobbleAmp; // 涡旋扰动幅度（0=无）
            public float wobbleFreq;// 扰动频率（rad/s）
            public float wobblePhase;
            public Vector2 wobbleDir;
        }

        private readonly VFXManager owner;
        private readonly ParticleSystem ps;
        private readonly P[] pool;
        private readonly ParticleSystem.Particle[] flush;
        private readonly int cap;
        private readonly float drag;
        private readonly float zOffset; // 粒子 Z 偏移：负值 = 靠近相机（盖过 z=0 的部件）
        private int count;

        public int Count => count;
        public string Name { get; }

        public FXSystem(VFXManager owner, string name, int cap, float drag, Texture2D texture, bool additive = false, float stretchScale = 0f, float zOffset = 0f)
        {
            this.owner = owner;
            Name = name;
            this.cap = cap;
            this.drag = drag;
            this.zOffset = zOffset;
            pool = new P[cap];
            flush = new ParticleSystem.Particle[cap];

            // ===== 自建粒子系统（回退到 Z 轴修改前：MakeMaterial + sortingOrder，无 Z 偏移） =====
            var go = new GameObject("SFSVisualFX_" + name);
            go.transform.SetParent(owner.transform, false);
            ps = go.AddComponent<ParticleSystem>();
            var ren = ps.GetComponent<ParticleSystemRenderer>();
            if (stretchScale > 0f)
            {
                ren.renderMode = ParticleSystemRenderMode.Stretch;
                ren.velocityScale = stretchScale;
                ren.lengthScale = 1.2f;
            }
            else
            {
                ren.renderMode = ParticleSystemRenderMode.Billboard;
            }
            ren.material = owner.MakeMaterial(texture, additive);
            ren.sortingOrder = 100; // 初始值；运行时 SetSortingLayer 同步
            ren.shadowCastingMode = ShadowCastingMode.Off;
            ren.receiveShadows = false;

            // 重配粒子参数（手动驱动，不改 renderer 排序）
            ParticleSystem.MainModule main = ps.main;
            main.loop = false;
            main.playOnAwake = false;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.simulationSpeed = 0.001f; // 原生模拟几乎关闭，位置/速度完全由我们驱动
            main.maxParticles = cap;
            main.startLifetime = 1f;
            main.startSpeed = 0f;
            main.startSize = 1f;
            main.startColor = Color.white;
            main.gravityModifier = 0f;
            ParticleSystem.EmissionModule em = ps.emission;
            em.enabled = false;
            ParticleSystem.ShapeModule sh = ps.shape;
            sh.enabled = false;
            ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            ps.Play();
        }

        /// <summary>
        /// 运行时设置渲染层级：烟设到 SortingLayer 最高层 + 高 sortingOrder（Emit() 中调用）。
        /// </summary>
        public void SetSortingLayer(string layerName, int order)
        {
            var ren = ps.GetComponent<ParticleSystemRenderer>();
            if (ren == null) return;
            if (!string.IsNullOrEmpty(layerName)) ren.sortingLayerName = layerName;
            ren.sortingOrder = order;
        }

        /// <summary>
        /// 发射一个粒子（场景局部坐标与速度）。
        /// growFast/growSlow：两阶段尺寸膨胀；color0→color1 寿命渐变；
        /// groundY：地表局部 y（&gt;= -1e6 时启用反弹），bounceVel 为反弹竖向保留比例；
        /// noGravity：冲击波环等贴地特效；wobbleAmp/wobbleFreq：涡旋扰动（烟团翻滚）。
        /// 满预算返回 false（性能约束）。
        /// </summary>
        public bool Add(Vector3 pos, Vector3 vel, float life, float size,
            Color color0, Color color1,
            float growFast = 1.5f, float growSlow = 2.5f,
            float groundY = -1e7f, float bounceVel = 0.35f,
            bool noGravity = false, float wobbleAmp = 0f, float wobbleFreq = 0f)
        {
            if (count >= cap || owner.TotalCount >= owner.Budget)
            {
                return false;
            }
            pos.z = zOffset; // Z 轴：负值 = 靠近相机，烟盖过 z=0 的部件
            pool[count] = new P
            {
                pos = pos,
                vel = vel,
                life = life,
                maxLife = life,
                size = size,
                growFast = growFast,
                growSlow = growSlow,
                color0 = color0,
                color1 = color1,
                groundY = groundY,
                bounceVel = bounceVel,
                noGravity = noGravity,
                wobbleAmp = wobbleAmp,
                wobbleFreq = wobbleFreq,
                wobblePhase = UnityEngine.Random.Range(0f, Mathf.PI * 2f),
                wobbleDir = UnityEngine.Random.insideUnitCircle.normalized
            };
            count++;
            return true;
        }

        public void Clear()
        {
            count = 0;
            ps.Clear();
        }

        /// <summary>世界位置偏移（WorldView.onPositionOffset）。</summary>
        public void ShiftPosition(Vector2 offset)
        {
            Vector3 o = offset;
            for (int i = 0; i < count; i++)
            {
                pool[i].pos += o;
            }
        }

        /// <summary>世界速度偏移（WorldView.onVelocityOffset）。</summary>
        public void ShiftVelocity(Vector2 offset)
        {
            Vector3 o = offset;
            for (int i = 0; i < count; i++)
            {
                pool[i].vel += o;
            }
        }

        /// <summary>手动积分一步。planet 为当前视点星球；globalOffset/velOffset 为世界偏移；density 为视点高度大气密度。</summary>
        public void Simulate(float dt, Planet planet, Double2 globalOffset, Vector2 velOffset, float density)
        {
            if (count == 0)
            {
                return;
            }
            for (int i = 0; i < count; i++)
            {
                ref P p = ref pool[i];

                // 重力（行星引力场，与原版 WorldParticle 相同；冲击波环等 noGravity 粒子不受下拉）
                if (!p.noGravity && planet != null)
                {
                    Double2 gp = globalOffset + p.pos;
                    Vector2 g = (Vector2)planet.GetGravity(gp) * dt;
                    p.vel.x += g.x;
                    p.vel.y += g.y;
                }

                // 大气阻尼（与原版 WorldParticle 相同公式；真空 density=0 自动无阻尼）
                if (density > 0f && drag > 0f)
                {
                    Vector2 vAbs = (Vector2)p.vel + velOffset; // 绝对速度（含相机速度补偿）
                    float num = density * dt * drag;
                    float sqr = vAbs.sqrMagnitude;
                    if (sqr > 0.0001f)
                    {
                        Vector2 damp = vAbs.normalized * (sqr * num);
                        p.vel.x -= damp.x;
                        p.vel.y -= damp.y;
                    }
                }

                p.pos += p.vel * dt;

                // 涡旋扰动（烟团翻滚）：双正交正弦速度扰动，模拟湍流
                if (p.wobbleAmp > 0f)
                {
                    float age = p.maxLife - p.life;
                    float a = Mathf.Sin(age * p.wobbleFreq + p.wobblePhase) * p.wobbleAmp * dt;
                    float b = Mathf.Sin(age * p.wobbleFreq * 0.7f + p.wobblePhase * 1.7f) * p.wobbleAmp * 0.8f * dt;
                    p.vel.x += p.wobbleDir.x * a - p.wobbleDir.y * b;
                    p.vel.y += p.wobbleDir.y * a + p.wobbleDir.x * b;
                }

                // 地面反弹（stickiness/贴地翻滚，仅一次）：落地后水平扩散 + 少量上卷
                if (p.groundY > -1e6f && p.pos.y < p.groundY)
                {
                    p.pos.y = p.groundY;
                    p.vel.y = Mathf.Abs(p.vel.y) * p.bounceVel + 0.4f;
                    float ang = UnityEngine.Random.Range(0f, Mathf.PI * 2f);
                    p.vel.x += Mathf.Cos(ang) * UnityEngine.Random.Range(1.2f, 3.0f);
                    p.groundY = -1e7f; // 只反弹一次
                }

                p.life -= dt;
                if (p.life <= 0f)
                {
                    // swap-remove
                    pool[i] = pool[count - 1];
                    count--;
                    i--;
                }
            }
        }

        /// <summary>把粒子缓冲一次性提交给 ParticleSystem（每帧一次）。</summary>
        public void Flush()
        {
            for (int i = 0; i < count; i++)
            {
                ref P p = ref pool[i];
                float ageRatio = 1f - p.life / p.maxLife; // 0 出生 → 1 消亡
                ParticleSystem.Particle fp = flush[i];
                fp.position = p.pos;
                fp.velocity = p.vel;
                fp.remainingLifetime = p.life;
                fp.startLifetime = p.maxLife;

                // 双阶段生长：前 35% 快速膨胀（growFast），之后缓慢扩展（growSlow），近似 logGrow
                float fast = Mathf.Clamp01(ageRatio / 0.35f);
                float slow = Mathf.Clamp01((ageRatio - 0.35f) / 0.65f);
                fp.startSize = p.size * (1f + p.growFast * fast) * (1f + p.growSlow * slow * slow);

                // 颜色随寿命渐变 + 淡入淡出（出生 10% 淡入，尾部 40% 二次平滑淡出——边缘柔和不"硬切"）
                Color c = Color.Lerp(p.color0, p.color1, Mathf.Sqrt(ageRatio));
                float fadeIn = Mathf.Clamp01(ageRatio / 0.1f);
                float fadeOutRaw = Mathf.Clamp01(p.life / Mathf.Max(0.05f, p.maxLife * 0.4f));
                float fadeOut = fadeOutRaw * fadeOutRaw; // 二次曲线：尾部渐变到透明，消除硬边缘
                c.a *= Mathf.Min(fadeIn, fadeOut);
                fp.startColor = c;
                fp.rotation = 0f;
                flush[i] = fp;
            }
            ps.SetParticles(flush, count);
        }
    }
}
