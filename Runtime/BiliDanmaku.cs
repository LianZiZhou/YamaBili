using UdonSharp;
using UnityEngine;
using VRC.SDK3.Data;
using VRC.SDK3.StringLoading;
using VRC.SDKBase;
using VRC.Udon.Common.Interfaces;
using Yamadev.YamaStream;

namespace YamaBili
{
    // YamaPlayer plugin: when the playing URL belongs to bili-proxy, downloads the same URL as a string
    // (the server answers downloaders with JSON) and hands danmaku/subtitles to the views and the
    // episode list to the search panel. Also continues to the next episode when a video ends.
    [UdonBehaviourSyncMode(BehaviourSyncMode.None)]
    public class BiliDanmaku : Listener
    {
        [SerializeField] Controller _controller;
        [SerializeField] BiliUrlPool _pool;
        [SerializeField] BiliDanmakuView[] _views;
        [SerializeField] UdonSharpBehaviour[] _panels; // BiliSearch of each screen UI, told when the episode list or settings change
        [SerializeField] int _requestsPerHour = 60;

        public bool AutoNext = true;

        [HideInInspector] public string Title = "";
        [HideInInspector] public string Bv = "";
        [HideInInspector] public int[] PageSlots = new int[0];
        [HideInInspector] public string[] PageTitles = new string[0];
        [HideInInspector] public string[] PageLinks = new string[0];
        [HideInInspector] public int PageCurrent = -1;
        [HideInInspector] public string Status = "";

        VRCUrl _pending;
        int _requests;
        float _windowStart;

        void Start()
        {
            _controller.AddListener(this);
            _views[0].SettingsListener = this;
        }

        public override void OnUrlChanged()
        {
            ClearAll();
            VRCUrl url = _controller.Track.GetVRCUrl();
            if (url == null || !_pool.IsOurs(url.Get())) { Notify(); return; }

            if (Time.time - _windowStart > 3600f) { _windowStart = Time.time; _requests = 0; }
            if (_requests >= _requestsPerHour) { Status = "hourLimit"; Notify(); return; }
            _requests++;
            _pending = url;
            Status = "loading";
            Notify();
            VRCStringDownloader.LoadUrl(url, (IUdonEventReceiver)this);
        }

        void ClearAll()
        {
            for (int i = 0; i < _views.Length; i++) _views[i].Clear();
            Title = "";
            Bv = "";
            PageSlots = new int[0];
            PageTitles = new string[0];
            PageLinks = new string[0];
            PageCurrent = -1;
            Status = "";
            _pending = null;
        }

        public override void OnStringLoadSuccess(IVRCStringDownload result)
        {
            if (_pending == null || result.Url.Get() != _pending.Get()) return; // superseded by a newer track
            _pending = null;
            if (!VRCJson.TryDeserializeFromJson(result.Result, out DataToken json) || json.TokenType != TokenType.DataDictionary)
            {
                Status = "badResponse";
                Notify();
                return;
            }
            DataDictionary d = json.DataDictionary;
            if (d.TryGetValue("error", TokenType.String, out DataToken err))
            {
                Status = err.String;
                Notify();
                return;
            }

            Title = Str(d, "title");
            Bv = Str(d, "bv");
            string part = Str(d, "part");
            if (!_controller.Track.HasTitle())
            {
                // Local only, like YamaPlayer's own VideoInfo does for YouTube titles.
                _controller.Track.SetTitle(part != "" ? Title + " / " + part : Title);
                _controller.SendCustomVideoEvent(nameof(OnVideoInfoLoaded));
            }

            ReadPages(d);
            ReadDanmaku(d);
            Status = "";
            Notify();
        }

        public override void OnStringLoadError(IVRCStringDownload result)
        {
            if (_pending == null || result.Url.Get() != _pending.Get()) return;
            _pending = null;
            Status = "networkError";
            Notify();
        }

        void ReadPages(DataDictionary d)
        {
            if (!d.TryGetValue("pages", TokenType.DataList, out DataToken pages)) return;
            DataList list = pages.DataList;
            int n = list.Count;
            PageSlots = new int[n];
            PageTitles = new string[n];
            PageLinks = new string[n];
            for (int i = 0; i < n; i++)
            {
                DataDictionary p = list[i].DataDictionary;
                PageSlots[i] = Int(p, "i");
                PageTitles[i] = Str(p, "title");
                PageLinks[i] = Str(p, "link");
            }
            PageCurrent = d.TryGetValue("cur", TokenType.Double, out DataToken cur) ? (int)cur.Double : -1;
        }

        void ReadDanmaku(DataDictionary d)
        {
            float[] rollT = Times(d, "roll", 0); string[] rollS = Texts(d, "roll", 1); int[] rollC = Colors(d, "roll");
            float[] topT = Times(d, "top", 0); string[] topS = Texts(d, "top", 1); int[] topC = Colors(d, "top");
            float[] botT = Times(d, "bottom", 0); string[] botS = Texts(d, "bottom", 1); int[] botC = Colors(d, "bottom");
            float[] subFrom = Times(d, "sub", 0); float[] subTo = Times(d, "sub", 1); string[] subS = Texts(d, "sub", 2);
            for (int i = 0; i < _views.Length; i++)
                _views[i].SetData(rollT, rollS, rollC, topT, topS, topC, botT, botS, botC, subFrom, subTo, subS);
        }

        // --- Episode continuation ---------------------------------------------

        public override void OnVideoEnd()
        {
            if (!AutoNext || !Networking.IsOwner(_controller.gameObject)) return;
            int next = PageCurrent + 1;
            if (PageCurrent < 0 || next >= PageSlots.Length) return;
            Playlist queue = _controller.Queue;
            if (queue == null || queue.Length > 0) return; // tracks the user queued come first
            VRCUrl url = _pool.GetV(PageSlots[next]);
            if (url == null) return;
            // The controller runs Forward() shortly after OnVideoEnd, which picks this up from the queue.
            queue.TakeOwnership();
            queue.AddTrack(Track.New(VideoPlayerType.AVProVideoPlayer, Title + " / " + PageTitles[next], url, PageLinks[next]));
        }

        public void PlayPage(int index)
        {
            if (index < 0 || index >= PageSlots.Length) return;
            VRCUrl url = _pool.GetV(PageSlots[index]);
            if (url == null) return;
            _controller.TakeOwnership();
            _controller.PlayTrack(Track.New(VideoPlayerType.AVProVideoPlayer, Title + " / " + PageTitles[index], url, PageLinks[index]));
        }

        public void RefreshViews()
        {
            for (int i = 1; i < _views.Length; i++) _views[i].CopySettings(_views[0]);
            for (int i = 0; i < _views.Length; i++) _views[i].ApplySettings();
            SendToPanels("_OnDanmakuSettingsChanged");
        }

        // The first view edited by the panels; sent once its saved settings are restored.
        public void _OnDanmakuSettingsChanged() => RefreshViews();

        void Notify() => SendToPanels("_OnVideoInfoChanged");

        void SendToPanels(string eventName)
        {
            for (int i = 0; i < _panels.Length; i++)
                if (_panels[i] != null) _panels[i].SendCustomEvent(eventName);
        }

        // --- JSON helpers -------------------------------------------------------

        static string Str(DataDictionary d, string key)
        {
            return d.TryGetValue(key, TokenType.String, out DataToken t) ? t.String : "";
        }

        static int Int(DataDictionary d, string key)
        {
            return d.TryGetValue(key, TokenType.Double, out DataToken t) ? (int)t.Double : -1;
        }

        // Each entry of d[key] is a list; returns column `col` as numbers.
        static float[] Times(DataDictionary d, string key, int col)
        {
            if (!d.TryGetValue(key, TokenType.DataList, out DataToken t)) return new float[0];
            DataList list = t.DataList;
            float[] result = new float[list.Count];
            for (int i = 0; i < result.Length; i++)
            {
                DataToken v = list[i].DataList[col];
                result[i] = v.TokenType == TokenType.Double ? (float)v.Double : 0f;
            }
            return result;
        }

        // Optional third column of a danmaku entry: 0xRRGGBB, or -1 (white) when absent.
        static int[] Colors(DataDictionary d, string key)
        {
            if (!d.TryGetValue(key, TokenType.DataList, out DataToken t)) return new int[0];
            DataList list = t.DataList;
            int[] result = new int[list.Count];
            for (int i = 0; i < result.Length; i++)
            {
                DataList entry = list[i].DataList;
                result[i] = entry.Count > 2 && entry[2].TokenType == TokenType.Double ? (int)entry[2].Double : -1;
            }
            return result;
        }

        static string[] Texts(DataDictionary d, string key, int col)
        {
            if (!d.TryGetValue(key, TokenType.DataList, out DataToken t)) return new string[0];
            DataList list = t.DataList;
            string[] result = new string[list.Count];
            for (int i = 0; i < result.Length; i++)
            {
                DataToken v = list[i].DataList[col];
                result[i] = v.TokenType == TokenType.String ? v.String : "";
            }
            return result;
        }
    }
}
