using System;
using System.IO;
using System.Collections.Generic;
using UnityEngine;

// ===== DTO for import/export =====
[Serializable]
public class DetectionSetupJson
{
    public string name;        // required: head/detection name (no .json)
    public string[] classes;   // optional: class labels (no samples)
    public string tag;         // optional metadata (for logs/UI only)
    public string version;     // optional
    public string notes;       // optional
}

/// <summary>
/// Extension methods for your existing DetectionManager (no changes to its class).
/// Usage:
///   detectionManager.InjectDetectionFromJson(jsonString, true, true);
///   var json = detectionManager.ExportDetectionSetupAsJson(optionalTag, optionalVer, optionalNotes);
///   var path = detectionManager.SaveExportToFile(); // writes to persistentDataPath
/// </summary>
public static class DetectionManagerJsonExtensions
{
    // --------- Inject settings (name + classes) without samples ----------
    public static bool InjectDetectionFromJson(this DetectionManager dm, string json, bool setLivePreferredName = true, bool clearTrainer = true)
    {
        if (dm == null) { Debug.LogWarning("[DM-JSON] DetectionManager is null."); return false; }
        if (string.IsNullOrWhiteSpace(json))
        {
            Debug.LogWarning("[DM-JSON] Inject: empty json.");
            return false;
        }

        DetectionSetupJson cfg = null;
        try { cfg = JsonUtility.FromJson<DetectionSetupJson>(json); }
        catch (Exception e)
        {
            Debug.LogError("[DM-JSON] Inject: JSON parse error -> " + e.Message);
            return false;
        }

        if (cfg == null || string.IsNullOrWhiteSpace(cfg.name))
        {
            Debug.LogWarning("[DM-JSON] Inject: missing required 'name'.");
            return false;
        }

        // sanitize name
        string safeName = SanitizeFileName(cfg.name);
        var labels = (cfg.classes != null) ? new List<string>(cfg.classes) : new List<string>();

        // ----- get trainer from DetectionManager via reflection (no code changes needed) -----
        var trainerField = dm.GetType().GetField("trainer",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
        var trainer = (trainerField != null) ? trainerField.GetValue(dm) as DynamicClassTrainer : null;

        if (trainer != null)
        {
            try
            {
                // reset & set classes (no samples)  *** List -> array ***
                trainer.ResetAll(labels.ToArray());
                // point trainer file names to this head
                trainer.sessionName  = safeName;
                trainer.saveHeadName = safeName + ".json";
                trainer.loadHeadName = safeName + ".json";
            }
            catch (Exception e)
            {
                Debug.LogWarning("[DM-JSON] Inject: trainer.ResetAll failed -> " + e.Message);
            }
        }
        else
        {
            Debug.LogWarning("[DM-JSON] Inject: no trainer on DetectionManager.");
        }

        // prefs to keep other UIs in sync
        PlayerPrefs.SetString("TTM_LastHead", safeName);
        PlayerPrefs.SetString("TinyTeach_LastHeadName", safeName);
        PlayerPrefs.Save();

        // ----- optionally prep LiveClassifier to prefer this head name -----
        var clsField = dm.GetType().GetField("classifier",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
        var cls = (clsField != null) ? clsField.GetValue(dm) as LiveClassifier : null;
        if (setLivePreferredName && cls != null)
        {
            try
            {
                cls.StopClassifying();
                cls.Clear();
                cls.ClearPreferredHead();
                cls.SetPreferredHead(safeName, enforce: true);
            }
            catch { /* safe no-op */ }
        }

        Debug.Log($"[DM-JSON] Injected detection setup -> name='{safeName}', classes=[{string.Join(", ", labels)}]");
        return true;
    }

    // --------- Export current setup (name + classes) as JSON ----------
    public static string ExportDetectionSetupAsJson(this DetectionManager dm, string tag = null, string version = null, string notes = null)
    {
        if (dm == null) return "{}";

        string headName = "Active";
        string[] classesArr = Array.Empty<string>();

        // prefer trainer
        var trainerField = dm.GetType().GetField("trainer",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
        var trainer = (trainerField != null) ? trainerField.GetValue(dm) as DynamicClassTrainer : null;

        if (trainer != null)
        {
            try
            {
                if (!string.IsNullOrEmpty(trainer.saveHeadName))
                    headName = Path.GetFileNameWithoutExtension(trainer.saveHeadName);
                else if (!string.IsNullOrEmpty(trainer.sessionName))
                    headName = trainer.sessionName;

                if (trainer.Classes != null)
                    classesArr = trainer.Classes.ToArray();
            }
            catch { /* ignore */ }
        }
        else
        {
            // fallback: read PlayerPrefs if no trainer
            headName = PlayerPrefs.GetString("TTM_LastHead", headName);
        }

        var dto = new DetectionSetupJson
        {
            name = headName,
            classes = classesArr,
            tag = tag,
            version = version,
            notes = notes
        };

        string json = JsonUtility.ToJson(dto, prettyPrint: true);
        Debug.Log("[DM-JSON] Export:\n" + json);
        return json;
    }

    // --------- Save export JSON to disk (optional helper) ----------
    public static string SaveExportToFile(this DetectionManager dm, string fileName = null)
    {
        string json = dm.ExportDetectionSetupAsJson();
        if (string.IsNullOrEmpty(fileName))
        {
            // try to name it after head
            try
            {
                var obj = JsonUtility.FromJson<DetectionSetupJson>(json);
                fileName = !string.IsNullOrEmpty(obj?.name) ? $"{SanitizeFileName(obj.name)}.detection.json" : "detection_setup.json";
            }
            catch { fileName = "detection_setup.json"; }
        }

        string dir = Application.persistentDataPath;
        string path = Path.Combine(dir, fileName);
        File.WriteAllText(path, json);
        Debug.Log($"[DM-JSON] Export saved → {path}");
        return path;
    }

    // --------- utils ----------
    static string SanitizeFileName(string t)
    {
        if (string.IsNullOrWhiteSpace(t)) return "Active";
        foreach (char c in Path.GetInvalidFileNameChars())
            t = t.Replace(c, '_');
        return t.Trim();
    }
}
