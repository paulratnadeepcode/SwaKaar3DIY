using UnityEngine;

namespace SwaKaar3DIY.UI
{
    [RequireComponent(typeof(RectTransform))]
    public sealed class SafeAreaLayout : MonoBehaviour
    {
        RectTransform rect;
        Rect previousArea;
        Vector2Int previousSize;

        void OnEnable() { rect = (RectTransform)transform; Apply(); }
        void Update()
        {
            if (Screen.safeArea != previousArea || previousSize != new Vector2Int(Screen.width, Screen.height)) Apply();
        }
        void Apply()
        {
            if (Screen.width <= 0 || Screen.height <= 0) return;
            previousArea = Screen.safeArea;
            previousSize = new Vector2Int(Screen.width, Screen.height);
            rect.anchorMin = new Vector2(previousArea.xMin / Screen.width, previousArea.yMin / Screen.height);
            rect.anchorMax = new Vector2(previousArea.xMax / Screen.width, previousArea.yMax / Screen.height);
            rect.offsetMin = rect.offsetMax = Vector2.zero;
        }
    }
}
