using UnityEngine;
using UnityObject = UnityEngine.Object;

namespace PSDImporter
{
    public enum PSDMatchAuditRunMode
    {
        DryRunAudit,
        ApplyAndSave
    }

    [CreateAssetMenu(fileName = "PSDMatchAuditConfig", menuName = "PSD2NGUI/Match Audit Config")]
    public class PSDMatchAuditConfig : ScriptableObject
    {
        [Header("Inputs")]
        public UnityObject psdDataAsset;
        public string psdDataPath;
        public GameObject targetRoot;
        public string targetRootPath;
        public PSDImportConfig importConfig;

        [Header("Run")]
        public PSDMatchAuditRunMode runMode = PSDMatchAuditRunMode.DryRunAudit;
        [Min(1)] public int maxTopCandidates = 5;
        [Min(1)] public int maxSuspects = 12;
        [Range(1, 4)] public int captureScale = 1;
        public int auditRenderWidth = 0;
        public int auditRenderHeight = 0;

        [Header("Output")]
        public string outputFolder = "Assets/_OpenCode/TZUI/PS/PSDTools/Log/Audit";
    }
}
