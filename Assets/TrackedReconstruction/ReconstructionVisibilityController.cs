using UnityEngine;
using UnityEngine.XR.ARFoundation;
namespace SwaKaar3DIY.TrackedReconstruction
{
    // Scene-owned controls survive temporary image tracking loss and content recreation.
    [DefaultExecutionOrder(200)]
    public sealed class ReconstructionVisibilityController : MonoBehaviour
    {
        [SerializeField] ARTrackedImageManager imageManager;
        [SerializeField] UnityEngine.UI.Toggle meshToggle;
        [SerializeField] UnityEngine.UI.Toggle pointsToggle;
        void Awake()
        {
            if (imageManager == null || meshToggle == null || pointsToggle == null)
            {
                Debug.LogError("Reconstruction visibility references are incomplete.", this);
                enabled = false;
            }
        }
        void LateUpdate()
        {
            foreach (var image in imageManager.trackables)
            {
                if (image == null || image.referenceImage.name != "NASAEarth") continue;
                var content = image.GetComponentInChildren<ReconstructionRepresentations>(true);
                if (content != null) content.SetVisibility(meshToggle.isOn, pointsToggle.isOn);
            }
        }
    }
}