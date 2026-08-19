using System;
using ModLoader;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace SFSVisualFX
{
    /// <summary>
    /// SFS Visual FX - launch smoke and landing impact visual effects.
    /// Install: place SFSVisualFX.dll in Mods/SFSVisualFX/ (folder name must match the dll).
    /// Pure visual layer: no physics, collision or save changes. Delete the folder to uninstall.
    /// </summary>
    public class MainMod : Mod
    {
        /// <summary>Mod folder path, used to load particle textures from Textures/.</summary>
        public static string ModFolderPath;

        public override string ModNameID => "sfsvisualfx";
        public override string DisplayName => "SFS Visual FX";
        public override string Author => "Hakino";
        public override string MinimumGameVersionNecessary => "1.5.11";
        public override string ModVersion => "1.0.0";
        public override string Description => "Launch smoke, surface-colored dust and landing shockwaves. Pure visual, zero gameplay changes.";

        public override void Load()
        {
            try
            {
                ModFolderPath = ModFolder;
                ModConfig.Load(ModFolder);
                SceneManager.sceneLoaded += OnSceneLoaded;
                EnsureManager();
                Debug.Log("[SFSVisualFX] Loaded v" + ModVersion);
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                Debug.LogError("[SFSVisualFX] Load failed: " + e.Message);
            }
        }

        private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            EnsureManager();
        }

        private void EnsureManager()
        {
            if (VFXManager.Instance != null)
            {
                return;
            }
            var go = new GameObject("SFSVisualFX");
            go.AddComponent<VFXManager>();
        }
    }
}
