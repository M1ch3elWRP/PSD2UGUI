using System;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace PSDImporter
{
    public static class PSDMatchScoringRegression
    {
        private const string FixturePath = "Editor/Fixtures/PSDMatchScoringRegression.fixture.json";
        private const float Tolerance = 0.0001f;

        [Serializable]
        private struct Vec2Data
        {
            public float x;
            public float y;

            public Vector2 ToVector2()
            {
                return new Vector2(x, y);
            }
        }

        [Serializable]
        private class RegressionCase
        {
            public string name;
            public bool isTypeMatch;
            public bool inLayout;
            public Vec2Data nodeCenter;
            public Vec2Data nodeSize;
            public Vec2Data nodeAnchor;
            public Vec2Data nodePivot;
            public int nodeDepth;
            public Vec2Data psdCenter;
            public Vec2Data psdSize;
            public Vec2Data psdAnchor;
            public Vec2Data psdPivot;
            public int psdDepth;
        }

        [Serializable]
        private class RegressionFixture
        {
            public float maxDistanceError;
            public float maxSizeDiff;
            public int maxDepthDiff;
            public float weightPosition;
            public float weightSize;
            public float weightType;
            public RegressionCase[] cases;
        }

        [MenuItem("PSDTools/Debug/Run Match Scoring Regression")]
        public static void RunFromMenu()
        {
            bool ok = RunRegression();
            Debug.Log(ok ? "[MatchRegression] PASS" : "[MatchRegression] FAIL");
        }

        public static bool RunRegression()
        {
            string fullPath = Path.Combine(Directory.GetCurrentDirectory(), FixturePath);
            if (!File.Exists(fullPath))
            {
                Debug.LogError($"[MatchRegression] Fixture not found: {FixturePath}");
                return false;
            }

            string json = File.ReadAllText(fullPath);
            RegressionFixture fixture = JsonUtility.FromJson<RegressionFixture>(json);
            if (fixture == null || fixture.cases == null || fixture.cases.Length == 0)
            {
                Debug.LogError("[MatchRegression] Fixture parsing failed or no cases.");
                return false;
            }

            PSDImportConfig config = ScriptableObject.CreateInstance<PSDImportConfig>();
            config.maxDistanceError = fixture.maxDistanceError;
            config.maxSizeDiff = fixture.maxSizeDiff;
            config.weightPosition = fixture.weightPosition;
            config.weightSize = fixture.weightSize;
            config.weightType = fixture.weightType;

            try
            {
                foreach (RegressionCase regressionCase in fixture.cases)
                {
                    var nodeGeom = BuildNodeGeom(regressionCase);
                    var psdGeom = BuildPsdGeom(regressionCase);

                    var scoringInput = PSDMatchScoring.ScoringInput.FromConfig(nodeGeom, psdGeom, regressionCase.isTypeMatch, config);
                    scoringInput.maxDepthDiff = fixture.maxDepthDiff;
                    PSDMatchScoring.ScoreBreakdown unified = PSDMatchScoring.Evaluate(scoringInput);

                    float strategyScore = PSDMatchingStrategy.CalculateMatchScoreFromGeometry(nodeGeom, psdGeom, regressionCase.isTypeMatch, config);
                    PSDMatchScoring.ScoreBreakdown restoreScore = VisualBindingRestoreService.CalculateScoreFromGeometry(nodeGeom, psdGeom, regressionCase.isTypeMatch, config);

                    float[] features = PSDMatchFeatureExtractor.ExtractFeaturesFromGeometry(
                        nodeGeom,
                        psdGeom,
                        regressionCase.isTypeMatch,
                        regressionCase.inLayout,
                        fixture.maxDistanceError,
                        fixture.maxSizeDiff,
                        fixture.maxDepthDiff);

                    bool caseOk = NearlyEqual(unified.total, strategyScore)
                        && NearlyEqual(unified.total, restoreScore.total)
                        && NearlyEqual(unified.geometry.distNorm, features[0])
                        && NearlyEqual(unified.geometry.sizeNorm, features[1])
                        && NearlyEqual(regressionCase.isTypeMatch ? 1f : 0f, features[2])
                        && NearlyEqual(unified.geometry.sameDepth, features[4])
                        && NearlyEqual(unified.geometry.anchorDiff, features[5]);

                    if (!caseOk)
                    {
                        Debug.LogError($"[MatchRegression] Case '{regressionCase.name}' mismatch. unified={unified.total:F4}, strategy={strategyScore:F4}, restore={restoreScore.total:F4}, feat(dist={features[0]:F4}, size={features[1]:F4}, type={features[2]:F4}, sameDepth={features[4]:F4}, anchor={features[5]:F4})");
                        return false;
                    }
                }

                return true;
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(config);
            }
        }

        private static PSDMatchGeometry.NodeGeom BuildNodeGeom(RegressionCase regressionCase)
        {
            PSDMatchGeometry.NodeGeom geom = default;
            geom.centerLocal = regressionCase.nodeCenter.ToVector2();
            geom.sizeLocal = regressionCase.nodeSize.ToVector2();
            geom.rectMinLocal = geom.centerLocal - geom.sizeLocal * 0.5f;
            geom.rectMaxLocal = geom.centerLocal + geom.sizeLocal * 0.5f;
            geom.depth = regressionCase.nodeDepth;
            geom.anchorCenter = regressionCase.nodeAnchor.ToVector2();
            geom.pivot = regressionCase.nodePivot.ToVector2();
            return geom;
        }

        private static PSDMatchGeometry.PsdGeom BuildPsdGeom(RegressionCase regressionCase)
        {
            PSDMatchGeometry.PsdGeom geom = default;
            geom.centerLocal = regressionCase.psdCenter.ToVector2();
            geom.sizeLocal = regressionCase.psdSize.ToVector2();
            geom.rectMinLocal = geom.centerLocal - geom.sizeLocal * 0.5f;
            geom.rectMaxLocal = geom.centerLocal + geom.sizeLocal * 0.5f;
            geom.depth = regressionCase.psdDepth;
            geom.anchorCenter = regressionCase.psdAnchor.ToVector2();
            geom.pivot = regressionCase.psdPivot.ToVector2();
            return geom;
        }

        private static bool NearlyEqual(float a, float b)
        {
            return Mathf.Abs(a - b) <= Tolerance;
        }
    }
}
