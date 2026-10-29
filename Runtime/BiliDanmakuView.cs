using UdonSharp;
using UnityEngine;
using UnityEngine.UI;
using VRC.SDK3.Persistence;
using VRC.SDKBase;
using Yamadev.YamaStream;

namespace YamaBili
{
    // Draws danmaku and subtitles in front of one YamaPlayer screen, driven by the local playback time.
    // Scrolling comments live on several depth layers floating in front of the screen. Every frame each
    // layer is scaled for the local player's head distance, so it always covers exactly the screen and
    // clips to it; moving the head sideways still gives parallax between the layers.
    // Nothing here is synced: every player downloads and renders on their own.
    [UdonBehaviourSyncMode(BehaviourSyncMode.None)]
    public class BiliDanmakuView : UdonSharpBehaviour
    {
        [SerializeField] Controller _controller;
        [SerializeField] RectTransform _area;          // any layer: all share the screen's canvas size
        [SerializeField] CanvasGroup _group;
        [SerializeField] GameObject[] _hideWhenActive; // YamaPlayer's menu pages: no danmaku over them
        [SerializeField] int _layerCount = 4;
        [SerializeField] Transform _canvasRoot;        // on the screen plane, forward pointing into the screen
        [SerializeField] RectTransform[] _layers;
        [SerializeField] float[] _layerDepths;         // metres in front of the screen
        [SerializeField] float _minLayerGap = 0.35f;   // hide a layer once the head is closer than this to it
        [SerializeField] Text[] _roll;                 // grouped by layer: slots of layer l are contiguous
        [SerializeField] Text[] _top;
        [SerializeField] Text[] _bottom;
        [SerializeField] Text _subtitle;
        [SerializeField] GameObject _subtitleBox;

        [Header("Settings (defaults)")]
        public bool DanmakuOn = true;
        public bool RollOn = true;
        public bool TopOn = true;
        public bool BottomOn = true;
        public bool SubtitleOn = true;
        public float ScrollSpeed = 240f;   // canvas units per second
        public float FontSize = 34f;
        public float Opacity = 0.85f;
        public float AreaRatio = 0.75f;    // share of the height used by scrolling danmaku
        public float StayTime = 5f;        // seconds a top/bottom comment stays

        const float LaneGap = 1.3f;        // lane height in font sizes
        const float RollSpacing = 40f;     // gap kept behind the previous comment in a lane
        const int LanesPerLayer = 32;
        [SerializeField] string _saveKey = "YamaBili."; // PlayerData key prefix, set by the builder from BiliUrlPool.SaveKeyPrefix

        float[] _rollT, _topT, _botT, _subFrom, _subTo;
        string[] _rollS, _topS, _botS, _subS;
        int[] _rollC, _topC, _botC;        // 0xRRGGBB, -1 for white
        int _rollCur, _topCur, _botCur, _subCur;
        bool _hasData;
        float _lastTime = -1f;

        float[] _rollX, _rollW;
        int[] _laneOwner;        // per layer and lane: roll slot that entered it last, -1 when free
        float[] _topEnd, _botEnd; // per slot: video time the comment disappears
        float _width, _height;
        int _perLayer;
        bool _restored;
        bool _ready;

        void Start()
        {
            // Data that arrived while this screen was off must survive switching it on.
            if (_ready) return;
            Init();
            Clear();
        }

        // A screen that starts switched off (the sofa TV) gets data before its Start has run.
        void Init()
        {
            if (_ready) return;
            _ready = true;
            _rollX = new float[_roll.Length];
            _rollW = new float[_roll.Length];
            _topEnd = new float[_top.Length];
            _botEnd = new float[_bottom.Length];
            _laneOwner = new int[_layerCount * LanesPerLayer];
            _width = _area.rect.width;
            _height = _area.rect.height;
            _perLayer = _roll.Length / _layerCount;
        }

        // --- Data ------------------------------------------------------------

        public void SetData(float[] rollT, string[] rollS, int[] rollC, float[] topT, string[] topS, int[] topC,
            float[] botT, string[] botS, int[] botC, float[] subFrom, float[] subTo, string[] subS)
        {
            Init();
            _rollT = rollT; _rollS = rollS; _rollC = rollC;
            _topT = topT; _topS = topS; _topC = topC;
            _botT = botT; _botS = botS; _botC = botC;
            _subFrom = subFrom; _subTo = subTo; _subS = subS;
            _hasData = true;
            Seek(_controller.VideoTime);
        }

        public void Clear()
        {
            Init();
            _hasData = false;
            _rollT = new float[0]; _topT = new float[0]; _botT = new float[0]; _subFrom = new float[0];
            HideAll();
        }

        void HideAll()
        {
            for (int i = 0; i < _roll.Length; i++) _roll[i].gameObject.SetActive(false);
            for (int i = 0; i < _top.Length; i++) _top[i].gameObject.SetActive(false);
            for (int i = 0; i < _bottom.Length; i++) _bottom[i].gameObject.SetActive(false);
            for (int i = 0; i < _laneOwner.Length; i++) _laneOwner[i] = -1;
            _subtitleBox.SetActive(false);
        }

        void Seek(float time)
        {
            HideAll();
            _rollCur = LowerBound(_rollT, time);
            _topCur = LowerBound(_topT, time);
            _botCur = LowerBound(_botT, time);
            _subCur = LowerBound(_subTo, time);
            _lastTime = time;
        }

        static int LowerBound(float[] times, float t)
        {
            int lo = 0, hi = times.Length;
            while (lo < hi)
            {
                int mid = (lo + hi) / 2;
                if (times[mid] < t) lo = mid + 1; else hi = mid;
            }
            return lo;
        }

        // --- Frame -----------------------------------------------------------

        void Update()
        {
            bool hidden = false;
            for (int i = 0; i < _hideWhenActive.Length; i++)
                if (_hideWhenActive[i] != null && _hideWhenActive[i].activeInHierarchy) hidden = true;
            float alpha = hidden ? 0f : 1f;
            if (_group.alpha != alpha) _group.alpha = alpha;

            if (!_hasData) return;
            if (!hidden) FitLayersToHead();
            float time = _controller.VideoTime;
            // A jump backwards, or forwards by more than a few seconds, is a seek: start over from there.
            if (time < _lastTime - 0.5f || time > _lastTime + 3f) Seek(time);
            bool running = _controller.IsPlaying && !_controller.Paused;
            float dt = running ? Time.deltaTime * _controller.Speed : 0f;
            _lastTime = time;

            UpdateSubtitle(time);

            if (!DanmakuOn) return;
            if (running)
            {
                EmitRoll(time);
                _topCur = EmitFixed(time, _topT, _topS, _topC, _topCur, _top, _topEnd, TopOn, true);
                _botCur = EmitFixed(time, _botT, _botS, _botC, _botCur, _bottom, _botEnd, BottomOn, false);
            }
            MoveRoll(dt);
            ExpireFixed(time, _top, _topEnd);
            ExpireFixed(time, _bottom, _botEnd);
        }

        void UpdateSubtitle(float time)
        {
            if (!SubtitleOn || _subFrom.Length == 0)
            {
                if (_subtitleBox.activeSelf) _subtitleBox.SetActive(false);
                return;
            }
            while (_subCur < _subTo.Length && _subTo[_subCur] <= time) _subCur++;
            bool show = _subCur < _subFrom.Length && _subFrom[_subCur] <= time;
            if (show && _subtitle.text != _subS[_subCur]) _subtitle.text = _subS[_subCur];
            if (_subtitleBox.activeSelf != show) _subtitleBox.SetActive(show);
        }

        void EmitRoll(float time)
        {
            int lanes = Mathf.Clamp(Mathf.FloorToInt(_height * AreaRatio / (FontSize * LaneGap)), 1, LanesPerLayer);
            int emitted = 0;
            // `<=` with a 1 s window: every comment, including the last one, gets its chance.
            while (_rollCur < _rollT.Length && _rollT[_rollCur] <= time && emitted < 4)
            {
                if (time - _rollT[_rollCur] < 1f && RollOn)
                {
                    int pick = PickLane(lanes);
                    if (pick < 0) { _rollCur++; continue; } // every lane of every layer is busy: drop it
                    int layer = pick / LanesPerLayer;
                    int lane = pick % LanesPerLayer;
                    int slot = FreeRollSlot(layer);
                    Text t = _roll[slot];
                    t.text = _rollS[_rollCur];
                    t.fontSize = Mathf.RoundToInt(FontSize);
                    SetColor(t, _rollC, _rollCur);
                    t.gameObject.SetActive(true);
                    _rollW[slot] = t.preferredWidth;
                    _rollX[slot] = _width;
                    _laneOwner[layer * LanesPerLayer + lane] = slot;
                    // Rows of different layers interleave, so comments at different depths don't stack on one line.
                    float row = lane + (float)layer / _layerCount;
                    t.rectTransform.anchoredPosition = new Vector2(_rollX[slot], -row * FontSize * LaneGap);
                    emitted++;
                }
                _rollCur++;
            }
        }

        // Spreads comments over the whole height first: lanes are tried in random order, and a lane where
        // no layer has a comment still entering wins. Only when every row is busy do depths share a row.
        // Returns layer * LanesPerLayer + lane, or -1.
        int PickLane(int lanes)
        {
            int best = -1, bestBusy = 1000;
            int laneStart = Random.Range(0, lanes);
            for (int a = 0; a < lanes; a++)
            {
                int lane = (laneStart + a) % lanes;
                int busy = 0, freeLayer = -1;
                int layerStart = Random.Range(0, _layerCount);
                for (int b = 0; b < _layerCount; b++)
                {
                    int layer = (layerStart + b) % _layerCount;
                    if (LaneEntering(layer, lane)) busy++;
                    if (freeLayer < 0 && _layers[layer].gameObject.activeSelf && LaneFree(layer, lane) && FreeRollSlot(layer) >= 0)
                        freeLayer = layer;
                }
                if (freeLayer >= 0 && busy < bestBusy)
                {
                    bestBusy = busy;
                    best = freeLayer * LanesPerLayer + lane;
                    if (busy == 0) break;
                }
            }
            return best;
        }

        // A lane is free once the comment that entered it last has fully cleared the right edge.
        bool LaneFree(int layer, int lane)
        {
            int owner = _laneOwner[layer * LanesPerLayer + lane];
            if (owner < 0 || !_roll[owner].gameObject.activeSelf) return true;
            return _rollX[owner] + _rollW[owner] + RollSpacing <= _width;
        }

        // Still in the right part of the screen, where a new comment on the same row would crowd it.
        bool LaneEntering(int layer, int lane)
        {
            int owner = _laneOwner[layer * LanesPerLayer + lane];
            if (owner < 0 || !_roll[owner].gameObject.activeSelf) return false;
            return _rollX[owner] + _rollW[owner] > _width * 0.55f;
        }

        // Layer at depth d, seen from a head at distance D, lines up with the screen when scaled by (D - d) / D.
        void FitLayersToHead()
        {
            VRCPlayerApi local = Networking.LocalPlayer;
            if (!Utilities.IsValid(local)) return;
            Vector3 head = local.GetTrackingData(VRCPlayerApi.TrackingDataType.Head).position;
            float distance = Vector3.Dot(head - _canvasRoot.position, -_canvasRoot.forward);
            for (int i = 0; i < _layers.Length; i++)
            {
                float depth = _layerDepths[i];
                bool show = distance - depth > _minLayerGap;
                if (_layers[i].gameObject.activeSelf != show) _layers[i].gameObject.SetActive(show);
                if (!show) continue;
                float scale = (distance - depth) / distance;
                if (Mathf.Abs(_layers[i].localScale.x - scale) > 0.001f) _layers[i].localScale = new Vector3(scale, scale, scale);
            }
        }

        void MoveRoll(float dt)
        {
            for (int i = 0; i < _roll.Length; i++)
            {
                Text t = _roll[i];
                if (!t.gameObject.activeSelf) continue;
                if (dt == 0f) continue;
                _rollX[i] -= ScrollSpeed * dt;
                if (_rollX[i] + _rollW[i] < 0f) { t.gameObject.SetActive(false); continue; }
                RectTransform rt = t.rectTransform;
                rt.anchoredPosition = new Vector2(_rollX[i], rt.anchoredPosition.y);
            }
        }

        int EmitFixed(float time, float[] times, string[] texts, int[] colors, int cur, Text[] slots, float[] ends, bool on, bool fromTop)
        {
            while (cur < times.Length && times[cur] <= time)
            {
                if (time - times[cur] < 1f && on)
                {
                    int slot = FreeSlot(slots);
                    if (slot >= 0)
                    {
                        Text t = slots[slot];
                        t.text = texts[cur];
                        t.fontSize = Mathf.RoundToInt(FontSize);
                        SetColor(t, colors, cur);
                        ends[slot] = times[cur] + StayTime;
                        // Slots are ordered top to bottom (or bottom to top), so the first free slot is the free row.
                        float y = slot * FontSize * LaneGap;
                        t.rectTransform.anchoredPosition = new Vector2(0f, fromTop ? -y : y);
                        t.gameObject.SetActive(true);
                    }
                }
                cur++;
            }
            return cur;
        }

        void ExpireFixed(float time, Text[] slots, float[] ends)
        {
            for (int i = 0; i < slots.Length; i++)
                if (slots[i].gameObject.activeSelf && time >= ends[i]) slots[i].gameObject.SetActive(false);
        }

        static int FreeSlot(Text[] slots)
        {
            for (int i = 0; i < slots.Length; i++) if (!slots[i].gameObject.activeSelf) return i;
            return -1;
        }

        int FreeRollSlot(int layer)
        {
            int end = (layer + 1) * _perLayer;
            for (int i = layer * _perLayer; i < end; i++)
                if (!_roll[i].gameObject.activeSelf) return i;
            return -1;
        }

        // The comment's own colour; very dark ones are lifted so they still read against the black outline.
        void SetColor(Text t, int[] colors, int index)
        {
            int rgb = colors != null && index < colors.Length ? colors[index] : -1;
            Color c = Color.white;
            if (rgb >= 0)
            {
                c = new Color(((rgb >> 16) & 255) / 255f, ((rgb >> 8) & 255) / 255f, (rgb & 255) / 255f);
                float max = Mathf.Max(c.r, Mathf.Max(c.g, c.b));
                if (max < 0.35f)
                {
                    float lift = 0.35f - max;
                    c = new Color(c.r + lift, c.g + lift, c.b + lift);
                }
            }
            c.a = Opacity;
            t.color = c;
        }

        // --- Settings --------------------------------------------------------

        // Extra screens follow the settings of the one the panel edits.
        public void CopySettings(BiliDanmakuView from)
        {
            DanmakuOn = from.DanmakuOn; RollOn = from.RollOn; TopOn = from.TopOn; BottomOn = from.BottomOn; SubtitleOn = from.SubtitleOn;
            ScrollSpeed = from.ScrollSpeed; FontSize = from.FontSize; Opacity = from.Opacity; AreaRatio = from.AreaRatio; StayTime = from.StayTime;
        }

        public void ApplySettings()
        {
            Init();
            if (!DanmakuOn) HideAll();
            if (!RollOn) for (int i = 0; i < _roll.Length; i++) _roll[i].gameObject.SetActive(false);
            if (!TopOn) for (int i = 0; i < _top.Length; i++) _top[i].gameObject.SetActive(false);
            if (!BottomOn) for (int i = 0; i < _bottom.Length; i++) _bottom[i].gameObject.SetActive(false);
            for (int i = 0; i < _laneOwner.Length; i++) _laneOwner[i] = -1;
            SaveSettings();
        }

        void SaveSettings()
        {
            if (!_restored) return;
            PlayerData.SetBool(_saveKey + "On", DanmakuOn);
            PlayerData.SetBool(_saveKey + "Roll", RollOn);
            PlayerData.SetBool(_saveKey + "Top", TopOn);
            PlayerData.SetBool(_saveKey + "Bottom", BottomOn);
            PlayerData.SetBool(_saveKey + "Sub", SubtitleOn);
            PlayerData.SetFloat(_saveKey + "Speed", ScrollSpeed);
            PlayerData.SetFloat(_saveKey + "Font", FontSize);
            PlayerData.SetFloat(_saveKey + "Opacity", Opacity);
            PlayerData.SetFloat(_saveKey + "Area", AreaRatio);
            PlayerData.SetFloat(_saveKey + "Stay", StayTime);
        }

        public override void OnPlayerRestored(VRCPlayerApi player)
        {
            if (!player.isLocal) return;
            bool b;
            float f;
            if (PlayerData.TryGetBool(player, _saveKey + "On", out b)) DanmakuOn = b;
            if (PlayerData.TryGetBool(player, _saveKey + "Roll", out b)) RollOn = b;
            if (PlayerData.TryGetBool(player, _saveKey + "Top", out b)) TopOn = b;
            if (PlayerData.TryGetBool(player, _saveKey + "Bottom", out b)) BottomOn = b;
            if (PlayerData.TryGetBool(player, _saveKey + "Sub", out b)) SubtitleOn = b;
            if (PlayerData.TryGetFloat(player, _saveKey + "Speed", out f)) ScrollSpeed = f;
            if (PlayerData.TryGetFloat(player, _saveKey + "Font", out f)) FontSize = f;
            if (PlayerData.TryGetFloat(player, _saveKey + "Opacity", out f)) Opacity = f;
            if (PlayerData.TryGetFloat(player, _saveKey + "Area", out f)) AreaRatio = f;
            if (PlayerData.TryGetFloat(player, _saveKey + "Stay", out f)) StayTime = f;
            _restored = true;
            ApplySettings();
            SendCustomEventDelayedFrames(nameof(_NotifyRestored), 1);
        }

        [HideInInspector] public UdonSharpBehaviour SettingsListener;
        public void _NotifyRestored()
        {
            if (SettingsListener != null) SettingsListener.SendCustomEvent("_OnDanmakuSettingsChanged");
        }
    }
}
