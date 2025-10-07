using UnityEngine;

public class WebcamToRT : MonoBehaviour
{
    public RenderTexture target;   // assign RT_224
    public int width = 640, height = 480, fps = 30;

    private WebCamTexture cam;

    void Start() {
        cam = new WebCamTexture(width, height, fps);
        cam.Play();
    }

    void Update() {
        if (target == null || cam == null) return;
        if (cam.didUpdateThisFrame) {
            Graphics.Blit(cam, target);
        }
    }

    void OnDestroy() {
        if (cam != null) cam.Stop();
    }
}
