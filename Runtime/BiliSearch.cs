using UdonSharp;
using UnityEngine;
using UnityEngine.UI;
using VRC.SDK3.Components;
using VRC.SDK3.Data;
using VRC.SDK3.Image;
using VRC.SDK3.Persistence;
using VRC.SDK3.StringLoading;
using VRC.SDKBase;
using VRC.Udon.Common.Interfaces;
using Yamadev.YamaStream;

namespace YamaBili
{
    // The "video search" page inside YamaPlayer's screen UI: Bilibili keyword / uploader / favorites lists,
    // the current video's parts, danmaku settings and search history. Results are played through the
    // YamaPlayer controller, so playback and sync are YamaPlayer's own.
    [UdonBehaviourSyncMode(BehaviourSyncMode.None)]
    public class BiliSearch : UdonSharpBehaviour
    {
        const int TabSearch = 0, TabUploader = 1, TabFavorites = 2, TabPages = 3, TabDanmaku = 4, TabHistory = 5;
        const int Rows = 20, HistoryMax = 10;
        const float RequestInterval = 5f;
        [SerializeField] string _saveKey = "YamaBili."; // PlayerData key prefix, set by the builder from BiliUrlPool.SaveKeyPrefix

        [SerializeField] Controller _controller;
        [SerializeField] UdonSharpBehaviour _uiController; // YamaPlayer UIController, read for its language
        [SerializeField] BiliUrlPool _pool;
        [SerializeField] BiliDanmaku _danmaku;
        [SerializeField] BiliDanmakuView _view;

        [Header("Layout")]
        [SerializeField] GameObject[] _tabViews;      // list, list, list, parts, danmaku, history
        [SerializeField] Toggle[] _tabToggles;        // YamaPlayer-style side menu, one toggle group
        [SerializeField] GameObject _inputBar;
        [SerializeField] VRCUrlInputField _input;
        [SerializeField] Text _hint;
        [SerializeField] Text _status;
        [SerializeField] Text _listTitle;

        [Header("Result rows")]
        [SerializeField] GameObject[] _rows;
        [SerializeField] Text[] _rowTitles;
        [SerializeField] Text[] _rowMeta;
        [SerializeField] Text[] _rowOwners;
        [SerializeField] RawImage[] _rowCovers;
        [SerializeField] Text _pageText;
        [SerializeField] GameObject _prevButton;
        [SerializeField] GameObject _nextButton;

        [Header("Parts rows")]
        [SerializeField] GameObject[] _partRows;
        [SerializeField] Text[] _partTitles;
        [SerializeField] GameObject[] _partMarks;     // pink "playing" icon per row
        [SerializeField] Text _partPageText;
        [SerializeField] Text _partHeader;
        [SerializeField] Toggle _autoNextOn;
        [SerializeField] Toggle _autoNextOff;

        [Header("History rows")]
        [SerializeField] GameObject[] _historyRows;
        [SerializeField] Text[] _historyTitles;

        [Header("Danmaku settings")]
        [SerializeField] Toggle[] _switchOn;    // danmaku, roll, top, bottom, subtitle: the "on" radio of each pair
        [SerializeField] Toggle[] _switchOff;
        [SerializeField] Text[] _settingValues; // speed, font, opacity, area, stay

        [Header("Localization")]
        [SerializeField] TextAsset _locale;
        [SerializeField] Text[] _labels;
        [SerializeField] string[] _labelKeys;

        [HideInInspector] public int _clickedIndex;

        DataDictionary _strings;
        string _language = "zh-CN";
        int _tab;

        // Current list
        string _listKind = "";
        string _listName = "";
        int _page, _pages, _self = -1, _prev = -1, _next = -1;
        int[] _itemSlot = new int[Rows];
        int[] _itemUp = new int[Rows];
        string[] _itemTitle = new string[Rows];
        string[] _itemBv = new string[Rows];
        int _itemCount;

        // Requests
        VRCUrl _pendingList;
        float _lastRequest = -100f;
        VRCImageDownloader _images;
        VRCUrl _pendingImage;
        Texture2D _atlas;

        // Parts paging
        int _partPage;

        // History: query slot + name + kind, newest first
        int[] _histSlot = new int[0];
        string[] _histName = new string[0];
        string[] _histKind = new string[0];
        bool _restored;

        // Danmaku defaults, for Reset
        bool _dOn, _dRoll, _dTop, _dBottom, _dSub;
        float _dSpeed, _dFont, _dOpacity, _dArea, _dStay;

        void Start()
        {
            _images = new VRCImageDownloader();
            LoadLocale();
            _dOn = _view.DanmakuOn; _dRoll = _view.RollOn; _dTop = _view.TopOn; _dBottom = _view.BottomOn; _dSub = _view.SubtitleOn;
            _dSpeed = _view.ScrollSpeed; _dFont = _view.FontSize; _dOpacity = _view.Opacity; _dArea = _view.AreaRatio; _dStay = _view.StayTime;
            ShowTab(TabSearch);
            ClearList();
        }

        void OnDestroy()
        {
            if (_images != null) _images.Dispose();
        }

        // --- Localization ------------------------------------------------------

        void LoadLocale()
        {
            if (_locale != null && VRCJson.TryDeserializeFromJson(_locale.text, out DataToken json) && json.TokenType == TokenType.DataDictionary)
                _strings = json.DataDictionary;
            else
                _strings = new DataDictionary();
            RefreshLanguage();
        }

        // Follows the language picked in YamaPlayer's settings; called whenever the page opens.
        public void RefreshLanguage()
        {
            string lang = null;
            if (_uiController != null)
            {
                object i18n = _uiController.GetProgramVariable("_i18n");
                if (i18n != null) lang = (string)((object[])i18n)[1];
            }
            if (string.IsNullOrEmpty(lang)) lang = VRCPlayerApi.GetCurrentLanguage();
            if (string.IsNullOrEmpty(lang)) lang = "en";
            if (lang.StartsWith("zh")) lang = "zh-CN";
            else if (!lang.StartsWith("ja")) lang = "en";
            else lang = "ja";
            _language = lang;
            for (int i = 0; i < _labels.Length && i < _labelKeys.Length; i++)
                if (_labels[i] != null) _labels[i].text = L(_labelKeys[i]);
            UpdateHint();
            UpdateListTitle();
            UpdateSettings();
            UpdateParts();
            UpdateHistory();
        }

        string L(string key)
        {
            if (_strings.TryGetValue(_language, TokenType.DataDictionary, out DataToken table) &&
                table.DataDictionary.TryGetValue(key, TokenType.String, out DataToken value))
                return value.String;
            return key;
        }

        void SetStatus(string key) => _status.text = string.IsNullOrEmpty(key) ? "" : L(key);

        // --- Tabs ---------------------------------------------------------------

        // Menu toggles fire on both on and off; only the one switched on counts.
        public void ShowSearch() => ShowIfOn(TabSearch);
        public void ShowUploader() => ShowIfOn(TabUploader);
        public void ShowFavorites() => ShowIfOn(TabFavorites);
        public void ShowParts() => ShowIfOn(TabPages);
        public void ShowDanmaku() => ShowIfOn(TabDanmaku);
        public void ShowHistory() => ShowIfOn(TabHistory);

        void ShowIfOn(int tab)
        {
            if (_tabToggles[tab].isOn) ShowTab(tab);
        }

        void ShowTab(int tab)
        {
            bool changedInput = tab != _tab && tab <= TabFavorites;
            _tab = tab;
            for (int i = 0; i < _tabViews.Length; i++)
            {
                // The three list tabs share one view.
                int owner = i <= TabFavorites ? TabSearch : i;
                bool active = (tab <= TabFavorites ? TabSearch : tab) == owner;
                if (_tabViews[i] != null && _tabViews[i].activeSelf != active) _tabViews[i].SetActive(active);
            }
            for (int i = 0; i < _tabToggles.Length; i++) _tabToggles[i].SetIsOnWithoutNotify(i == tab);
            _inputBar.SetActive(tab <= TabFavorites);
            if (tab <= TabFavorites)
            {
                if (changedInput) _input.SetUrl(InputPrefix());
                else RefillInput();
            }
            UpdateHint();
            if (tab == TabPages) UpdateParts();
            if (tab == TabDanmaku) UpdateSettings();
            if (tab == TabHistory) UpdateHistory();
        }

        VRCUrl InputPrefix() => _tab == TabUploader ? _pool.UploaderPrefix : _tab == TabFavorites ? _pool.FavoritesPrefix : _pool.SearchPrefix;

        // The field is filled at Start while the page is still hidden. In VRChat it can then hold the prefix
        // but show nothing, and setting the same value again is ignored, so it is cleared and set again
        // whenever the page opens and when the pointer comes over it. Anything typed after the prefix stays.
        public void RefillInput()
        {
            if (_tab > TabFavorites) return;
            VRCUrl url = _input.GetUrl();
            VRCUrl prefix = InputPrefix();
            string text = url == null ? "" : url.Get();
            if (text.Length > 0 && text != prefix.Get()) return;
            _input.SetUrl(VRCUrl.Empty);
            _input.SetUrl(prefix);
        }

        // Sent by the side bar button after the page is switched on; again a few frames later, once the
        // page's own field has come up.
        public void OpenPage()
        {
            RefreshLanguage();
            RefillInput();
            SendCustomEventDelayedFrames(nameof(RefillInput), 2);
        }

        void UpdateHint()
        {
            if (_hint == null) return;
            _hint.text = L(_tab == TabUploader ? "hintUploader" : _tab == TabFavorites ? "hintFavorites" : "hintSearch");
        }

        // --- Requests -------------------------------------------------------------

        public void Submit()
        {
            VRCUrl url = _input.GetUrl();
            VRCUrl prefix = InputPrefix();
            string text = url == null ? "" : url.Get();
            if (!text.StartsWith(prefix.Get()) || text.Substring(prefix.Get().Length).Trim().Length == 0)
            {
                SetStatus("badInput");
                _input.SetUrl(prefix);
                return;
            }
            RequestList(url);
        }

        void RequestList(VRCUrl url)
        {
            if (url == null) return;
            if (Time.time - _lastRequest < RequestInterval) { SetStatus("tooFast"); return; }
            _lastRequest = Time.time;
            _pendingList = url;
            SetStatus("loading");
            VRCStringDownloader.LoadUrl(url, (IUdonEventReceiver)this);
        }

        public void PrevPage() => RequestList(_pool.GetQ(_prev));
        public void NextPage() => RequestList(_pool.GetQ(_next));

        public override void OnStringLoadSuccess(IVRCStringDownload result)
        {
            if (_pendingList == null || result.Url.Get() != _pendingList.Get()) return;
            _pendingList = null;
            if (!VRCJson.TryDeserializeFromJson(result.Result, out DataToken json) || json.TokenType != TokenType.DataDictionary)
            {
                SetStatus("badResponse");
                return;
            }
            DataDictionary d = json.DataDictionary;
            if (d.TryGetValue("error", TokenType.String, out DataToken err)) { SetStatus(err.String); return; }
            ReadList(d);
        }

        public override void OnStringLoadError(IVRCStringDownload result)
        {
            if (_pendingList == null || result.Url.Get() != _pendingList.Get()) return;
            _pendingList = null;
            SetStatus("networkError");
        }

        void ReadList(DataDictionary d)
        {
            _listKind = Str(d, "kind");
            _listName = Str(d, "title");
            _page = Int(d, "page");
            _pages = Int(d, "pages");
            _self = Int(d, "self");
            _prev = Int(d, "prev");
            _next = Int(d, "next");
            DataList items = d.TryGetValue("items", TokenType.DataList, out DataToken t) ? t.DataList : new DataList();
            _itemCount = Mathf.Min(items.Count, Rows);
            for (int i = 0; i < Rows; i++)
            {
                bool has = i < _itemCount;
                _rows[i].SetActive(has);
                if (!has) continue;
                DataDictionary it = items[i].DataDictionary;
                _itemSlot[i] = Int(it, "i");
                _itemUp[i] = Int(it, "up");
                _itemTitle[i] = Str(it, "title");
                _itemBv[i] = Str(it, "bv");
                _rowTitles[i].text = _itemTitle[i];
                _rowOwners[i].text = Str(it, "owner");
                _rowMeta[i].text = Str(it, "dur");
                _rowCovers[i].texture = null;
                _rowCovers[i].color = new Color(0.15f, 0.15f, 0.15f, 1f);
                _rowCovers[i].uvRect = new Rect(0f, 1f - (i + 1f) / Rows, 1f, 1f / Rows);
            }
            UpdateListTitle();
            _pageText.text = _pages > 0 ? _page + " / " + _pages : "";
            _prevButton.SetActive(_prev >= 0);
            _nextButton.SetActive(_next >= 0);
            SetStatus(_itemCount == 0 ? "noResults" : "");
            AddHistory(_self, _listName, _listKind);

            VRCUrl img = _pool.GetQ(Int(d, "img"));
            if (img != null)
            {
                // Disposing the downloader frees the previous atlas texture.
                _images.Dispose();
                _images = new VRCImageDownloader();
                _pendingImage = img;
                _images.DownloadImage(img, null, (IUdonEventReceiver)this, null);
            }
        }

        void UpdateListTitle()
        {
            if (string.IsNullOrEmpty(_listKind)) return;
            string kindLabel = L(_listKind == "mid" ? "tabUploader" : _listKind == "fav" ? "tabFavorites" : "tabSearch");
            _listTitle.text = kindLabel + " · " + _listName;
        }

        void ClearList()
        {
            _itemCount = 0;
            for (int i = 0; i < _rows.Length; i++) _rows[i].SetActive(false);
            _listTitle.text = "";
            _pageText.text = "";
            _prevButton.SetActive(false);
            _nextButton.SetActive(false);
        }

        public override void OnImageLoadSuccess(IVRCImageDownload result)
        {
            if (_pendingImage == null || result.Url.Get() != _pendingImage.Get()) return;
            _pendingImage = null;
            _atlas = result.Result;
            for (int i = 0; i < _itemCount; i++)
            {
                _rowCovers[i].texture = _atlas;
                _rowCovers[i].color = Color.white;
            }
        }

        public override void OnImageLoadError(IVRCImageDownload result)
        {
            if (_pendingImage != null && result.Url.Get() == _pendingImage.Get()) _pendingImage = null;
        }

        // --- Row actions -------------------------------------------------------------

        bool CanControl()
        {
            if ((int)_controller.PlayerPermission >= (int)PlayerPermission.Editor) return true;
            SetStatus("noPermission");
            return false;
        }

        // Track is an object[] in Udon and cannot be compared with null, so callers check HasItemUrl first.
        bool HasItemUrl(int i) => _pool.GetV(_itemSlot[i]) != null;

        Track ItemTrack(int i)
        {
            VRCUrl url = _pool.GetV(_itemSlot[i]);
            return Track.New(VideoPlayerType.AVProVideoPlayer, _itemTitle[i], url, "https://www.bilibili.com/video/" + _itemBv[i]);
        }

        public void _PlayItem()
        {
            int i = _clickedIndex;
            if (i < 0 || i >= _itemCount || !HasItemUrl(i) || !CanControl()) return;
            _controller.TakeOwnership();
            _controller.PlayTrack(ItemTrack(i));
            SetStatus("");
        }

        public void _QueueItem()
        {
            int i = _clickedIndex;
            if (i < 0 || i >= _itemCount || !HasItemUrl(i) || !CanControl()) return;
            Playlist queue = _controller.Queue;
            queue.TakeOwnership();
            queue.AddTrack(ItemTrack(i));
            SetStatus("queued");
        }

        public void _OpenUploader()
        {
            int i = _clickedIndex;
            if (i < 0 || i >= _itemCount) return;
            RequestList(_pool.GetQ(_itemUp[i]));
        }

        // Queues every result on the current page, in order.
        public void QueueAll()
        {
            if (_itemCount == 0 || !CanControl()) return;
            Playlist queue = _controller.Queue;
            queue.TakeOwnership();
            for (int i = 0; i < _itemCount; i++)
            {
                if (HasItemUrl(i)) queue.AddTrack(ItemTrack(i));
            }
            SetStatus("queued");
        }

        // --- Parts ---------------------------------------------------------------------

        public void _OnVideoInfoChanged()
        {
            if (!string.IsNullOrEmpty(_danmaku.Status) && _danmaku.Status != "loading") SetStatus(_danmaku.Status);
            _partPage = _danmaku.PageCurrent >= 0 ? _danmaku.PageCurrent / Rows : 0;
            UpdateParts();
        }

        void UpdateParts()
        {
            if (_partRows == null || _partRows.Length == 0) return;
            int n = _danmaku.PageTitles.Length;
            int pages = Mathf.Max(1, (n + Rows - 1) / Rows);
            _partPage = Mathf.Clamp(_partPage, 0, pages - 1);
            for (int r = 0; r < _partRows.Length; r++)
            {
                int i = _partPage * Rows + r;
                bool has = i < n;
                _partRows[r].SetActive(has);
                if (!has) continue;
                _partTitles[r].text = _danmaku.PageTitles[i];
                _partMarks[r].SetActive(i == _danmaku.PageCurrent);
            }
            _partHeader.text = n == 0 ? L("noPages") : _danmaku.Title;
            _partPageText.text = n > Rows ? (_partPage + 1) + " / " + pages : "";
            _autoNextOn.SetIsOnWithoutNotify(_danmaku.AutoNext);
            _autoNextOff.SetIsOnWithoutNotify(!_danmaku.AutoNext);
        }

        public void _PlayPart()
        {
            if (!CanControl()) return;
            _danmaku.PlayPage(_partPage * Rows + _clickedIndex);
        }

        public void PrevParts() { _partPage--; UpdateParts(); }
        public void NextParts() { _partPage++; UpdateParts(); }
        public void _OnAutoNextChanged() => _danmaku.AutoNext = _autoNextOn.isOn;

        // --- History ---------------------------------------------------------------------

        void AddHistory(int slot, string name, string kind)
        {
            if (slot < 0 || string.IsNullOrEmpty(name)) return;
            LoadHistory(Networking.LocalPlayer); // the other screen's panel may have added to it
            int n = Mathf.Min(_histSlot.Length + 1, HistoryMax);
            int[] slots = new int[n];
            string[] names = new string[n];
            string[] kinds = new string[n];
            slots[0] = slot; names[0] = name; kinds[0] = kind;
            int k = 1;
            for (int i = 0; i < _histSlot.Length && k < n; i++)
            {
                if (_histName[i] == name && _histKind[i] == kind) continue;
                slots[k] = _histSlot[i]; names[k] = _histName[i]; kinds[k] = _histKind[i];
                k++;
            }
            if (k < n)
            {
                int[] s2 = new int[k]; string[] n2 = new string[k]; string[] k2 = new string[k];
                System.Array.Copy(slots, s2, k); System.Array.Copy(names, n2, k); System.Array.Copy(kinds, k2, k);
                slots = s2; names = n2; kinds = k2;
            }
            _histSlot = slots; _histName = names; _histKind = kinds;
            SaveHistory();
            if (_tab == TabHistory) UpdateHistory();
        }

        void UpdateHistory()
        {
            if (_restored) LoadHistory(Networking.LocalPlayer);
            for (int r = 0; r < _historyRows.Length; r++)
            {
                bool has = r < _histSlot.Length;
                _historyRows[r].SetActive(has);
                if (!has) continue;
                string kind = L(_histKind[r] == "mid" ? "tabUploader" : _histKind[r] == "fav" ? "tabFavorites" : "tabSearch");
                _historyTitles[r].text = kind + " · " + _histName[r];
            }
        }

        // Query slots are recycled by the server; the list that comes back carries its own title,
        // so a recycled slot shows up as a different list rather than silently replacing the entry.
        public void _OpenHistory()
        {
            int i = _clickedIndex;
            if (i < 0 || i >= _histSlot.Length) return;
            ShowTab(_histKind[i] == "mid" ? TabUploader : _histKind[i] == "fav" ? TabFavorites : TabSearch);
            RequestList(_pool.GetQ(_histSlot[i]));
        }

        public void ClearHistory()
        {
            _histSlot = new int[0]; _histName = new string[0]; _histKind = new string[0];
            SaveHistory();
            UpdateHistory();
        }

        void SaveHistory()
        {
            DataList list = new DataList();
            for (int i = 0; i < _histSlot.Length; i++)
            {
                DataDictionary e = new DataDictionary();
                e.SetValue("q", _histSlot[i]);
                e.SetValue("n", _histName[i]);
                e.SetValue("k", _histKind[i]);
                list.Add(e);
            }
            if (VRCJson.TrySerializeToJson(list, JsonExportType.Minify, out DataToken json))
                PlayerData.SetString(_saveKey + "History", json.String);
        }

        public override void OnPlayerRestored(VRCPlayerApi player)
        {
            if (!player.isLocal) return;
            _restored = true;
            UpdateHistory();
        }

        // Every screen's panel shares the one saved history, so it is read back before each use.
        void LoadHistory(VRCPlayerApi player)
        {
            if (!_restored || !Utilities.IsValid(player)) return;
            if (!PlayerData.TryGetString(player, _saveKey + "History", out string saved)) return;
            if (!VRCJson.TryDeserializeFromJson(saved, out DataToken json) || json.TokenType != TokenType.DataList) return;
            DataList list = json.DataList;
            int n = Mathf.Min(list.Count, HistoryMax);
            _histSlot = new int[n]; _histName = new string[n]; _histKind = new string[n];
            for (int i = 0; i < n; i++)
            {
                DataDictionary e = list[i].DataDictionary;
                _histSlot[i] = Int(e, "q");
                _histName[i] = Str(e, "n");
                _histKind[i] = Str(e, "k");
            }
        }

        // --- Danmaku settings ---------------------------------------------------------------

        public void _OnSwitchChanged()
        {
            _view.DanmakuOn = _switchOn[0].isOn;
            _view.RollOn = _switchOn[1].isOn;
            _view.TopOn = _switchOn[2].isOn;
            _view.BottomOn = _switchOn[3].isOn;
            _view.SubtitleOn = _switchOn[4].isOn;
            _danmaku.RefreshViews();
        }
        public void SpeedDown() { _view.ScrollSpeed = Mathf.Max(60f, _view.ScrollSpeed - 40f); ApplyDanmaku(); }
        public void SpeedUp() { _view.ScrollSpeed = Mathf.Min(600f, _view.ScrollSpeed + 40f); ApplyDanmaku(); }
        public void FontDown() { _view.FontSize = Mathf.Max(20f, _view.FontSize - 4f); ApplyDanmaku(); }
        public void FontUp() { _view.FontSize = Mathf.Min(64f, _view.FontSize + 4f); ApplyDanmaku(); }
        public void OpacityDown() { _view.Opacity = Mathf.Max(0.2f, _view.Opacity - 0.1f); ApplyDanmaku(); }
        public void OpacityUp() { _view.Opacity = Mathf.Min(1f, _view.Opacity + 0.1f); ApplyDanmaku(); }
        public void AreaDown() { _view.AreaRatio = Mathf.Max(0.25f, _view.AreaRatio - 0.25f); ApplyDanmaku(); }
        public void AreaUp() { _view.AreaRatio = Mathf.Min(1f, _view.AreaRatio + 0.25f); ApplyDanmaku(); }
        public void StayDown() { _view.StayTime = Mathf.Max(2f, _view.StayTime - 1f); ApplyDanmaku(); }
        public void StayUp() { _view.StayTime = Mathf.Min(10f, _view.StayTime + 1f); ApplyDanmaku(); }

        public void ResetDanmaku()
        {
            _view.DanmakuOn = _dOn; _view.RollOn = _dRoll; _view.TopOn = _dTop; _view.BottomOn = _dBottom; _view.SubtitleOn = _dSub;
            _view.ScrollSpeed = _dSpeed; _view.FontSize = _dFont; _view.Opacity = _dOpacity; _view.AreaRatio = _dArea; _view.StayTime = _dStay;
            ApplyDanmaku();
        }

        // Updates every screen, and every panel's values (this one included) through _OnDanmakuSettingsChanged.
        void ApplyDanmaku() => _danmaku.RefreshViews();

        public void _OnDanmakuSettingsChanged() => UpdateSettings();

        void UpdateSettings()
        {
            if (_settingValues == null || _settingValues.Length < 5) return;
            bool[] on = { _view.DanmakuOn, _view.RollOn, _view.TopOn, _view.BottomOn, _view.SubtitleOn };
            for (int i = 0; i < _switchOn.Length; i++)
            {
                _switchOn[i].SetIsOnWithoutNotify(on[i]);
                _switchOff[i].SetIsOnWithoutNotify(!on[i]);
            }
            _settingValues[0].text = Mathf.RoundToInt(_view.ScrollSpeed).ToString();
            _settingValues[1].text = Mathf.RoundToInt(_view.FontSize).ToString();
            _settingValues[2].text = Mathf.RoundToInt(_view.Opacity * 100f) + "%";
            _settingValues[3].text = Mathf.RoundToInt(_view.AreaRatio * 100f) + "%";
            _settingValues[4].text = Mathf.RoundToInt(_view.StayTime) + "s";
        }

        // --- JSON helpers ----------------------------------------------------------------------

        static string Str(DataDictionary d, string key)
        {
            return d.TryGetValue(key, TokenType.String, out DataToken t) ? t.String : "";
        }

        static int Int(DataDictionary d, string key)
        {
            if (d.TryGetValue(key, out DataToken t))
            {
                if (t.TokenType == TokenType.Double) return (int)t.Double;
                if (t.TokenType == TokenType.Int) return t.Int;
            }
            return -1;
        }
    }
}
