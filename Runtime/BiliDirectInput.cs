using UdonSharp;
using UnityEngine;
using VRC.SDK3.Components;
using VRC.SDKBase;

namespace YamaBili
{
    // Covers YamaPlayer's link-icon URL field with one that starts out holding the server's ?url= prefix,
    // so a pasted Bilibili link plays through the proxy. Whatever is entered goes into YamaPlayer's own
    // field and its PlayUrl event, keeping the player's permission check and play / queue dialog.
    [UdonBehaviourSyncMode(BehaviourSyncMode.None)]
    public class BiliDirectInput : UdonSharpBehaviour
    {
        [SerializeField] UdonSharpBehaviour _uiController; // YamaPlayer UIController
        [SerializeField] VRCUrlInputField _original;       // YamaPlayer's field, under ours
        [SerializeField] string _playEvent = "PlayUrl";
        [SerializeField] VRCUrlInputField _input;
        [SerializeField] BiliUrlPool _pool;

        void Start() => Refill();

        // Also sent on pointer enter: a field set while its canvas was still hidden can come up empty.
        public void Refill()
        {
            VRCUrl url = _input.GetUrl();
            if (url == null || string.IsNullOrEmpty(url.Get())) _input.SetUrl(_pool.DirectPrefix);
        }

        public void Submit()
        {
            VRCUrl url = _input.GetUrl();
            string text = url == null ? "" : url.Get();
            string prefix = _pool.DirectPrefix.Get();
            // Left alone, or only the prefix: nothing to play.
            if (text.Trim().Length == 0 || (text.StartsWith(prefix) && text.Substring(prefix.Length).Trim().Length == 0))
            {
                _input.SetUrl(_pool.DirectPrefix);
                return;
            }
            _original.SetUrl(url);
            _uiController.SendCustomEvent(_playEvent);
            _input.SetUrl(_pool.DirectPrefix);
        }
    }
}
