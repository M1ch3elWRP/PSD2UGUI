using System.IO;
using UnityEditor;
using UnityEngine;

namespace PSDImporter
{
    public static class PSDMatchMLMenu
    {
        [MenuItem("PSDTools/ML/Train Match Model")]
        private static void TrainMatchModel()
        {
            string datasetPath = EditorUtility.OpenFilePanel("Select Match Dataset (JSON)", "", "json");
            if (string.IsNullOrEmpty(datasetPath)) return;

            string json = File.ReadAllText(datasetPath);
            var dataset = JsonUtility.FromJson<PSDMatchDataset>(json);
            if (dataset == null || dataset.samples == null || dataset.samples.Length == 0)
            {
                EditorUtility.DisplayDialog("Match ML", "Dataset is empty or invalid.", "OK");
                return;
            }

            var options = new PSDMatchTrainingOptions();
            PSDMatchTrainingReport report;
            var model = PSDMatchML.Train(dataset, options, out report);

            string outputPath = EditorUtility.SaveFilePanel("Save Match Model (JSON)", "", "psd_match_model.json", "json");
            if (string.IsNullOrEmpty(outputPath)) return;

            string outJson = JsonUtility.ToJson(model, true);
            File.WriteAllText(outputPath, outJson);

            EditorUtility.DisplayDialog(
                "Match ML",
                $"Saved model. Samples={report.sampleCount}, Loss={report.loss:F4}, Acc={report.accuracy:P1}",
                "OK");
        }
    }
}
