using UnityEngine;
using PassthroughCameraSamples;
public class WebcamToRT : MonoBehaviour
{

    public WebCamTextureManager webCamTextureManager;
    public RenderTexture target;   // assign RT_224
    public int width = 640, height = 480, fps = 30;

    public bool IsPassthrough = false;

    private WebCamTexture cam;

    void Start() {


        if (!IsPassthrough)
        { 
        cam = new WebCamTexture(width, height, fps);
        cam.Play();

        }

    }

    void Update()
    {
        if (target == null || cam == null) return;
        if (cam.didUpdateThisFrame && !IsPassthrough)
        {
            Graphics.Blit(cam, target);
        }
        
        if (IsPassthrough)
        {
            Graphics.Blit(webCamTextureManager.WebCamTexture, target);
        }
    }

    void OnDestroy() {
        if (cam != null) cam.Stop();
    }
}
