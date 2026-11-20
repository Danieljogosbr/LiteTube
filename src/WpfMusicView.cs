#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Threading;

namespace LiteTube
{
    /// <summary>
    /// Camada 1 (UI WPF Pura) — Ambiente YouTube Music.
    /// Conecta diretamente AudioEngine + WpfMediaListControl + WpfMusicPlayerBar + Playlists da conta music.youtube.com.
    /// </summary>
    public sealed class WpfMusicView : UserControl, IDisposable
    {
        private readonly AppConfig _config;
        private readonly YouTubeService _ytService;
        private readonly AudioEngine _audioEngine;
        private readonly DispatcherTimer _uiTimer;

        private ColumnDefinition _sidebarColumn = null!;
        private Border _sidebarPanel = null!;
        private Border _miniSidebarPanel = null!;
        private Button _navExplore = null!;
        private Button _navHome = null!;
        private Button _navAccountPlaylists = null!;
        private Button _navFavorites = null!;
        private Button _navQueue = null!;
        private Button _miniHome = null!;
        private Button _miniExplore = null!;
        private Button _miniPlaylists = null!;
        private Button _miniFavorites = null!;
        private Button _miniQueue = null!;
        private Button _btnNewPlaylist = null!;
        private Button _btnImportPlaylist = null!;
        private ListBox _lstLocalPlaylists = null!;
        private Button _btnBackToPlaylists = null!;
        private TextBlock _lblMusicHeader = null!;
        private Button _btnShufflePlayAll = null!;
        private Button _btnPlayAll = null!;
        private ContentControl _musicListHost = null!;
        private ContentControl _musicBarHost = null!;

        private WpfMediaListControl _trackList = null!;
        private WpfMusicPlayerBar _playerBar = null!;

        private bool _sidebarExpanded = true;
        private bool _initialLoaded = false;
        private string _activeSection = "home";
        private string _searchContinuationToken = "";
        private string _accountPlaylistsContinuationToken = "";
        private string _openedAccountPlaylistContinuationToken = "";
        private TrackItem? _openedAccountPlaylistCard;
        private bool _isLoadingMore = false;
        private List<TrackItem> _displayedTracks = new List<TrackItem>();
        private List<TrackItem> _accountPlaylists = new List<TrackItem>();

        public event Action<string>? StatusNotification;
        public event Action<TrackItem>? WatchClipRequested;
        public event Action<TrackItem>? ChannelClicked;
        public event Action? AccountStatusMayHaveChanged;

        public WpfMediaListControl TrackList => _trackList;
        public WpfMusicPlayerBar PlayerBar => _playerBar;

        public WpfMusicView(AppConfig config, YouTubeService ytService, AudioEngine audioEngine)
        {
            _config = config;
            _ytService = ytService;
            _audioEngine = audioEngine;

            LoadXaml();
            BuildControls();
            WireEvents();
            WireAudioEngineEvents();
            RefreshLocalPlaylistsList();
            UpdateNavSelection();

            _uiTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
            _uiTimer.Tick += OnUiTick;
            _uiTimer.Start();
        }

        private void LoadXaml()
        {
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            string p1 = Path.Combine(baseDir, "src", "WpfMusicView.xaml");
            string p2 = Path.Combine(baseDir, "WpfMusicView.xaml");
            string? xamlPath = File.Exists(p1) ? p1 : (File.Exists(p2) ? p2 : null);

            FrameworkElement root;
            if (xamlPath != null)
            {
                using var fs = new FileStream(xamlPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                root = (FrameworkElement)XamlReader.Load(fs);
            }
            else
            {
                root = new Grid();
            }

            Content = root;

            _sidebarColumn = (ColumnDefinition)root.FindName("MusicSidebarColumn");
            _sidebarPanel = (Border)root.FindName("MusicSidebarPanel");
            _miniSidebarPanel = (Border)root.FindName("MusicMiniSidebarPanel");
            _navExplore = (Button)root.FindName("NavMusicExplore");
            _navHome = (Button)root.FindName("NavMusicHome");
            _navAccountPlaylists = (Button)root.FindName("NavMusicAccountPlaylists");
            _navFavorites = (Button)root.FindName("NavMusicFavorites");
            _navQueue = (Button)root.FindName("NavMusicQueue");
            _miniHome = (Button)root.FindName("MiniMusicHome");
            _miniExplore = (Button)root.FindName("MiniMusicExplore");
            _miniPlaylists = (Button)root.FindName("MiniMusicPlaylists");
            _miniFavorites = (Button)root.FindName("MiniMusicFavorites");
            _miniQueue = (Button)root.FindName("MiniMusicQueue");
            _btnNewPlaylist = (Button)root.FindName("BtnNewPlaylist");
            _btnImportPlaylist = (Button)root.FindName("BtnImportPlaylist");
            _lstLocalPlaylists = (ListBox)root.FindName("LstLocalPlaylists");
            _btnBackToPlaylists = (Button)root.FindName("BtnBackToMusicPlaylists");
            _lblMusicHeader = (TextBlock)root.FindName("LblMusicHeader");
            _btnShufflePlayAll = (Button)root.FindName("BtnShufflePlayAll");
            _btnPlayAll = (Button)root.FindName("BtnPlayAll");
            _musicListHost = (ContentControl)root.FindName("MusicListHost");
            _musicBarHost = (ContentControl)root.FindName("MusicBarHost");
        }

        private void BuildControls()
        {
            _trackList = new WpfMediaListControl
            {
                IsGridMode = false,
                IsMusicEnvironment = true,
                SingleClickActivates = false,
                IsFavoriteOrSavedCheck = id => _config.IsFavorite(id)
            };
            _trackList.SetAccentColor(255, 0, 51);
            _musicListHost.Content = _trackList;

            _playerBar = new WpfMusicPlayerBar();
            _playerBar.UpdateAudioVisuals(_config.MusicVolume, _config.IsMusicMuted);
            _playerBar.UpdateShuffleAndRepeatVisuals(_config.Shuffle, _config.Repeat);
            _musicBarHost.Content = _playerBar;
        }

        private void WireEvents()
        {
            _navHome.Click += async (s, e) => await LoadRecommendedAsync();
            _miniHome.Click += async (s, e) => await LoadRecommendedAsync();
            _navExplore.Click += async (s, e) => await LoadExploreAsync();
            _miniExplore.Click += async (s, e) => await LoadExploreAsync();
            _navAccountPlaylists.Click += async (s, e) => await LoadAccountPlaylistsAsync(false);
            _miniPlaylists.Click += async (s, e) => await LoadAccountPlaylistsAsync(false);
            _navFavorites.Click += (s, e) => ShowFavorites();
            _miniFavorites.Click += (s, e) => ShowFavorites();
            _navQueue.Click += (s, e) => ShowQueue();
            _miniQueue.Click += (s, e) => ShowQueue();

            _btnBackToPlaylists.Click += async (s, e) => await LoadAccountPlaylistsAsync(false);
            _btnNewPlaylist.Click += (s, e) => CreateNewPlaylist();
            _btnImportPlaylist.Click += async (s, e) => await ImportPlaylistByUrlAsync();

            _lstLocalPlaylists.SelectionChanged += (s, e) =>
            {
                int idx = _lstLocalPlaylists.SelectedIndex;
                if (idx >= 0 && idx < _config.Playlists.Count)
                {
                    var pl = _config.Playlists[idx];
                    _activeSection = "local_playlist";
                    _openedAccountPlaylistCard = null;
                    _searchContinuationToken = "";
                    _btnBackToPlaylists.Visibility = Visibility.Collapsed;
                    SetListModeForTracks();
                    UpdateNavSelection();
                    _displayedTracks = new List<TrackItem>(pl.Tracks);
                    _lblMusicHeader.Text = string.Format("Playlist Local: {0} ({1} faixas)", pl.Name, pl.Tracks.Count);
                    _trackList.HasMoreItems = false;
                    _trackList.SetItems(_displayedTracks, true);
                }
            };

            _btnPlayAll.Click += (s, e) =>
            {
                if (_activeSection == "account_playlists") return;
                if (_displayedTracks.Count > 0)
                {
                    int idx = _trackList.SelectedIndex >= 0 ? _trackList.SelectedIndex : 0;
                    _audioEngine.SetQueueAndPlay(_displayedTracks, idx);
                }
            };

            _btnShufflePlayAll.Click += (s, e) =>
            {
                if (_activeSection == "account_playlists" || _displayedTracks.Count == 0) return;
                _config.Shuffle = true;
                _config.Save();
                _playerBar.UpdateShuffleAndRepeatVisuals(_config.Shuffle, _config.Repeat);
                int startIdx = new Random().Next(0, _displayedTracks.Count);
                _audioEngine.SetQueueAndPlay(_displayedTracks, startIdx);
            };

            _trackList.ItemActivated += async (idx, item) =>
            {
                if (item == null) return;
                if (item.IsPlaylistCard)
                {
                    await OpenAccountPlaylistAsync(item);
                    return;
                }
                if (item.IsChannel)
                {
                    ChannelClicked?.Invoke(item);
                    return;
                }
                _audioEngine.SetQueueAndPlay(_displayedTracks, idx);
            };
            _trackList.FavoriteClicked += (idx, track) =>
            {
                if (track == null || track.IsPlaylistCard) return;
                bool fav = _config.ToggleFavorite(track);
                _trackList.RefreshVisuals();
                if (_audioEngine.CurrentTrack != null && _audioEngine.CurrentTrack.Id == track.Id)
                    _playerBar.UpdateFavoriteVisual(fav);
                StatusNotification?.Invoke((fav ? "Adicionado aos favoritos: " : "Removido dos favoritos: ") + track.Title);
            };
            _trackList.AddToQueueClicked += (idx, track) =>
            {
                if (track == null || track.IsPlaylistCard) return;
                _audioEngine.AddToQueue(track);
                StatusNotification?.Invoke("Adicionado à fila de música: " + track.Title);
            };
            _trackList.WatchClipClicked += (idx, track) =>
            {
                if (track != null && !track.IsPlaylistCard) WatchClipRequested?.Invoke(track);
            };
            _trackList.ChannelClicked += item =>
            {
                if (item != null) ChannelClicked?.Invoke(item);
            };
            _trackList.LoadMoreRequested += async () => await LoadMoreAsync();

            // Player Bar events
            _playerBar.PlayPauseRequested += () => _audioEngine.TogglePlayPause();
            _playerBar.NextRequested += () => _audioEngine.PlayNext();
            _playerBar.PreviousRequested += () => _audioEngine.PlayPrevious();
            _playerBar.SeekRequested += sec => _audioEngine.SeekToSeconds(sec);
            _playerBar.VolumeChanged += vol =>
            {
                _audioEngine.Volume = vol;
                _config.Save();
                _playerBar.UpdateAudioVisuals(_audioEngine.Volume, _audioEngine.IsMuted);
            };
            _playerBar.MuteToggleRequested += () =>
            {
                _audioEngine.IsMuted = !_audioEngine.IsMuted;
                _config.Save();
                _playerBar.UpdateAudioVisuals(_audioEngine.Volume, _audioEngine.IsMuted);
            };
            _playerBar.ShuffleToggleRequested += () =>
            {
                _config.Shuffle = !_config.Shuffle;
                _config.Save();
                _playerBar.UpdateShuffleAndRepeatVisuals(_config.Shuffle, _config.Repeat);
            };
            _playerBar.RepeatToggleRequested += () =>
            {
                _config.Repeat = (_config.Repeat + 1) % 3;
                _config.Save();
                _playerBar.UpdateShuffleAndRepeatVisuals(_config.Shuffle, _config.Repeat);
            };
            _playerBar.LikeToggleRequested += () =>
            {
                var cur = _audioEngine.CurrentTrack;
                if (cur == null) return;
                bool fav = _config.ToggleFavorite(cur);
                _playerBar.UpdateFavoriteVisual(fav);
                _trackList.RefreshVisuals();
            };
            _playerBar.QueueToggleRequested += () => ShowQueue();
        }

        private void WireAudioEngineEvents()
        {
            _audioEngine.TrackChanged += track =>
            {
                _ = Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (track == null) return;
                    _trackList.PlayingVideoId = track.Id;
                    _playerBar.UpdateTrackMetadata(track, _config.IsFavorite(track.Id));
                    string thumb = !string.IsNullOrWhiteSpace(track.CustomThumbUrl)
                        ? track.CustomThumbUrl
                        : ("https://i.ytimg.com/vi/" + track.Id + "/mqdefault.jpg");
                    _playerBar.UpdateThumbnailFromUrl(thumb);
                }));
            };

            _audioEngine.OfftopicSegmentsLoaded += segs =>
            {
                _ = Dispatcher.BeginInvoke(new Action(() => _playerBar.SetOfftopicSegments(segs)));
            };

            _audioEngine.StatusMessage += msg =>
            {
                _ = Dispatcher.BeginInvoke(new Action(() => StatusNotification?.Invoke(msg)));
            };
        }

        private void OnUiTick(object? sender, EventArgs e)
        {
            if (_playerBar.IsUserDraggingScrubber) return;
            double cur = _audioEngine.CurrentPositionSeconds;
            double total = _audioEngine.TotalDurationSeconds;
            _playerBar.UpdatePlaybackState(_audioEngine.IsPlaying, cur, total);
        }

        public void ToggleSidebar()
        {
            _sidebarExpanded = !_sidebarExpanded;
            _sidebarColumn.Width = _sidebarExpanded ? new GridLength(216) : new GridLength(72);
            _sidebarPanel.Visibility = _sidebarExpanded ? Visibility.Visible : Visibility.Collapsed;
            _miniSidebarPanel.Visibility = _sidebarExpanded ? Visibility.Collapsed : Visibility.Visible;
        }

        public void OnAuthChanged()
        {
            _accountPlaylists.Clear();
            _accountPlaylistsContinuationToken = "";
            if (_activeSection == "account_playlists")
            {
                _ = LoadAccountPlaylistsAsync(true);
            }
            else if (_activeSection == "home")
            {
                _ = LoadRecommendedAsync();
            }
        }

        public async Task EnsureInitialFeedLoadedAsync()
        {
            if (_initialLoaded) return;
            _initialLoaded = true;
            await LoadRecommendedAsync();
        }

        public void RefreshLocalPlaylistsList()
        {
            _lstLocalPlaylists.Items.Clear();
            foreach (var pl in _config.Playlists)
            {
                _lstLocalPlaylists.Items.Add(string.Format("{0} ({1})", pl.Name, pl.Tracks.Count));
            }
        }

        public async Task SearchAsync(string query)
        {
            if (string.IsNullOrWhiteSpace(query)) return;
            _activeSection = "search";
            _openedAccountPlaylistCard = null;
            _lstLocalPlaylists.SelectedIndex = -1;
            _searchContinuationToken = "";
            _btnBackToPlaylists.Visibility = Visibility.Collapsed;
            SetListModeForTracks();
            UpdateNavSelection();

            _lblMusicHeader.Text = string.Format("Buscando \"{0}\" no YouTube Music...", query.Trim());
            _trackList.IsLoadingSkeleton = true;
            try
            {
                var page = await _ytService.SearchPageAsync(query.Trim(), "music", _config);
                _displayedTracks = page.Items ?? new List<TrackItem>();
                _searchContinuationToken = page.ContinuationToken ?? "";
                _lblMusicHeader.Text = string.Format("Música: \"{0}\" ({1} faixas)", query.Trim(), _displayedTracks.Count);
                _trackList.HasMoreItems = !string.IsNullOrEmpty(_searchContinuationToken);
                _trackList.SetItems(_displayedTracks, true);
            }
            finally
            {
                _trackList.IsLoadingSkeleton = false;
            }
        }

        public async Task RefreshCurrentAsync()
        {
            if (_activeSection == "explore") await LoadExploreAsync();
            else if (_activeSection == "account_playlists") await LoadAccountPlaylistsAsync(true);
            else if (_activeSection == "account_playlist_items" && _openedAccountPlaylistCard != null) await OpenAccountPlaylistAsync(_openedAccountPlaylistCard);
            else if (_activeSection == "favorites") ShowFavorites();
            else if (_activeSection == "queue") ShowQueue();
            else await LoadRecommendedAsync();
        }

        private async Task LoadAccountPlaylistsAsync(bool forceRefresh)
        {
            _activeSection = "account_playlists";
            _openedAccountPlaylistCard = null;
            _openedAccountPlaylistContinuationToken = "";
            _lstLocalPlaylists.SelectedIndex = -1;
            _btnBackToPlaylists.Visibility = Visibility.Collapsed;
            _btnPlayAll.Visibility = Visibility.Collapsed;
            _btnShufflePlayAll.Visibility = Visibility.Collapsed;
            UpdateNavSelection();

            // Modo Grade de Cards para exibir suas playlists do YouTube Music igual ao site oficial
            _trackList.IsGridMode = true;
            _trackList.IsMusicEnvironment = false;
            _trackList.SingleClickActivates = true;

            _lblMusicHeader.Text = "Carregando suas playlists do YouTube Music...";
            _trackList.IsLoadingSkeleton = true;

            try
            {
                var page = await YouTubeAccountFeedService.Shared.FetchMusicAccountPlaylistsAsync(_config, forceRefresh);
                AccountStatusMayHaveChanged?.Invoke();

                if (page.Items.Count > 0)
                {
                    _accountPlaylists = page.Items;
                    _accountPlaylistsContinuationToken = page.ContinuationToken ?? "";
                    _lblMusicHeader.Text = string.Format("Suas Playlists no YouTube Music ({0})", _accountPlaylists.Count);
                    _trackList.HasMoreItems = !string.IsNullOrEmpty(_accountPlaylistsContinuationToken);
                    _trackList.SetItems(_accountPlaylists, true);
                    StatusNotification?.Invoke(string.Format("{0} playlist(s) do YouTube Music carregadas!", _accountPlaylists.Count));
                }
                else
                {
                    string msg = !string.IsNullOrEmpty(page.StatusMessage)
                        ? page.StatusMessage
                        : "Nenhuma playlist salva encontrada na conta conectada.";
                    _lblMusicHeader.Text = "Playlists (Conta) • " + msg;
                    _trackList.HasMoreItems = false;
                    _trackList.SetItems(new List<TrackItem>(), true);
                    StatusNotification?.Invoke(msg);
                }
            }
            finally
            {
                _trackList.IsLoadingSkeleton = false;
            }
        }

        private async Task OpenAccountPlaylistAsync(TrackItem playlistCard)
        {
            if (playlistCard == null || string.IsNullOrEmpty(playlistCard.PlaylistId)) return;

            _activeSection = "account_playlist_items";
            _openedAccountPlaylistCard = playlistCard;
            _openedAccountPlaylistContinuationToken = "";
            _btnBackToPlaylists.Visibility = Visibility.Visible;
            SetListModeForTracks();
            UpdateNavSelection();

            _lblMusicHeader.Text = string.Format("Carregando faixas de \"{0}\"...", playlistCard.Title);
            _trackList.IsLoadingSkeleton = true;

            try
            {
                var page = await YouTubeAccountFeedService.Shared.FetchMusicPlaylistItemsAsync(playlistCard.PlaylistId, _config);
                if (page.Items.Count > 0)
                {
                    _displayedTracks = page.Items;
                    _openedAccountPlaylistContinuationToken = page.ContinuationToken ?? "";
                }
                else
                {
                    var fb = await _ytService.FetchUrlOrPlaylistAsync(playlistCard.PlaylistId, _config, false);
                    _displayedTracks = fb.Item1 ?? new List<TrackItem>();
                }

                _lblMusicHeader.Text = string.Format("{0} ({1} faixas)", playlistCard.Title, _displayedTracks.Count);
                _trackList.HasMoreItems = !string.IsNullOrEmpty(_openedAccountPlaylistContinuationToken);
                _trackList.SetItems(_displayedTracks, true);
                StatusNotification?.Invoke(string.Format("Playlist '{0}' aberta com {1} faixas.", playlistCard.Title, _displayedTracks.Count));
            }
            finally
            {
                _trackList.IsLoadingSkeleton = false;
            }
        }

        private void SetListModeForTracks()
        {
            _trackList.IsGridMode = false;
            _trackList.IsMusicEnvironment = true;
            _trackList.SingleClickActivates = false;
            _btnPlayAll.Visibility = Visibility.Visible;
            _btnShufflePlayAll.Visibility = Visibility.Visible;
        }

        private async Task LoadRecommendedAsync()
        {
            _activeSection = "home";
            _openedAccountPlaylistCard = null;
            _lstLocalPlaylists.SelectedIndex = -1;
            _searchContinuationToken = "";
            _btnBackToPlaylists.Visibility = Visibility.Collapsed;
            SetListModeForTracks();
            UpdateNavSelection();

            _lblMusicHeader.Text = "Carregando recomendados do YouTube Music...";
            _trackList.IsLoadingSkeleton = true;
            try
            {
                var res = await _ytService.FetchPersonalizedMusicFeedAsync(_config);
                _displayedTracks = res.Item1 ?? new List<TrackItem>();
                _lblMusicHeader.Text = string.Format("Recomendados ({0} faixas)", _displayedTracks.Count);
                _trackList.HasMoreItems = false;
                _trackList.SetItems(_displayedTracks, true);
            }
            finally
            {
                _trackList.IsLoadingSkeleton = false;
            }
        }

        private async Task LoadExploreAsync()
        {
            _activeSection = "explore";
            _openedAccountPlaylistCard = null;
            _lstLocalPlaylists.SelectedIndex = -1;
            _searchContinuationToken = "";
            _btnBackToPlaylists.Visibility = Visibility.Collapsed;
            SetListModeForTracks();
            UpdateNavSelection();

            _lblMusicHeader.Text = "Carregando Explorar / Em Alta no YouTube Music...";
            _trackList.IsLoadingSkeleton = true;
            try
            {
                var res = await _ytService.FetchMusicExploreFeedAsync(_config);
                _displayedTracks = res.Item1 ?? new List<TrackItem>();
                _lblMusicHeader.Text = string.Format("Explorar ({0} faixas)", _displayedTracks.Count);
                _trackList.HasMoreItems = false;
                _trackList.SetItems(_displayedTracks, true);
            }
            finally
            {
                _trackList.IsLoadingSkeleton = false;
            }
        }

        private void ShowFavorites()
        {
            _activeSection = "favorites";
            _openedAccountPlaylistCard = null;
            _lstLocalPlaylists.SelectedIndex = -1;
            _searchContinuationToken = "";
            _btnBackToPlaylists.Visibility = Visibility.Collapsed;
            SetListModeForTracks();
            UpdateNavSelection();

            _displayedTracks = new List<TrackItem>(_config.Favorites);
            _lblMusicHeader.Text = string.Format("Seus Favoritos ({0} faixas)", _displayedTracks.Count);
            _trackList.HasMoreItems = false;
            _trackList.SetItems(_displayedTracks, true);
        }

        private void ShowQueue()
        {
            _activeSection = "queue";
            _openedAccountPlaylistCard = null;
            _lstLocalPlaylists.SelectedIndex = -1;
            _searchContinuationToken = "";
            _btnBackToPlaylists.Visibility = Visibility.Collapsed;
            SetListModeForTracks();
            UpdateNavSelection();

            _displayedTracks = new List<TrackItem>(_audioEngine.Queue);
            _lblMusicHeader.Text = string.Format("Fila de Reprodução Atual ({0} faixas)", _displayedTracks.Count);
            _trackList.HasMoreItems = false;
            _trackList.SetItems(_displayedTracks, true);
        }

        private async Task LoadMoreAsync()
        {
            if (_isLoadingMore) return;

            if (_activeSection == "account_playlists" && !string.IsNullOrEmpty(_accountPlaylistsContinuationToken))
            {
                _isLoadingMore = true;
                _trackList.IsLoadingMore = true;
                try
                {
                    var next = await YouTubeAccountFeedService.Shared.FetchMusicAccountPlaylistsAsync(_config, true, _accountPlaylistsContinuationToken);
                    _accountPlaylistsContinuationToken = next.ContinuationToken ?? "";
                    _trackList.HasMoreItems = !string.IsNullOrEmpty(_accountPlaylistsContinuationToken);
                    if (next.Items.Count > 0)
                    {
                        _accountPlaylists.AddRange(next.Items);
                        _trackList.AppendItems(next.Items);
                    }
                }
                finally
                {
                    _isLoadingMore = false;
                    _trackList.IsLoadingMore = false;
                }
                return;
            }

            if (_activeSection == "account_playlist_items" && _openedAccountPlaylistCard != null && !string.IsNullOrEmpty(_openedAccountPlaylistContinuationToken))
            {
                _isLoadingMore = true;
                _trackList.IsLoadingMore = true;
                try
                {
                    var next = await YouTubeAccountFeedService.Shared.FetchMusicPlaylistItemsAsync(
                        _openedAccountPlaylistCard.PlaylistId, _config, _openedAccountPlaylistContinuationToken);
                    _openedAccountPlaylistContinuationToken = next.ContinuationToken ?? "";
                    _trackList.HasMoreItems = !string.IsNullOrEmpty(_openedAccountPlaylistContinuationToken);
                    if (next.Items.Count > 0)
                    {
                        _displayedTracks.AddRange(next.Items);
                        _trackList.AppendItems(next.Items);
                        _lblMusicHeader.Text = string.Format("{0} ({1} faixas)", _openedAccountPlaylistCard.Title, _displayedTracks.Count);
                    }
                }
                finally
                {
                    _isLoadingMore = false;
                    _trackList.IsLoadingMore = false;
                }
                return;
            }

            if (_activeSection == "search" && !string.IsNullOrEmpty(_searchContinuationToken))
            {
                _isLoadingMore = true;
                _trackList.IsLoadingMore = true;
                try
                {
                    var next = await _ytService.FetchSearchContinuationAsync(_searchContinuationToken, false);
                    _searchContinuationToken = next.ContinuationToken ?? "";
                    _trackList.HasMoreItems = !string.IsNullOrEmpty(_searchContinuationToken);
                    if (next.Items != null && next.Items.Count > 0)
                    {
                        _displayedTracks.AddRange(next.Items);
                        _trackList.AppendItems(next.Items);
                    }
                }
                finally
                {
                    _isLoadingMore = false;
                    _trackList.IsLoadingMore = false;
                }
            }
        }

        private void CreateNewPlaylist()
        {
            string? name = ShowInputPrompt("Nova Playlist Local", "Nome da nova playlist:");
            if (string.IsNullOrWhiteSpace(name)) return;
            _config.Playlists.Add(new UserPlaylist { Name = name.Trim() });
            _config.Save();
            RefreshLocalPlaylistsList();
            StatusNotification?.Invoke("Playlist criada: " + name.Trim());
        }

        private async Task ImportPlaylistByUrlAsync()
        {
            string? url = ShowInputPrompt("Importar Playlist por URL", "Cole o link da playlist do YouTube / YT Music:");
            if (string.IsNullOrWhiteSpace(url)) return;

            StatusNotification?.Invoke("Importando playlist...");
            var res = await _ytService.FetchUrlOrPlaylistAsync(url.Trim(), _config, false);
            if (res.Item1 != null && res.Item1.Count > 0)
            {
                _config.Playlists.Add(new UserPlaylist
                {
                    Name = "Importada (" + res.Item1.Count + ")",
                    RemoteUrl = url.Trim(),
                    Tracks = res.Item1
                });
                _config.Save();
                RefreshLocalPlaylistsList();
                StatusNotification?.Invoke(string.Format("Playlist importada com {0} faixas!", res.Item1.Count));
            }
            else
            {
                StatusNotification?.Invoke(res.Item2 ?? "Não foi possível importar esta playlist.");
            }
        }

        private string? ShowInputPrompt(string title, string label)
        {
            var dlg = new Window
            {
                Title = title,
                Width = 420,
                Height = 155,
                ResizeMode = ResizeMode.NoResize,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = Window.GetWindow(this),
                Background = new SolidColorBrush(Color.FromRgb(18, 18, 20))
            };
            dlg.SourceInitialized += (s, e) => WpfMainWindow.ApplyDarkTitleBar(dlg);
            var stack = new StackPanel { Margin = new Thickness(16) };
            stack.Children.Add(new TextBlock { Text = label, Foreground = Brushes.White, Margin = new Thickness(0, 0, 0, 8) });
            var tb = new TextBox
            {
                Background = new SolidColorBrush(Color.FromRgb(30, 30, 34)),
                Foreground = Brushes.White,
                CaretBrush = Brushes.White,
                BorderBrush = new SolidColorBrush(Color.FromRgb(56, 56, 62)),
                Padding = new Thickness(8, 6, 8, 6)
            };
            stack.Children.Add(tb);
            var btn = new Button
            {
                Content = "OK",
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 10, 0, 0),
                Padding = new Thickness(18, 6, 18, 6),
                Background = new SolidColorBrush(Color.FromRgb(255, 0, 51)),
                Foreground = Brushes.White,
                BorderThickness = new Thickness(0)
            };
            btn.Click += (s, e) => { dlg.DialogResult = true; dlg.Close(); };
            stack.Children.Add(btn);
            dlg.Content = stack;
            tb.Focus();
            return dlg.ShowDialog() == true ? tb.Text : null;
        }

        private void UpdateNavSelection()
        {
            bool plActive = _activeSection == "account_playlists" || _activeSection == "account_playlist_items";
            StyleNav(_navHome, _activeSection == "home");
            StyleNav(_miniHome, _activeSection == "home");
            StyleNav(_navExplore, _activeSection == "explore");
            StyleNav(_miniExplore, _activeSection == "explore");
            StyleNav(_navAccountPlaylists, plActive);
            StyleNav(_miniPlaylists, plActive);
            StyleNav(_navFavorites, _activeSection == "favorites");
            StyleNav(_miniFavorites, _activeSection == "favorites");
            StyleNav(_navQueue, _activeSection == "queue");
            StyleNav(_miniQueue, _activeSection == "queue");
        }

        private static void StyleNav(Button btn, bool active)
        {
            btn.Background = active ? new SolidColorBrush(Color.FromRgb(34, 34, 38)) : Brushes.Transparent;
            btn.Foreground = active ? new SolidColorBrush(Color.FromRgb(241, 241, 241)) : new SolidColorBrush(Color.FromRgb(185, 185, 190));
            btn.FontWeight = active ? FontWeights.Bold : FontWeights.Normal;
        }

        public void Dispose()
        {
            try { _uiTimer.Stop(); } catch { }
        }
    }
}
