using UnityEngine;
namespace SwaKaar3DIY.TrackedReconstruction
{
    public sealed class ReconstructionRepresentations : MonoBehaviour
    {
        [SerializeField] GameObject meshRoot;
        [SerializeField] GameObject pointsRoot;
        public void SetVisibility(bool mesh, bool points)
        {
            if (meshRoot != null && meshRoot.activeSelf != mesh) meshRoot.SetActive(mesh);
            if (pointsRoot != null && pointsRoot.activeSelf != points) pointsRoot.SetActive(points);
        }
    }
}