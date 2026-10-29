using UdonSharp;
using UnityEngine;

namespace YamaBili
{
    // Lets one handler serve a whole column of row buttons: sets `_clickedIndex` on the target, then sends the event.
    [UdonBehaviourSyncMode(BehaviourSyncMode.None)]
    public class BiliIndexButton : UdonSharpBehaviour
    {
        public UdonSharpBehaviour Target;
        public int Index;
        public string EventName;

        public void OnClick()
        {
            Target.SetProgramVariable("_clickedIndex", Index);
            Target.SendCustomEvent(EventName);
        }
    }
}
