using System;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;

namespace SwaKaar3DIY.UI
{
    public sealed class ConfirmationDialog : MonoBehaviour
    {
        [SerializeField] TMP_Text message;
        [SerializeField] TMP_Text confirmLabel;
        [SerializeField] UnityEngine.UI.Button cancelButton;
        [SerializeField] UnityEngine.UI.Button confirmButton;
        [SerializeField] RectTransform safeArea;
        [SerializeField] RectTransform panel;
        Action confirmed;
        GameObject previousSelection;
        public bool IsOpen { get; private set; }

        void LateUpdate()
        {
            panel.sizeDelta = new Vector2(Mathf.Max(1, Mathf.Min(820, safeArea.rect.width - 48)),
                Mathf.Max(1, Mathf.Min(300, safeArea.rect.height - 48)));
        }

        void Awake()
        {
            cancelButton.onClick.AddListener(Cancel);
            confirmButton.onClick.AddListener(Confirm);
        }

        public void Show(string prompt, string confirmText, Action onConfirm)
        {
            if (IsOpen) return;
            previousSelection = EventSystem.current != null ? EventSystem.current.currentSelectedGameObject : null;
            message.text = prompt;
            confirmLabel.text = confirmText;
            confirmed = onConfirm;
            IsOpen = true;
            gameObject.SetActive(true);
            cancelButton.interactable = confirmButton.interactable = true;
            if (EventSystem.current != null) EventSystem.current.SetSelectedGameObject(cancelButton.gameObject);
        }

        public void Cancel()
        {
            if (!IsOpen) return;
            Close();
            if (EventSystem.current != null && previousSelection != null)
                EventSystem.current.SetSelectedGameObject(previousSelection);
        }

        public void Confirm()
        {
            if (!IsOpen) return;
            var action = confirmed;
            Close();
            action?.Invoke();
        }

        void Close()
        {
            IsOpen = false;
            confirmed = null;
            cancelButton.interactable = confirmButton.interactable = false;
            gameObject.SetActive(false);
        }
    }
}
