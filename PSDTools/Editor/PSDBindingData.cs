using System;
using System.Collections.Generic;
using UnityEngine;

namespace PSDImporter
{
    // 单个绑定记录
    [Serializable]
    public class LayerBinding
    {
        public int layerID;          // PS 图层 ID (Key)
        public string layerName;     // 仅作调试显示用
        public GameObject bindTarget; // 绑定的 Unity 物体
    }

    [CreateAssetMenu(fileName = "PSDBindingData", menuName = "PSDTools/Binding Data Asset")]
    public class PSDBindingData : ScriptableObject
    {
        public List<LayerBinding> bindings = new List<LayerBinding>();

        // 快速查找字典 (运行时构建)
        private Dictionary<int, GameObject> lookupCache;

        public void BuildCache()
        {
            lookupCache = new Dictionary<int, GameObject>();
            foreach (var b in bindings)
            {
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

        public void SaveBinding(int layerID, string name, GameObject target)
        {
            var exist = bindings.Find(x => x.layerID == layerID);
            if (exist != null)
            {
                exist.bindTarget = target;
                exist.layerName = name; // 更新名字以防改名
            }
            else
            {
                bindings.Add(new LayerBinding { layerID = layerID, layerName = name, bindTarget = target });
            }
        }
    }
}