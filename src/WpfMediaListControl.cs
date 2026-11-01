using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace LiteTube
{
    /// <summary>
    /// Hardware-accelerated (Direct3D), UI-virtualized media catalog control using native WPF.
    /// Supports:
    /// 1. YouTube Grid Mode (multi-column responsive cards with avatar, title, channel link and favorite).
    /// 2. YouTube Watch Page Sidebar ("Up Next" compact horizontal cards).
    /// 3. YouTube Music Row Mode (track rows with duration, favorite, queue and watch clip buttons).
    /// </summary>
    public class WpfMediaListControl : UserControl
    {
        private ListBox _listBox;
        private Border _footerPanel;
        private TextBlock _footerText;
        private Border _emptyStatePanel;
        private TextBlock _emptyStateText;

        private DataTemplate _gridRowTemplate;
        private DataTemplate _sidebarItemTemplate;
        private DataTemplate _musicRowTemplate;

        private readonly List<TrackItem> _items = new List<TrackItem>();
        private readonly List<TrackItemViewModel> _viewModels = new List<TrackItemViewModel>();
        private readonly ObservableCollection<CardRowViewModel> _gridRows = new ObservableCollection<CardRowViewModel>();
        private readonly ObservableCollection<TrackItemViewModel> _linearRows = new ObservableCollection<TrackItemViewModel>();

        private bool _isGridMode = false;
        private bool _isCompactSidebarMode = false;
        private bool _isMusicEnvironment = false;
        private bool _hasMoreItems = false;
        private bool _isLoadingMore = false;
        private bool _singleClickActivates = false;
        private int _selectedIndex = -1;
        private string _playingVideoId = "";
        private int _lastColumnCount = -1;
        private double _lastUsableWidth = -1;
        private bool _isSuspended = false;
        private double _lastScrollOffset = 0;

        private SolidColorBrush _accentBrush;
        private Func<string, bool> _isFavoriteOrSavedCheck;

        public event Action<int, TrackItem> ItemActivated;
        public event Action<TrackItem> ChannelClicked;
        public event Action<int, TrackItem> FavoriteClicked;
        public event Action<int, TrackItem> AddToQueueClicked;
        public event Action<int, TrackItem> WatchClipClicked;
        public event Action LoadMoreRequested;

        public WpfMediaListControl()
        {
            _accentBrush = new SolidColorBrush(Color.FromRgb(255, 30, 60));
            _accentBrush.Freeze();

            InitializeComponent();
        }

        private void InitializeComponent()
        {
            bool loaded = false;

            // 1. Tenta carregar recurso compilado BAML via Application.LoadComponent
            try
            {
                var resourceUri = new Uri("/LiteTube;component/src/WpfMediaListControl.xaml", UriKind.Relative);
                Application.LoadComponent(this, resourceUri);
                loaded = true;
            }
            catch { }

            if (!loaded)
            {
                try
                {
                    var resourceUri2 = new Uri("/LiteTube;component/WpfMediaListControl.xaml", UriKind.Relative);
                    Application.LoadComponent(this, resourceUri2);
                    loaded = true;
                }
                catch { }
            }

            // 2. Fallback: carrega diretamente o arquivo WpfMediaListControl.xaml via XamlReader
            if (!loaded)
            {
                string baseDir = AppDomain.CurrentDomain.BaseDirectory;
                string p1 = Path.Combine(baseDir, "src", "WpfMediaListControl.xaml");
                string p2 = Path.Combine(baseDir, "WpfMediaListControl.xaml");
                string targetPath = File.Exists(p1) ? p1 : (File.Exists(p2) ? p2 : null);

                if (targetPath != null)
                {
                    using (var fs = new FileStream(targetPath, FileMode.Open, FileAccess.Read, FileShare.Read))
                    {
                        var root = (FrameworkElement)XamlReader.Load(fs);
                        this.Content = root;
                        loaded = true;
                    }
                }
            }

            var container = this.Content as FrameworkElement ?? this;

            if (container.Resources.Contains("GridRowTemplate"))
                _gridRowTemplate = (DataTemplate)container.Resources["GridRowTemplate"];
            if (container.Resources.Contains("SidebarItemTemplate"))
                _sidebarItemTemplate = (DataTemplate)container.Resources["SidebarItemTemplate"];
            if (container.Resources.Contains("MusicRowTemplate"))
                _musicRowTemplate = (DataTemplate)container.Resources["MusicRowTemplate"];

            _listBox = (ListBox)container.FindName("MediaListBox");
            _footerPanel = (Border)container.FindName("FooterPanel");
            _footerText = (TextBlock)container.FindName("FooterText");
            _emptyStatePanel = (Border)container.FindName("EmptyStatePanel");
            _emptyStateText = (TextBlock)container.FindName("EmptyStateText");

            if (_listBox != null)
            {
                _listBox.Loaded += (s, e) =>
                {
                    if (_wpfScrollViewer == null)
                    {
                        _wpfScrollViewer = FindVisualChild<ScrollViewer>(_listBox);
                    }
                };
                _listBox.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler(OnListBoxScrollChanged));
                _listBox.PreviewMouseLeftButtonDown += OnListBoxPreviewMouseLeftButtonDown;
                _listBox.MouseLeftButtonUp += OnListBoxMouseLeftButtonUp;
            }

            this.MouseEnter += (s, e) =>
            {
                if (_listBox != null && !_listBox.IsFocused)
                {
                    _listBox.Focus();
                }
            };

            if (_footerPanel != null)
            {
                _footerPanel.MouseLeftButtonUp += (s, e) =>
                {
                    if (_hasMoreItems && !_isLoadingMore && LoadMoreRequested != null)
                    {
                        LoadMoreRequested();
                    }
                };
            }

            ApplyCurrentMode();
        }

        #region Public Properties & Configuration

        public List<TrackItem> Items
        {
            get { return _items; }
        }

        public bool IsSuspended
        {
            get { return _isSuspended; }
        }

        public double ScrollOffset
        {
            get
            {
                if (WpfScrollViewer != null) return WpfScrollViewer.VerticalOffset;
                return _lastScrollOffset;
            }
            set
            {
                _lastScrollOffset = value;
                if (WpfScrollViewer != null)
                {
                    WpfScrollViewer.ScrollToVerticalOffset(value);
                }
            }
        }

        public bool IsGridMode
        {
            get { return _isGridMode; }
            set
            {
                if (_isGridMode != value)
                {
                    _isGridMode = value;
                    ApplyCurrentMode();
                    RebuildItemsSource();
                }
            }
        }

        public bool IsCompactSidebarMode
        {
            get { return _isCompactSidebarMode; }
            set
            {
                if (_isCompactSidebarMode != value)
                {
                    _isCompactSidebarMode = value;
                    ApplyCurrentMode();
                    RebuildItemsSource();
                }
            }
        }

        public bool IsMusicEnvironment
        {
            get { return _isMusicEnvironment; }
            set
            {
                if (_isMusicEnvironment != value)
                {
                    _isMusicEnvironment = value;
                    ApplyCurrentMode();
                    RebuildItemsSource();
                }
            }
        }

        public bool HasMoreItems
        {
            get { return _hasMoreItems; }
            set
            {
                _hasMoreItems = value;
                UpdateFooterState();
            }
        }

        public bool IsLoadingMore
        {
            get { return _isLoadingMore; }
            set
            {
                _isLoadingMore = value;
                UpdateFooterState();
            }
        }

        public bool SingleClickActivates
        {
            get { return _singleClickActivates; }
            set { _singleClickActivates = value; }
        }

        public int SelectedIndex
        {
            get { return _selectedIndex; }
            set
            {
                if (_selectedIndex != value)
                {
                    int old = _selectedIndex;
                    _selectedIndex = value;
                    UpdateItemSelectionState(old);
                    UpdateItemSelectionState(_selectedIndex);
                }
            }
        }

        public TrackItem SelectedItem
        {
            get
            {
                if (_selectedIndex >= 0 && _selectedIndex < _items.Count)
                    return _items[_selectedIndex];
                return null;
            }
        }

        public string PlayingVideoId
        {
            get { return _playingVideoId; }
            set
            {
                string norm = value ?? "";
                if (!string.Equals(_playingVideoId, norm, StringComparison.OrdinalIgnoreCase))
                {
                    _playingVideoId = norm;
                    UpdateAllPlayingStates();
                }
            }
        }

        public Func<string, bool> IsFavoriteOrSavedCheck
        {
            get { return _isFavoriteOrSavedCheck; }
            set
            {
                _isFavoriteOrSavedCheck = value;
                UpdateAllFavoriteStates();
            }
        }

        public void SetAccentColor(byte r, byte g, byte b)
        {
            var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
            brush.Freeze();
            _accentBrush = brush;
            foreach (var vm in _viewModels)
            {
                vm.SetAccentBrush(_accentBrush);
            }
        }

        #endregion

        #region Public Methods

        public void SetItems(List<TrackItem> items, bool resetScroll = true)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action<List<TrackItem>, bool>(SetItems), items, resetScroll);
                return;
            }

            _isSuspended = false;
            _items.Clear();
            _viewModels.Clear();

            if (items != null)
            {
                _items.AddRange(items);
            }

            for (int i = 0; i < _items.Count; i++)
            {
                var it = _items[i];
                var vm = new TrackItemViewModel(it, i, _accentBrush, Dispatcher);
                if (_isFavoriteOrSavedCheck != null && !string.IsNullOrEmpty(it.Id))
                {
                    vm.IsFavorite = _isFavoriteOrSavedCheck(it.Id);
                }
                vm.IsPlaying = !string.IsNullOrEmpty(_playingVideoId) &&
                               string.Equals(it.Id, _playingVideoId, StringComparison.OrdinalIgnoreCase);
                _viewModels.Add(vm);
            }

            if (resetScroll)
            {
                _selectedIndex = _items.Count > 0 ? 0 : -1;
                _lastScrollOffset = 0;
                ScrollToTop();
            }
            else if (_selectedIndex >= _items.Count)
            {
                _selectedIndex = _items.Count - 1;
            }

            RebuildItemsSource();
            UpdateEmptyState();
            UpdateFooterState();
        }

        public int AppendItems(List<TrackItem> moreItems)
        {
            if (moreItems == null || moreItems.Count == 0) return 0;

            if (!Dispatcher.CheckAccess())
            {
                return (int)Dispatcher.Invoke(new Func<List<TrackItem>, int>(AppendItems), moreItems);
            }

            var existingIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < _items.Count; i++)
            {
                if (!string.IsNullOrEmpty(_items[i].Id))
                    existingIds.Add(_items[i].Id);
            }

            int added = 0;
            var newVms = new List<TrackItemViewModel>();

            for (int i = 0; i < moreItems.Count; i++)
            {
                var it = moreItems[i];
                if (it == null || string.IsNullOrEmpty(it.Id)) continue;
                if (existingIds.Add(it.Id))
                {
                    int idx = _items.Count;
                    _items.Add(it);
                    var vm = new TrackItemViewModel(it, idx, _accentBrush, Dispatcher);
                    if (_isFavoriteOrSavedCheck != null)
                    {
                        vm.IsFavorite = _isFavoriteOrSavedCheck(it.Id);
                    }
                    vm.IsPlaying = !string.IsNullOrEmpty(_playingVideoId) &&
                                   string.Equals(it.Id, _playingVideoId, StringComparison.OrdinalIgnoreCase);
                    _viewModels.Add(vm);
                    newVms.Add(vm);
                    added++;
                }
            }

            if (added > 0)
            {
                if (_isGridMode)
                {
                    RebuildGridRows();
                }
                else
                {
                    for (int i = 0; i < newVms.Count; i++)
                    {
                        _linearRows.Add(newVms[i]);
                    }
                }
            }

            UpdateEmptyState();
            UpdateFooterState();
            return added;
        }

        public void ScrollToTop()
        {
            if (_listBox != null)
            {
                var sv = WpfScrollViewer;
                if (sv != null)
                {
                    sv.ScrollToTop();
                }
                else if (_listBox.Items.Count > 0)
                {
                    _listBox.ScrollIntoView(_listBox.Items[0]);
                }
            }
        }

        private ScrollViewer _wpfScrollViewer;

        public ScrollViewer WpfScrollViewer
        {
            get
            {
                if (_wpfScrollViewer == null && _listBox != null)
                {
                    _wpfScrollViewer = FindVisualChild<ScrollViewer>(_listBox);
                }
                return _wpfScrollViewer;
            }
        }

        public void ScrollByDelta(int delta)
        {
            var sv = WpfScrollViewer;
            if (sv != null)
            {
                double offset = sv.VerticalOffset - (delta / 3.0);
                sv.ScrollToVerticalOffset(Math.Max(0, Math.Min(sv.ScrollableHeight, offset)));
            }
        }

        public void RefreshVisuals()
        {
            UpdateAllFavoriteStates();
            UpdateAllPlayingStates();
        }

        public void InvalidateItem(int index)
        {
            if (index >= 0 && index < _viewModels.Count)
            {
                var vm = _viewModels[index];
                if (_isFavoriteOrSavedCheck != null && !string.IsNullOrEmpty(vm.Id))
                {
                    vm.IsFavorite = _isFavoriteOrSavedCheck(vm.Id);
                }
                vm.IsPlaying = !string.IsNullOrEmpty(_playingVideoId) &&
                               string.Equals(vm.Id, _playingVideoId, StringComparison.OrdinalIgnoreCase);
                vm.RefreshVisualBindings();
            }
        }

        public void InvalidateCardById(string targetId)
        {
            if (string.IsNullOrEmpty(targetId)) return;
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action<string>(InvalidateCardById), targetId);
                return;
            }

            for (int i = 0; i < _viewModels.Count; i++)
            {
                var vm = _viewModels[i];
                if (string.Equals(vm.Id, targetId, StringComparison.OrdinalIgnoreCase))
                {
                    if (_isFavoriteOrSavedCheck != null)
                    {
                        vm.IsFavorite = _isFavoriteOrSavedCheck(vm.Id);
                    }
                    vm.IsPlaying = !string.IsNullOrEmpty(_playingVideoId) &&
                                   string.Equals(vm.Id, _playingVideoId, StringComparison.OrdinalIgnoreCase);
                    vm.EnsureThumbnailLoaded();
                    vm.RefreshVisualBindings();
                    break;
                }
            }
        }

        public void SuspendAndDetach()
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.Invoke(new Action(SuspendAndDetach));
                return;
            }

            if (_isSuspended) return;

            if (WpfScrollViewer != null)
            {
                _lastScrollOffset = WpfScrollViewer.VerticalOffset;
            }

            _isSuspended = true;

            // Descarrega miniaturas ativas em memria/DirectX de todos os viewmodels
            for (int i = 0; i < _viewModels.Count; i++)
            {
                _viewModels[i].UnloadThumbnail();
            }

            // Desconecta a rvore visual de linhas e contineres do ListBox
            _gridRows.Clear();
            _linearRows.Clear();
            if (_listBox != null)
            {
                _listBox.ItemsSource = null;
            }
        }

        public void ResumeAndAttach()
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.Invoke(new Action(ResumeAndAttach));
                return;
            }

            if (!_isSuspended) return;
            _isSuspended = false;

            // Reconsi a lista visual a partir do modelo estruturado em memria (TrackItems)
            RebuildItemsSource();
            UpdateEmptyState();
            UpdateFooterState();

            // Restaura o offset do scroll de forma suave aps o layout pass do WPF
            if (_lastScrollOffset > 0 && _listBox != null)
            {
                double target = _lastScrollOffset;
                _listBox.Dispatcher.BeginInvoke(new Action(() =>
                {
                    var sv = WpfScrollViewer;
                    if (sv != null)
                    {
                        sv.ScrollToVerticalOffset(Math.Min(sv.ScrollableHeight, target));
                    }
                }), DispatcherPriority.Loaded);
            }
        }

        public void ClearItems(bool keepInMemory = false)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.Invoke(new Action<bool>(ClearItems), keepInMemory);
                return;
            }

            if (keepInMemory)
            {
                SuspendAndDetach();
                return;
            }

            _isSuspended = false;
            _lastScrollOffset = 0;
            _selectedIndex = -1;

            for (int i = 0; i < _viewModels.Count; i++)
            {
                _viewModels[i].UnloadThumbnail();
            }

            _items.Clear();
            _viewModels.Clear();
            _gridRows.Clear();
            _linearRows.Clear();

            if (_listBox != null)
            {
                _listBox.ItemsSource = null;
            }

            UpdateEmptyState();
            UpdateFooterState();
        }

        public bool IsItemVisible(string id)
        {
            if (_isSuspended) return false;
            if (string.IsNullOrEmpty(id) || _viewModels.Count == 0) return false;
            // No WPF virtualizado com renderizao por GPU, os cards no viewport esto ativos
            return true;
        }

        public void PruneOffscreenResources()
        {
            if (_isSuspended || _viewModels.Count == 0) return;
            int cur = _selectedIndex >= 0 ? _selectedIndex : 0;
            int keepMin = Math.Max(0, cur - 25);
            int keepMax = Math.Min(_viewModels.Count - 1, cur + 35);

            var keepIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < _viewModels.Count; i++)
            {
                var vm = _viewModels[i];
                if (i >= keepMin && i <= keepMax)
                {
                    if (!string.IsNullOrEmpty(vm.Id))
                        keepIds.Add(vm.Id);
                }
                else
                {
                    vm.UnloadThumbnail();
                }
            }
            ThumbnailCache.PruneOffscreenExcept(keepIds);
        }

        #endregion

        #region Internal Layout & View Switching

        private void ApplyCurrentMode()
        {
            if (_listBox == null) return;

            if (_isGridMode)
            {
                if (_gridRowTemplate != null) _listBox.ItemTemplate = _gridRowTemplate;
            }
            else if (_isCompactSidebarMode)
            {
                if (_sidebarItemTemplate != null) _listBox.ItemTemplate = _sidebarItemTemplate;
            }
            else
            {
                if (_musicRowTemplate != null) _listBox.ItemTemplate = _musicRowTemplate;
            }
        }

        private void RebuildItemsSource()
        {
            if (_listBox == null) return;

            if (_isGridMode)
            {
                RebuildGridRows();
                _listBox.ItemsSource = _gridRows;
            }
            else
            {
                _linearRows.Clear();
                for (int i = 0; i < _viewModels.Count; i++)
                {
                    _linearRows.Add(_viewModels[i]);
                }
                _listBox.ItemsSource = _linearRows;
            }
        }

        protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
        {
            base.OnRenderSizeChanged(sizeInfo);
            if (_isGridMode)
            {
                double usableW = Math.Max(240, ActualWidth - 24);
                int cols = CalculateColumnCount(usableW);
                if (cols != _lastColumnCount || Math.Abs(usableW - _lastUsableWidth) > 10)
                {
                    _lastColumnCount = cols;
                    _lastUsableWidth = usableW;
                    RebuildGridRows();
                }
            }
        }

        private int CalculateColumnCount(double usableWidth)
        {
            if (usableWidth >= 1220) return 4;
            if (usableWidth >= 880) return 3;
            if (usableWidth >= 550) return 2;
            return 1;
        }

        private void RebuildGridRows()
        {
            _gridRows.Clear();
            if (_viewModels.Count == 0) return;

            double usableW = Math.Max(240, ActualWidth > 0 ? ActualWidth - 28 : 800);
            int cols = CalculateColumnCount(usableW);
            _lastColumnCount = cols;
            _lastUsableWidth = usableW;

            int gapX = 18;
            int totalGaps = (cols - 1) * gapX;
            int cardW = Math.Max(260, (int)((usableW - totalGaps) / cols));
            if (cardW > 460) cardW = 460;

            int thumbH = (int)Math.Round(cardW * 9.0 / 16.0);
            int cardH = thumbH + 88;

            int total = _viewModels.Count;
            for (int i = 0; i < total; i += cols)
            {
                var rowVm = new CardRowViewModel(cardW, cardH, thumbH);
                for (int c = 0; c < cols && (i + c) < total; c++)
                {
                    var vm = _viewModels[i + c];
                    vm.CardWidth = cardW;
                    vm.CardHeight = cardH;
                    vm.ThumbHeight = thumbH;
                    rowVm.Cards.Add(vm);
                }
                _gridRows.Add(rowVm);
            }
        }

        private void UpdateAllFavoriteStates()
        {
            if (_isFavoriteOrSavedCheck == null) return;
            for (int i = 0; i < _viewModels.Count; i++)
            {
                var vm = _viewModels[i];
                if (!string.IsNullOrEmpty(vm.Id))
                {
                    vm.IsFavorite = _isFavoriteOrSavedCheck(vm.Id);
                }
            }
        }

        private void UpdateAllPlayingStates()
        {
            for (int i = 0; i < _viewModels.Count; i++)
            {
                var vm = _viewModels[i];
                vm.IsPlaying = !string.IsNullOrEmpty(_playingVideoId) &&
                               string.Equals(vm.Id, _playingVideoId, StringComparison.OrdinalIgnoreCase);
            }
        }

        private void UpdateItemSelectionState(int index)
        {
            if (index >= 0 && index < _viewModels.Count)
            {
                _viewModels[index].IsSelected = (index == _selectedIndex);
            }
        }

        private void UpdateFooterState()
        {
            if (_footerPanel == null || _footerText == null) return;

            if ((_hasMoreItems || _isLoadingMore) && _items.Count > 0)
            {
                _footerPanel.Visibility = Visibility.Visible;
                _footerText.Text = _isLoadingMore
                    ? "⏳ Carregando mais vídeos do YouTube..."
                    : "➕ Carregar mais vídeos (ou continue rolando)";
            }
            else
            {
                _footerPanel.Visibility = Visibility.Collapsed;
            }
        }

        private void UpdateEmptyState()
        {
            if (_emptyStatePanel == null || _emptyStateText == null) return;

            if (_items.Count == 0)
            {
                _emptyStatePanel.Visibility = Visibility.Visible;
                _emptyStateText.Text = _isCompactSidebarMode
                    ? "Carregando próximos vídeos..."
                    : "Pesquise vídeos, canais ou músicas na barra de busca superior do YouTube.";
            }
            else
            {
                _emptyStatePanel.Visibility = Visibility.Collapsed;
            }
        }

        #endregion

        #region User Interaction & Infinite Scroll

        private void OnListBoxScrollChanged(object sender, ScrollChangedEventArgs e)
        {
            if (_wpfScrollViewer == null)
            {
                _wpfScrollViewer = FindVisualChild<ScrollViewer>(_listBox);
            }
            if (e.VerticalChange == 0 && e.ViewportHeightChange == 0) return;

            var sv = _wpfScrollViewer ?? FindVisualChild<ScrollViewer>(_listBox);
            if (sv != null)
            {
                if (!_isSuspended && sv.VerticalOffset > 0)
                {
                    _lastScrollOffset = sv.VerticalOffset;
                }

                // Gatilho suave de infinite scroll ao aproximar-se do final (threshold 350px)
                if (sv.ScrollableHeight > 0 && sv.VerticalOffset >= (sv.ScrollableHeight - 350))
                {
                    if (_hasMoreItems && !_isLoadingMore && LoadMoreRequested != null)
                    {
                        LoadMoreRequested();
                    }
                }
            }
        }

        private void OnListBoxPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            // Trata duplo clique em modo lista/msica
            if (e.ClickCount >= 2 && !_singleClickActivates && !_isGridMode && !_isCompactSidebarMode)
            {
                var vm = FindTrackViewModelFromSource(e.OriginalSource as DependencyObject);
                if (vm != null && !vm.IsChannel)
                {
                    _selectedIndex = vm.Index;
                    if (ItemActivated != null)
                    {
                        ItemActivated(vm.Index, vm.Model);
                    }
                    e.Handled = true;
                }
            }
        }

        private void OnListBoxMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            var dep = e.OriginalSource as DependencyObject;
            if (dep == null) return;

            string tag = FindTagInVisualTree(dep);
            var vm = FindTrackViewModelFromSource(dep);

            if (tag == "LoadMore")
            {
                if (_hasMoreItems && !_isLoadingMore && LoadMoreRequested != null)
                {
                    LoadMoreRequested();
                }
                e.Handled = true;
                return;
            }

            if (vm == null) return;

            if (tag == "Favorite")
            {
                vm.IsFavorite = !vm.IsFavorite;
                if (FavoriteClicked != null)
                {
                    FavoriteClicked(vm.Index, vm.Model);
                }
                e.Handled = true;
                return;
            }

            if (tag == "Channel" || vm.IsChannel)
            {
                if (ChannelClicked != null)
                {
                    ChannelClicked(vm.Model);
                }
                else if (ItemActivated != null)
                {
                    ItemActivated(vm.Index, vm.Model);
                }
                e.Handled = true;
                return;
            }

            if (tag == "Queue")
            {
                if (AddToQueueClicked != null)
                {
                    AddToQueueClicked(vm.Index, vm.Model);
                }
                e.Handled = true;
                return;
            }

            if (tag == "Video")
            {
                if (WatchClipClicked != null)
                {
                    WatchClipClicked(vm.Index, vm.Model);
                }
                e.Handled = true;
                return;
            }

            // Clique no corpo do card
            _selectedIndex = vm.Index;
            UpdateItemSelectionState(_selectedIndex);

            if (_singleClickActivates || _isGridMode || _isCompactSidebarMode)
            {
                if (ItemActivated != null)
                {
                    ItemActivated(vm.Index, vm.Model);
                }
            }
        }

        private static string FindTagInVisualTree(DependencyObject start)
        {
            DependencyObject cur = start;
            while (cur != null)
            {
                var fe = cur as FrameworkElement;
                if (fe != null && fe.Tag != null)
                {
                    return fe.Tag.ToString();
                }
                cur = VisualTreeHelper.GetParent(cur);
            }
            return null;
        }

        private static TrackItemViewModel FindTrackViewModelFromSource(DependencyObject start)
        {
            DependencyObject cur = start;
            while (cur != null)
            {
                var fe = cur as FrameworkElement;
                if (fe != null && fe.DataContext is TrackItemViewModel)
                {
                    return (TrackItemViewModel)fe.DataContext;
                }
                cur = VisualTreeHelper.GetParent(cur);
            }
            return null;
        }

        private static T FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
        {
            if (parent == null) return null;
            int count = VisualTreeHelper.GetChildrenCount(parent);
            for (int i = 0; i < count; i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                var match = child as T;
                if (match != null) return match;
                var nested = FindVisualChild<T>(child);
                if (nested != null) return nested;
            }
            return null;
        }

        #endregion
    }

    #region ViewModels

    public class CardRowViewModel : INotifyPropertyChanged
    {
        public ObservableCollection<TrackItemViewModel> Cards { get; private set; }

        private double _cardWidth;
        public double CardWidth
        {
            get { return _cardWidth; }
            set { _cardWidth = value; OnPropertyChanged("CardWidth"); }
        }

        private double _cardHeight;
        public double CardHeight
        {
            get { return _cardHeight; }
            set { _cardHeight = value; OnPropertyChanged("CardHeight"); }
        }

        private double _thumbHeight;
        public double ThumbHeight
        {
            get { return _thumbHeight; }
            set { _thumbHeight = value; OnPropertyChanged("ThumbHeight"); }
        }

        public CardRowViewModel(double cardW, double cardH, double thumbH)
        {
            Cards = new ObservableCollection<TrackItemViewModel>();
            _cardWidth = cardW;
            _cardHeight = cardH;
            _thumbHeight = thumbH;
        }

        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged(string prop)
        {
            if (PropertyChanged != null) PropertyChanged(this, new PropertyChangedEventArgs(prop));
        }
    }

    public class TrackItemViewModel : INotifyPropertyChanged
    {
        private static readonly SolidColorBrush BrushBgDefault = new SolidColorBrush(Color.FromRgb(18, 18, 18));
        private static readonly SolidColorBrush BrushBgPlaying = new SolidColorBrush(Color.FromRgb(40, 22, 32));
        private static readonly SolidColorBrush BrushBgSelected = new SolidColorBrush(Color.FromRgb(28, 28, 28));
        private static readonly SolidColorBrush BrushTextMain = new SolidColorBrush(Color.FromRgb(245, 245, 245));
        private static readonly SolidColorBrush BrushTextDim = new SolidColorBrush(Color.FromRgb(160, 160, 160));
        private static readonly SolidColorBrush BrushFavDim = new SolidColorBrush(Color.FromArgb(140, 255, 255, 255));
        private static readonly SolidColorBrush BrushDurationDefault = new SolidColorBrush(Color.FromArgb(215, 10, 10, 10));
        private static readonly SolidColorBrush BrushDurationPlaylist = new SolidColorBrush(Color.FromArgb(220, 120, 60, 200));

        static TrackItemViewModel()
        {
            BrushBgDefault.Freeze();
            BrushBgPlaying.Freeze();
            BrushBgSelected.Freeze();
            BrushTextMain.Freeze();
            BrushTextDim.Freeze();
            BrushFavDim.Freeze();
            BrushDurationDefault.Freeze();
            BrushDurationPlaylist.Freeze();
        }

        private readonly TrackItem _model;
        private readonly int _index;
        private readonly Dispatcher _dispatcher;
        private SolidColorBrush _accentBrush;

        private ImageSource _thumbnailSource;
        private bool _thumbRequested = false;
        private bool _isPlaying = false;
        private bool _isFavorite = false;
        private bool _isSelected = false;

        private double _cardWidth = 320;
        private double _cardHeight = 268;
        private double _thumbHeight = 180;

        public TrackItem Model { get { return _model; } }
        public int Index { get { return _index; } }
        public string Id { get { return _model != null ? _model.Id : ""; } }
        public string Title { get { return _model != null ? (_model.DisplayTitle ?? _model.Title ?? "") : ""; } }
        public string Artist { get { return _model != null ? (_model.Artist ?? "") : ""; } }
        public string Duration { get { return _model != null ? (_model.Duration ?? "") : ""; } }
        public bool IsChannel { get { return _model != null && _model.IsChannel; } }
        public bool IsPlaylistCard { get { return _model != null && _model.IsPlaylistCard; } }

        public string ChannelDisplayTitle
        {
            get { return (_model != null && !string.IsNullOrEmpty(_model.Title)) ? _model.Title + " ✓" : ""; }
        }

        public string Subtitle
        {
            get
            {
                if (_model == null) return "";
                string dur = !string.IsNullOrEmpty(_model.Duration) && _model.Duration != "--:--" ? _model.Duration : "";
                if (string.IsNullOrEmpty(dur)) return _model.Artist ?? "";
                return (_model.Artist ?? "") + "  •  " + dur;
            }
        }

        public Visibility VideoVisibility
        {
            get { return IsChannel ? Visibility.Collapsed : Visibility.Visible; }
        }

        public Visibility ChannelVisibility
        {
            get { return IsChannel ? Visibility.Visible : Visibility.Collapsed; }
        }

        public Visibility DurationVisibility
        {
            get
            {
                return (!string.IsNullOrEmpty(Duration) && Duration != "--:--" && !IsChannel)
                    ? Visibility.Visible : Visibility.Collapsed;
            }
        }

        public Visibility FallbackVisibility
        {
            get { return _thumbnailSource == null ? Visibility.Visible : Visibility.Collapsed; }
        }

        public Visibility FavoriteButtonVisibility
        {
            get { return (IsChannel || IsPlaylistCard) ? Visibility.Collapsed : Visibility.Visible; }
        }

        public Visibility QueueButtonVisibility
        {
            get { return (IsChannel || IsPlaylistCard) ? Visibility.Collapsed : Visibility.Visible; }
        }

        public Visibility VideoButtonVisibility
        {
            get { return (IsChannel || IsPlaylistCard) ? Visibility.Collapsed : Visibility.Visible; }
        }

        public string PlaceholderIcon
        {
            get { return IsPlaylistCard ? "📂" : (IsChannel ? "👤" : "▶"); }
        }

        public Brush DurationBadgeBackground
        {
            get { return IsPlaylistCard ? BrushDurationPlaylist : BrushDurationDefault; }
        }

        public string AvatarInitial
        {
            get
            {
                if (IsPlaylistCard) return "📂";
                if (!string.IsNullOrEmpty(Artist)) return Artist.Substring(0, 1).ToUpper();
                if (!string.IsNullOrEmpty(Title)) return Title.Substring(0, 1).ToUpper();
                return "Y";
            }
        }

        public Brush AvatarBrush
        {
            get
            {
                if (IsPlaylistCard) return BrushDurationPlaylist;
                return GetChannelBrush(Artist);
            }
        }

        public double CardWidth
        {
            get { return _cardWidth; }
            set { if (_cardWidth != value) { _cardWidth = value; OnPropertyChanged("CardWidth"); } }
        }

        public double CardHeight
        {
            get { return _cardHeight; }
            set { if (_cardHeight != value) { _cardHeight = value; OnPropertyChanged("CardHeight"); } }
        }

        public double ThumbHeight
        {
            get { return _thumbHeight; }
            set { if (_thumbHeight != value) { _thumbHeight = value; OnPropertyChanged("ThumbHeight"); } }
        }

        public bool IsPlaying
        {
            get { return _isPlaying; }
            set
            {
                if (_isPlaying != value)
                {
                    _isPlaying = value;
                    OnPropertyChanged("IsPlaying");
                    RefreshVisualBindings();
                }
            }
        }

        public bool IsFavorite
        {
            get { return _isFavorite; }
            set
            {
                if (_isFavorite != value)
                {
                    _isFavorite = value;
                    OnPropertyChanged("IsFavorite");
                    OnPropertyChanged("FavoriteIcon");
                    OnPropertyChanged("FavoriteBrush");
                }
            }
        }

        public bool IsSelected
        {
            get { return _isSelected; }
            set
            {
                if (_isSelected != value)
                {
                    _isSelected = value;
                    OnPropertyChanged("IsSelected");
                    RefreshVisualBindings();
                }
            }
        }

        public Brush BackgroundBrush
        {
            get
            {
                if (_isPlaying) return BrushBgPlaying;
                if (_isSelected) return BrushBgSelected;
                return BrushBgDefault;
            }
        }

        public Thickness BorderThickness
        {
            get { return _isPlaying ? new Thickness(1.5) : new Thickness(0); }
        }

        public Brush BorderBrush
        {
            get { return _isPlaying ? _accentBrush : Brushes.Transparent; }
        }

        public Brush TitleBrush
        {
            get { return _isPlaying ? _accentBrush : BrushTextMain; }
        }

        public string TrackIndicator
        {
            get { return _isPlaying ? "♪" : (_index + 1).ToString(); }
        }

        public Brush IndicatorBrush
        {
            get { return _isPlaying ? _accentBrush : BrushTextDim; }
        }

        public string FavoriteIcon
        {
            get { return _isFavorite ? "❤" : "♡"; }
        }

        public Brush FavoriteBrush
        {
            get { return _isFavorite ? _accentBrush : BrushFavDim; }
        }

        public ImageSource ThumbnailSource
        {
            get
            {
                if (_thumbnailSource == null && !_thumbRequested)
                {
                    EnsureThumbnailLoaded();
                }
                return _thumbnailSource;
            }
            private set
            {
                if (_thumbnailSource != value)
                {
                    _thumbnailSource = value;
                    OnPropertyChanged("ThumbnailSource");
                    OnPropertyChanged("FallbackVisibility");
                }
            }
        }

        public TrackItemViewModel(TrackItem model, int index, SolidColorBrush accent, Dispatcher dispatcher)
        {
            _model = model;
            _index = index;
            _accentBrush = accent;
            _dispatcher = dispatcher;
        }

        public void SetAccentBrush(SolidColorBrush accent)
        {
            _accentBrush = accent;
            RefreshVisualBindings();
        }

        public void RefreshVisualBindings()
        {
            OnPropertyChanged("BackgroundBrush");
            OnPropertyChanged("BorderThickness");
            OnPropertyChanged("BorderBrush");
            OnPropertyChanged("TitleBrush");
            OnPropertyChanged("TrackIndicator");
            OnPropertyChanged("IndicatorBrush");
            OnPropertyChanged("FavoriteBrush");
        }

        public void UnloadThumbnail()
        {
            if (_thumbnailSource != null)
            {
                _thumbnailSource = null;
                _thumbRequested = false;
                OnPropertyChanged("ThumbnailSource");
                OnPropertyChanged("FallbackVisibility");
            }
        }

        public void EnsureThumbnailLoaded()
        {
            if (_thumbRequested && _thumbnailSource != null) return;
            _thumbRequested = true;

            string id = Id;
            if (string.IsNullOrEmpty(id)) return;

            // 1. Tenta carregar do cache persistente em disco (Zero impacto de rede)
            if (ThumbnailCache.HasDiskCache(id))
            {
                string diskPath = ThumbnailCache.GetDiskCachePath(id);
                var img = LoadThumbnailImageFast(diskPath);
                if (img != null)
                {
                    ThumbnailSource = img;
                    return;
                }
            }

            // 2. Se no estiver em disco, delega ao ThumbnailCache para download assncrono
            ThumbnailCache.GetOrRequestThumbnail(_model, () =>
            {
                if (_dispatcher != null && !_dispatcher.HasShutdownStarted)
                {
                    _dispatcher.BeginInvoke(new Action(() =>
                    {
                        try
                        {
                            string path = ThumbnailCache.GetDiskCachePath(id);
                            if (File.Exists(path))
                            {
                                var loadedImg = LoadThumbnailImageFast(path);
                                if (loadedImg != null)
                                {
                                    ThumbnailSource = loadedImg;
                                }
                            }
                        }
                        catch { }
                    }), DispatcherPriority.Background);
                }
            });
        }

        public static ImageSource LoadThumbnailImageFast(string filePath)
        {
            if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
                return null;

            try
            {
                byte[] buffer;
                using (var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (var ms = new MemoryStream((int)fs.Length))
                {
                    fs.CopyTo(ms);
                    buffer = ms.ToArray();
                }

                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.StreamSource = new MemoryStream(buffer);
                bitmap.DecodePixelWidth = 320;
                bitmap.EndInit();

                if (bitmap.CanFreeze)
                    bitmap.Freeze();

                return bitmap;
            }
            catch
            {
                return null;
            }
        }

        private static Brush GetChannelBrush(string name)
        {
            if (string.IsNullOrEmpty(name)) return BrushBgDefault;
            int hash = Math.Abs(name.GetHashCode());
            Color[] colors = new Color[]
            {
                Color.FromRgb(192, 57, 43),
                Color.FromRgb(41, 128, 185),
                Color.FromRgb(39, 174, 96),
                Color.FromRgb(142, 68, 173),
                Color.FromRgb(211, 84, 0),
                Color.FromRgb(22, 160, 133),
                Color.FromRgb(44, 62, 80)
            };
            var b = new SolidColorBrush(colors[hash % colors.Length]);
            b.Freeze();
            return b;
        }

        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged(string prop)
        {
            if (PropertyChanged != null) PropertyChanged(this, new PropertyChangedEventArgs(prop));
        }
    }

    #endregion
}
