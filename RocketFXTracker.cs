using System.Collections.Generic;
using SFS.World;
using SFS.WorldBase;
using UnityEngine;

namespace SFSVisualFX
{
    /// <summary>
    /// 挂在每枚火箭根物体上的状态跟踪器：随火箭销毁自动清理，零字典泄漏。
    /// 职责：
    ///   1. 记录每个推进器（EngineModule / BoosterModule）的"点火沿"，供点火蒸汽云触发；
    ///   2. 记录每个推进器的连续发射累加器（烟雾/吹尘/白热按速率折算到帧）；
    ///   3. 着陆判定：地表高度穿越 + 径向速度 → 触发触地冲击。
    /// 键用 object（模块实例），两种推进器模块共用。
    /// </summary>
    public sealed class RocketFXTracker : MonoBehaviour
    {
        private VFXManager manager;
        private bool initialized;
        private double prevGroundAlt;
        private float impactCooldown;
        private bool prevOnSurface;

        // 碰撞接触缓冲（原版 Rocket.IsOnSurface 同款检测，Rocket.cs:167-175）
        private readonly Collider2D[] contactBuffer = new Collider2D[5];
        private readonly ContactPoint2D[] contactPoints = new ContactPoint2D[5];

        /// <summary>当前是否与地面（Celestial Body 层）接触——"只在地面生效"的判定。</summary>
        public bool IsOnSurface { get; private set; }

        /// <summary>最近一帧的支撑面接触点（场景坐标）——地面特效中心（发射台平台表面）。</summary>
        public Vector3 LastSurfacePoint { get; private set; }

        private readonly Dictionary<object, bool> thrusterPrev = new Dictionary<object, bool>();
        private readonly Dictionary<object, float> smokeAcc = new Dictionary<object, float>();
        private readonly Dictionary<object, float> dustAcc = new Dictionary<object, float>();
        private readonly Dictionary<object, float> coreAcc = new Dictionary<object, float>();
        private readonly Dictionary<object, float> igniteTimer = new Dictionary<object, float>();

        public void Init(VFXManager m)
        {
            manager = m;
        }

        public bool GetPrev(object key)
        {
            return thrusterPrev.TryGetValue(key, out bool v) && v;
        }

        public void SetPrev(object key, bool v)
        {
            thrusterPrev[key] = v;
        }

        public float GetSmokeAcc(object key)
        {
            return smokeAcc.TryGetValue(key, out float v) ? v : 0f;
        }

        public void SetSmokeAcc(object key, float v)
        {
            smokeAcc[key] = v;
        }

        public float GetDustAcc(object key)
        {
            return dustAcc.TryGetValue(key, out float v) ? v : 0f;
        }

        public void SetDustAcc(object key, float v)
        {
            dustAcc[key] = v;
        }

        public float GetCoreAcc(object key)
        {
            return coreAcc.TryGetValue(key, out float v) ? v : 0f;
        }

        public void SetCoreAcc(object key, float v)
        {
            coreAcc[key] = v;
        }

        public float GetIgniteTimer(object key)
        {
            return igniteTimer.TryGetValue(key, out float v) ? v : 0f;
        }

        public void SetIgniteTimer(object key, float v)
        {
            igniteTimer[key] = v;
        }

        /// <summary>
        /// 每帧由 VFXManager 调用（仅实时物理时）。
        /// 着陆判定：**碰撞接触检测**（"Celestial Body" 层，原版 Rocket.IsOnSurface 同款，
        /// Rocket.cs:167-175）——rocket.location 是质心位置，高度穿越阈值在质心坐标系下
        /// 永远无法准确判定触地（火箭越高偏差越大），接触检测直接可靠。
        /// 接触沿（地面 → 触地）且径向速度向下超过阈值 → 冲击；
        /// 软着陆（<0.6m/s）也给出最小强度的小型扬尘，重着陆按速度线性增强。
        /// </summary>
        public void UpdateState(float realDt)
        {
            if (manager == null)
            {
                return;
            }
            Rocket rocket = GetComponent<Rocket>();
            if (rocket == null || rocket.location == null || rocket.rb2d == null)
            {
                return;
            }
            var loc = rocket.location.Value;
            Planet planet = loc.planet;
            if (planet == null || planet.data == null || !planet.data.hasTerrain)
            {
                return;
            }

            double groundAlt = loc.GetTerrainHeight(true);
            if (!initialized)
            {
                prevGroundAlt = groundAlt;
                initialized = true;
                return;
            }

            // 接触检测：与 "Celestial Body" 层（地形/发射台）发生接触即视为在地面上，
            // 并记录支撑面接触点（场景坐标）作为地面特效中心
            bool onSurface = false;
            int contactCount = rocket.rb2d.GetContacts(contactPoints);
            for (int i = 0; i < contactCount && i < contactPoints.Length; i++)
            {
                if (contactPoints[i].collider != null &&
                    contactPoints[i].collider.gameObject.layer == LayerMask.NameToLayer("Celestial Body"))
                {
                    onSurface = true;
                    LastSurfacePoint = contactPoints[i].point;
                    break;
                }
            }
            IsOnSurface = onSurface;

            impactCooldown -= realDt;
            if (impactCooldown <= 0f &&
                onSurface && !prevOnSurface &&
                Time.time > rocket.collisionImmunity)
            {
                double radialSpeed = loc.VerticalVelocity; // + 为远离星球中心
                if (radialSpeed < -0.6)
                {
                    float intensity = 0.15f + 0.85f * Mathf.Clamp01((float)(-radialSpeed / 8.0));
                    manager.TriggerImpact(rocket, planet, intensity);
                    impactCooldown = 1.2f;
                }
            }
            prevOnSurface = onSurface;
            prevGroundAlt = groundAlt;
        }
    }
}
