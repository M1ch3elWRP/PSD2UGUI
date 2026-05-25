using System;
using System.Collections.Generic;
using UnityEngine;

namespace PSDImporter
{
    // 单个绑定记录
    [Serializable]
    public class LayerBinding
    {
        public int layerID;           // PS 图层 ID (Key)
        public string layerName;      // 仅作调试显示用
        public GameObject bindTarget; // 场景实例引用（Prefab资产模式下可能为空）
        public string transformPath;  // 相对 Target Root 的路径，用于Prefab临时实例/引用失效恢复
    }

    [CreateAssetMenu(fileName = "PSDBindingData", menuName = "PSDTools/Binding Data Asset")]
    public class PSDBindingData : ScriptableObject
    {
        public List<LayerBinding> bindings = new List<LayerBinding>();

        // 快速查找字典 (运行时构建)
        private Dictionary<int, GameObject> lookupCache;
        private Dictionary<int, LayerBinding> bindingCache;

        public void BuildCache()
        {
            lookupCache = new Dictionary<int, GameObject>();
            bindingCache = new Dictionary<int, LayerBinding>();
            foreach (var b in bindings)
            {
                if (b == null)
                {
                    continue;
                }

                if (!bindingCache.ContainsKey(b.layerID))
                {
                    bindingCache.Add(b.layerID, b);
                }

                if (b.bindTarget != null && !lookupCache.ContainsKey(b.layerID))
                {
                    lookupCache.Add(b.layerID, b.bindTarget);
                }
            }
        }

        public GameObject GetBindTarget(int layerID)
        {
            if (lookupCache == null) BuildCache();
            return lookupCache.TryGetValue(layerID, out var go) ? go : null;
        }

        public GameObject GetBindTarget(int layerID, Transform root)
        {
            if (bindingCache == null) BuildCache();
            if (!bindingCache.TryGetValue(layerID, out LayerBinding binding) || binding == null)
            {
                return null;
            }

            if (binding.bindTarget != null && (root == null || binding.bindTarget.transform.IsChildOf(root)))
            {
                return binding.bindTarget;
            }

            if (root != null && !string.IsNullOrEmpty(binding.transformPath))
            {
                Transform found = FindRelativeTransform(root, binding.transformPath);
                if (found != null)
                {
                    return found.gameObject;
                }
            }

            if (root != null && string.IsNullOrEmpty(binding.transformPath) && binding.bindTarget != null)
            {
                return binding.bindTarget;
            }

            return null;
        }

        public void SaveBinding(int layerID, string name, GameObject target)
        {
            SaveBinding(layerID, name, target, null, true);
        }

        public void SaveBinding(int layerID, string name, GameObject target, Transform root, bool storeObjectReference = true)
        {
            var exist = bindings.Find(x => x.layerID == layerID);
            if (exist == null)
            {
                exist = new LayerBinding { layerID = layerID };
                bindings.Add(exist);
            }

            exist.bindTarget = storeObjectReference ? target : null;
            exist.layerName = name;
            exist.transformPath = target != null && root != null ? GetRelativeTransformPath(root, target.transform) : exist.transformPath;

            lookupCache = null;
            bindingCache = null;
        }

        private static Transform FindRelativeTransform(Transform root, string relativePath)
        {
            if (root == null)
            {
                return null;
            }

            if (string.IsNullOrEmpty(relativePath) || relativePath == ".")
            {
                return root;
            }

            return root.Find(relativePath);
        }

        private static string GetRelativeTransformPath(Transform root, Transform target)
        {
            if (root == null || target == null || !target.IsChildOf(root))
            {
                return string.Empty;
            }

            if (target == root)
            {
                return ".";
            }

            List<string> parts = new List<string>();
            Transform current = target;
            while (current != null && current != root)
            {
                parts.Add(current.name);
                current = current.parent;
            }

            parts.Reverse();
            return string.Join("/", parts);
        }
    }
}