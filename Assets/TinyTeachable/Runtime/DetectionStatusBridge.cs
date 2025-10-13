using System;
using System.Reflection;
using UnityEngine;
using UnityEngine.Events;

[DisallowMultipleComponent]
public class DetectionStatusBridge : MonoBehaviour
{
    [Header("Hook your existing components (auto-discovered if left empty)")]
    public DetectionManager detectionManager;   // optional
    public DynamicClassTrainer trainer;         // optional
    public LiveClassifier classifier;           // optional

    [Tooltip("Try to locate DetectionManager / Trainer / LiveClassifier at runtime if not assigned.")]
    public bool autoDiscover = true;

    // -------- UnityEvent types --------
    [Serializable] public class StringEvent : UnityEvent<string> {}
    [Serializable] public class BoolEvent   : UnityEvent<bool> {}
    [Serializable] public class IntEvent    : UnityEvent<int>  {}
    [Serializable] public class StringArrayEvent : UnityEvent<string[]> {}

    [Header("Events")]
    public StringEvent onHeadNameChanged;
    public BoolEvent   onIsClassifyingChanged;
    public IntEvent    onClassCountChanged;
    public StringArrayEvent onClassLabelsChanged;
    public UnityEvent  onStartedClassifying;
    public UnityEvent  onStoppedClassifying;

    // cached state
    string   _lastHeadName = "";
    bool     _lastIsClassifying = false;
    int      _lastClassCount = 0;
    string[] _lastLabels = Array.Empty<string>();

    void Awake()
    {
        if (autoDiscover)
        {
            if (!detectionManager) detectionManager = FindObjectOfType<DetectionManager>(true);
            if (!trainer)
            {
                // Prefer trainer attached to same GO or parent first
                trainer = GetComponentInParent<DynamicClassTrainer>();
                if (!trainer) trainer = FindObjectOfType<DynamicClassTrainer>(true);
            }
            if (!classifier)
            {
                classifier = GetComponentInParent<LiveClassifier>();
                if (!classifier) classifier = FindObjectOfType<LiveClassifier>(true);
            }
        }
    }

    void Start()
    {
        // seed caches so we only fire when values change
        _lastHeadName = CurrentHeadName;
        _lastIsClassifying = IsClassifying;
        _lastClassCount = ClassCount;
        _lastLabels = ClassLabels;
    }

    void Update()
    {
        // Head name change
        var head = CurrentHeadName;
        if (!string.Equals(head, _lastHeadName, StringComparison.Ordinal))
        {
            _lastHeadName = head;
            onHeadNameChanged?.Invoke(head);
        }

        // Running change
        var running = IsClassifying;
        if (running != _lastIsClassifying)
        {
            _lastIsClassifying = running;
            onIsClassifyingChanged?.Invoke(running);
            if (running) onStartedClassifying?.Invoke();
            else         onStoppedClassifying?.Invoke();
        }

        // Class count change
        var count = ClassCount;
        if (count != _lastClassCount)
        {
            _lastClassCount = count;
            onClassCountChanged?.Invoke(count);
        }

        // Labels change (shallow compare)
        var labels = ClassLabels;
        if (!ArraysEqual(labels, _lastLabels))
        {
            _lastLabels = labels;
            onClassLabelsChanged?.Invoke(labels);
        }
    }

    // ---- Public read API ----
    public string CurrentHeadName
    {
        get
        {
            if (classifier != null)
            {
                try
                {
                    var prop = classifier.GetType().GetProperty("CurrentHeadName", BindingFlags.Instance | BindingFlags.Public);
                    if (prop != null) return prop.GetValue(classifier) as string ?? "";
                } catch {}
            }
            if (trainer != null && !string.IsNullOrWhiteSpace(trainer.saveHeadName))
                return System.IO.Path.GetFileNameWithoutExtension(trainer.saveHeadName);
            return PlayerPrefs.GetString("TTM_LastHead", "");
        }
    }

    public bool IsClassifying
    {
        get
        {
            if (!classifier) return false;
            var t = classifier.GetType();
            try { var p = t.GetProperty("IsClassifying", BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic);
                  if (p?.PropertyType == typeof(bool)) return (bool)p.GetValue(classifier); } catch {}
            try { var f = t.GetField("isClassifying", BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic);
                  if (f?.FieldType == typeof(bool)) return (bool)f.GetValue(classifier); } catch {}
            try { var p2 = t.GetProperty("Running", BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic);
                  if (p2?.PropertyType == typeof(bool)) return (bool)p2.GetValue(classifier); } catch {}
            try { var f2 = t.GetField("running", BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic);
                  if (f2?.FieldType == typeof(bool)) return (bool)f2.GetValue(classifier); } catch {}
            return false;
        }
    }

    public int ClassCount => (trainer && trainer.Classes != null) ? trainer.Classes.Count : 0;
    public string[] ClassLabels => (trainer && trainer.Classes != null) ? trainer.Classes.ToArray() : Array.Empty<string>();
    public bool IsDetectionReady => !string.IsNullOrEmpty(CurrentHeadName) && ClassCount > 0;

    // ---- Convenience control (optional) ----
    public bool TryStart()
    {
        if (!classifier) return false;
        try { classifier.GetType().GetMethod("StartClassifying", BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic)?.Invoke(classifier, null); return true; }
        catch { return false; }
    }
    public bool TryStop()
    {
        if (!classifier) return false;
        try { classifier.GetType().GetMethod("StopClassifying", BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic)?.Invoke(classifier, null); return true; }
        catch { return false; }
    }

    // ---- utils ----
    static bool ArraysEqual(string[] a, string[] b)
    {
        if (ReferenceEquals(a,b)) return true;
        if (a == null || b == null || a.Length != b.Length) return false;
        for (int i=0;i<a.Length;i++) if (!string.Equals(a[i], b[i], StringComparison.Ordinal)) return false;
        return true;
    }
}
