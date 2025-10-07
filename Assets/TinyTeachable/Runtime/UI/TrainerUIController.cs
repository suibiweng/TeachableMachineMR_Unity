using UnityEngine;
using UnityEngine.UI;
using TMPro;

public class TrainerUIController : MonoBehaviour
{
    [Header("Wiring")]
    public DynamicClassTrainer trainer;

    [Header("UI")]
    public TMP_InputField newClassInput;
    public TMP_Dropdown classDropdown;
    public TMP_InputField sessionInput;
    public TMP_InputField saveHeadInput;
    public TMP_InputField loadHeadInput;
    public Button addClassBtn;
    public Button addSampleBtn;
    public Button trainSaveBtn;
    public Button loadHeadBtn;
    public Button snapshotBtn;
    public TextMeshProUGUI statusText;

    void Start()
    {
        if (trainer == null) { enabled = false; return; }

        // seed fields
        if (sessionInput) sessionInput.text = trainer.sessionName;
        if (saveHeadInput) saveHeadInput.text = trainer.saveHeadName;
        if (loadHeadInput) loadHeadInput.text = trainer.loadHeadName;

        RefreshClassDropdown();

        // wire events
        if (classDropdown) classDropdown.onValueChanged.AddListener(i => trainer.SelectClass(i));
        if (addClassBtn) addClassBtn.onClick.AddListener(() =>
        {
            var name = (newClassInput ? newClassInput.text : "class_" + (trainer.Classes.Count + 1));
            trainer.AddClass(name);
            RefreshClassDropdown(selectLast: true);
            if (statusText) statusText.text = $"Added class '{name}'";
        });
        if (addSampleBtn) addSampleBtn.onClick.AddListener(() =>
        {
            trainer.AddSampleFromCurrent();
            if (statusText) statusText.text = $"Sample -> {trainer.Classes[trainer.CurrentClassIndex]}  (D={trainer.EmbeddingDim})";
        });
        if (trainSaveBtn) trainSaveBtn.onClick.AddListener(() =>
        {
            PullTextFields();
            trainer.TrainAndSaveAndApply();
            if (statusText) statusText.text = $"Head saved/applied: {trainer.saveHeadName}";
        });
        if (loadHeadBtn) loadHeadBtn.onClick.AddListener(() =>
        {
            PullTextFields();
            trainer.LoadHeadAndApply();
            RefreshClassDropdown(); // reload class list from head
            if (statusText) statusText.text = $"Head loaded: {trainer.loadHeadName}";
        });
        if (snapshotBtn) snapshotBtn.onClick.AddListener(() =>
        {
            trainer.SaveSnapshotPNG("cap");
            if (statusText) statusText.text = "Snapshot saved.";
        });
    }

    void PullTextFields()
    {
        if (sessionInput) trainer.sessionName = sessionInput.text;
        if (saveHeadInput) trainer.saveHeadName = saveHeadInput.text;
        if (loadHeadInput) trainer.loadHeadName = loadHeadInput.text;
    }

    void RefreshClassDropdown(bool selectLast = false)
    {
        if (classDropdown == null) return;
        classDropdown.ClearOptions();
        classDropdown.AddOptions(new System.Collections.Generic.List<string>(trainer.Classes));
        classDropdown.value = selectLast ? Mathf.Max(0, trainer.Classes.Count - 1) : trainer.CurrentClassIndex;
        classDropdown.RefreshShownValue();
    }
}
