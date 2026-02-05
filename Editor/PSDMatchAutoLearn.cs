using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.UI;

namespace PSDImporter
{
    public static class PSDMatchAutoLearn
    {
        private static bool warnedMissingDataset;
        private static bool warnedMissingModel;

        public static int TryGetDatasetScreenCount(PSDMatchMLConfig config)
        {
            if (config == null) return 0;
            string datasetPath = ResolvePath(config.datasetPath);
            if (string.IsNullOrEmpty(datasetPath)) return 0;
            if (!File.Exists(datasetPath)) return 0;
            try
            {
                string json = File.ReadAllText(datasetPath);
                var dataset = JsonUtility.FromJson<PSDMatchDataset>(json);
                return dataset?.screenIds?.Length ?? 0;
            }
            catch
            {
                return 0;
            }
        }

        public static PSDMatchModel TryLoadModel(PSDMatchMLConfig config)
        {
            if (config == null) return null;
            string modelPath = ResolvePath(config.modelPath);
            if (string.IsNullOrEmpty(modelPath)) return null;
            if (!File.Exists(modelPath)) return null;
            string json = File.ReadAllText(modelPath);
            return JsonUtility.FromJson<PSDMatchModel>(json);
        }

        public static void RecordAndMaybeTrain(string psdDataPath, PSDData psdData, Transform root, List<BindingPairViewModel> bindings, PSDImportConfig matchConfig, PSDMatchMLConfig mlConfig)
        {
            if (mlConfig == null || !mlConfig.autoLearnEnabled) return;
            if (psdData == null || root == null || bindings == null || bindings.Count == 0) return;

            string datasetPath = ResolvePath(mlConfig.datasetPath);
            if (string.IsNullOrEmpty(datasetPath))
            {
                WarnOnce(ref warnedMissingDataset, "[MatchML] AutoLearn disabled: dataset path is empty.");
                return;
            }

            string modelPath = ResolvePath(mlConfig.modelPath);
            if (string.IsNullOrEmpty(modelPath))
            {
                WarnOnce(ref warnedMissingModel, "[MatchML] AutoLearn disabled: model path is empty.");
                return;
            }

            var dataset = LoadDataset(datasetPath, matchConfig, mlConfig);
            if (dataset == null) return;
            if (dataset.featureCount != PSDMatchFeatureExtractor.FeatureCount)
            {
                Debug.LogWarning("[MatchML] Dataset featureCount mismatch. Please regenerate dataset.");
                return;
            }

            string screenId = NormalizeScreenId(psdDataPath, psdData);
            if (string.IsNullOrEmpty(screenId)) screenId = Guid.NewGuid().ToString("N");

            var screenSet = new HashSet<string>(dataset.screenIds ?? new string[0]);
            bool alreadyRecorded = screenSet.Contains(screenId);
            if (alreadyRecorded && !mlConfig.allowRepeatScreens)
            {
                Debug.Log($"[MatchML] Screen already recorded: {screenId}");
                return;
            }

            var nodes = CollectCandidateNodes(root, matchConfig);
            if (nodes.Count == 0)
            {
                Debug.LogWarning("[MatchML] No candidate nodes found. Skip auto learn.");
                return;
            }

            var newSamples = BuildSamples(psdData, root, bindings, nodes, dataset, matchConfig, mlConfig, screenId);
            if (newSamples.Count == 0)
            {
                Debug.LogWarning("[MatchML] No samples collected. Skip auto learn.");
                return;
            }

            var sampleList = new List<PSDMatchSample>(dataset.samples ?? new PSDMatchSample[0]);
            if (alreadyRecorded && mlConfig.allowRepeatScreens)
            {
                int removed = sampleList.RemoveAll(s => s != null && s.screenId == screenId);
                if (removed > 0)
                {
                    Debug.Log($"[MatchML] Replacing samples for screen: {screenId} (removed {removed})");
                }
            }
            sampleList.AddRange(newSamples);
            dataset.samples = sampleList.ToArray();

            if (!alreadyRecorded)
            {
                screenSet.Add(screenId);
                dataset.screenIds = new List<string>(screenSet).ToArray();
            }

            SaveDataset(datasetPath, dataset);

            var options = new PSDMatchTrainingOptions
            {
                epochs = Mathf.Max(1, mlConfig.epochsPerUpdate),
                learningRate = Mathf.Max(0.0001f, mlConfig.learningRate),
                l2 = Mathf.Max(0f, mlConfig.l2)
            };

            PSDMatchTrainingReport report;
            PSDMatchModel model = LoadModel(modelPath);
            if (model != null)
            {
                model = PSDMatchML.TrainIncremental(newSamples.ToArray(), dataset, options, model, out report);
            }
            else
            {
                model = PSDMatchML.Train(dataset, options, out report);
            }

            SaveModel(modelPath, model);
            Debug.Log($"[MatchML] Model updated. Samples={report.sampleCount}, Loss={report.loss:F4}, Acc={report.accuracy:P1}");
        }

        private static List<RectTransform> CollectCandidateNodes(Transform root, PSDImportConfig matchConfig)
        {
            var list = new List<RectTransform>();
            var all = root.GetComponentsInChildren<RectTransform>(true);
            for (int i = 0; i < all.Length; i++)
            {
                var node = all[i];
                if (node == root) continue;
                if (matchConfig != null && matchConfig.skipInactiveMatch && !node.gameObject.activeInHierarchy) continue;
                list.Add(node);
            }
            return list;
        }

        private static List<PSDMatchSample> BuildSamples(PSDData psdData, Transform root, List<BindingPairViewModel> bindings, List<RectTransform> nodes, PSDMatchDataset dataset, PSDImportConfig matchConfig, PSDMatchMLConfig mlConfig, string screenId)
        {
            var samples = new List<PSDMatchSample>();
            int negativePerPositive = mlConfig != null ? Mathf.Max(0, mlConfig.negativePerPositive) : 0;

            for (int i = 0; i < bindings.Count; i++)
            {
                var bind = bindings[i];
                if (bind == null || bind.psdItem.pngName == null) continue;
                if (mlConfig != null && mlConfig.requireConfirmed && !(bind.isConfirmed || bind.isIdMatched)) continue;
                if (bind.unityNode == null) continue;

                var positiveNode = bind.unityNode as RectTransform;
                if (positiveNode == null) continue;

                float[] posX = PSDMatchFeatureExtractor.ExtractFeatures(
                    bind.psdItem,
                    positiveNode,
                    root,
                    psdData.width,
                    psdData.height,
                    dataset.maxDistanceError,
                    dataset.maxSizeDiff,
                    dataset.maxDepthDiff);

                samples.Add(new PSDMatchSample
                {
                    x = posX,
                    y = 1,
                    item = bind.psdItem.pngName,
                    node = GetTransformPath(positiveNode),
                    screenId = screenId
                });

                if (negativePerPositive <= 0) continue;

                var negatives = new List<(float score, RectTransform node, float[] x)>();
                for (int n = 0; n < nodes.Count; n++)
                {
                    var node = nodes[n];
                    if (node == positiveNode) continue;
                    if (!IsTypeMatch(node, bind.psdItem.uiType)) continue;

                    float[] x = PSDMatchFeatureExtractor.ExtractFeatures(
                        bind.psdItem,
                        node,
                        root,
                        psdData.width,
                        psdData.height,
                        dataset.maxDistanceError,
                        dataset.maxSizeDiff,
                        dataset.maxDepthDiff);

                    float sim = (1f - x[0]) + (1f - x[1]) + x[2] + x[4];
                    negatives.Add((sim, node, x));
                }

                negatives.Sort((a, b) => b.score.CompareTo(a.score));
                int take = Mathf.Min(negativePerPositive, negatives.Count);
                for (int k = 0; k < take; k++)
                {
                    var neg = negatives[k];
                    samples.Add(new PSDMatchSample
                    {
                        x = neg.x,
                        y = 0,
                        item = bind.psdItem.pngName,
                        node = GetTransformPath(neg.node),
                        screenId = screenId
                    });
                }
            }

            return samples;
        }

        private static PSDMatchDataset LoadDataset(string path, PSDImportConfig matchConfig, PSDMatchMLConfig mlConfig)
        {
            if (File.Exists(path))
            {
                string json = File.ReadAllText(path);
                var dataset = JsonUtility.FromJson<PSDMatchDataset>(json);
                if (dataset != null)
                {
                    if (dataset.featureCount <= 0) dataset.featureCount = PSDMatchFeatureExtractor.FeatureCount;
                    if (dataset.maxDistanceError <= 0f) dataset.maxDistanceError = matchConfig != null ? matchConfig.maxDistanceError : dataset.maxDistanceError;
                    if (dataset.maxSizeDiff <= 0f) dataset.maxSizeDiff = matchConfig != null ? matchConfig.maxSizeDiff : dataset.maxSizeDiff;
                    if (dataset.maxDepthDiff <= 0) dataset.maxDepthDiff = mlConfig != null ? mlConfig.maxDepthDiff : dataset.maxDepthDiff;
                    if (dataset.screenIds == null) dataset.screenIds = new string[0];
                    if (dataset.samples == null) dataset.samples = new PSDMatchSample[0];
                    return dataset;
                }
            }

            return new PSDMatchDataset
            {
                featureCount = PSDMatchFeatureExtractor.FeatureCount,
                maxDistanceError = matchConfig != null ? matchConfig.maxDistanceError : 200f,
                maxSizeDiff = matchConfig != null ? matchConfig.maxSizeDiff : 100f,
                maxDepthDiff = mlConfig != null ? mlConfig.maxDepthDiff : 10,
                screenIds = new string[0],
                samples = new PSDMatchSample[0]
            };
        }

        private static void SaveDataset(string path, PSDMatchDataset dataset)
        {
            EnsureDirectory(path);
            string json = JsonUtility.ToJson(dataset, true);
            File.WriteAllText(path, json);
        }

        private static PSDMatchModel LoadModel(string path)
        {
            if (!File.Exists(path)) return null;
            string json = File.ReadAllText(path);
            return JsonUtility.FromJson<PSDMatchModel>(json);
        }

        private static void SaveModel(string path, PSDMatchModel model)
        {
            EnsureDirectory(path);
            string json = JsonUtility.ToJson(model, true);
            File.WriteAllText(path, json);
        }

        private static string ResolvePath(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            string normalized = path.Replace("\\", "/");
            if (Path.IsPathRooted(normalized)) return normalized;
            string root = Path.GetDirectoryName(Application.dataPath);
            if (string.IsNullOrEmpty(root)) return null;
            return Path.GetFullPath(Path.Combine(root, normalized));
        }

        private static void EnsureDirectory(string path)
        {
            string dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }
        }

        private static string NormalizeScreenId(string psdDataPath, PSDData psdData)
        {
            if (!string.IsNullOrEmpty(psdDataPath))
            {
                return psdDataPath.Replace("\\", "/");
            }
            if (!string.IsNullOrEmpty(psdData?.psdAssetsFolder))
            {
                return psdData.psdAssetsFolder.Replace("\\", "/");
            }
            return null;
        }

        private static bool IsTypeMatch(Transform node, string psdType)
        {
            if (psdType == "Button") return node.GetComponent<Button>() != null;
            if (psdType == "Text") return node.GetComponent<Text>() != null;
            if (psdType == "RawImage") return node.GetComponent<RawImage>() != null;
            if (psdType == "Image") return node.GetComponent<Image>() != null && node.GetComponent<Button>() == null;
            if (psdType == "Layout") return node.GetComponent<LayoutGroup>() != null;
            if (psdType == "Item") return node.GetComponent<LayoutGroup>() == null && node.GetComponentInParent<LayoutGroup>() != null;
            return false;
        }

        private static string GetTransformPath(Transform t)
        {
            if (t == null) return string.Empty;
            var parts = new List<string>();
            var current = t;
            while (current != null)
            {
                parts.Add(current.name);
                current = current.parent;
            }
            parts.Reverse();
            return string.Join("/", parts);
        }

        private static void WarnOnce(ref bool flag, string message)
        {
            if (flag) return;
            flag = true;
            Debug.LogWarning(message);
        }
    }
}
