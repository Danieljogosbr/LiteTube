using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using System.Windows.Forms.Integration;

namespace LiteTube
{
    /// <summary>
    /// Modern LiteTube Media Card & Row Control hosted via WPF ElementHost:
    /// Powered by Direct3D hardware GPU acceleration and native UI Virtualization (container recycling).
    /// Completely eliminates CPU rendering bottlenecks and UI thread stutter during fast catalog scrolling.
    /// </summary>
    public class MediaCardListControl : Panel
    {
        private readonly ElementHost _elementHost;
        private readonly WpfMediaListControl _wpfList;
        private Color _accentColor = Color.FromArgb(255, 30, 60);

        public event Action<int, TrackItem> ItemActivated;
        public event Action<TrackItem> ChannelClicked;
        public event Action<int, TrackItem> FavoriteClicked;
        public event Action<int, TrackItem> AddToQueueClicked;
        public event Action<int, TrackItem> WatchClipClicked;
        public event Action LoadMoreRequested;

        public MediaCardListControl()
        {
            DoubleBuffered = true;
            BackColor = Theme.Bg; // #0A0A0A Preto profundo neutro

            _wpfList = new WpfMediaListControl();

            _elementHost = new ElementHost
            {
                Dock = DockStyle.Fill,
                Child = _wpfList,
                BackColor = Theme.Bg
            };

            // Assegura foco imediato para entrada de mouse e teclado no WPF ao passar o cursor
            _elementHost.MouseEnter += (s, e) =>
            {
                this.Focus();
                if (_elementHost.CanFocus) _elementHost.Focus();
            };

            this.MouseEnter += (s, e) =>
            {
                this.Focus();
            };

            // Redirecionamento manual do MouseWheel do WinForms para o ScrollViewer do WPF
            this.MouseWheel += (sender, e) =>
            {
                var sv = _wpfList != null ? _wpfList.WpfScrollViewer : null;
                if (sv != null)
                {
                    double offset = sv.VerticalOffset - (e.Delta / 3.0);
                    sv.ScrollToVerticalOffset(Math.Max(0, Math.Min(sv.ScrollableHeight, offset)));
                }
                else if (_wpfList != null)
                {
                    _wpfList.ScrollByDelta(e.Delta);
                }
            };

            _elementHost.MouseWheel += (sender, e) =>
            {
                var sv = _wpfList != null ? _wpfList.WpfScrollViewer : null;
                if (sv != null)
                {
                    double offset = sv.VerticalOffset - (e.Delta / 3.0);
                    sv.ScrollToVerticalOffset(Math.Max(0, Math.Min(sv.ScrollableHeight, offset)));
                }
                else if (_wpfList != null)
                {
                    _wpfList.ScrollByDelta(e.Delta);
                }
            };

            // Propagao de eventos da rvore WPF para consumidores WinForms
            _wpfList.ItemActivated += (idx, item) =>
            {
                if (ItemActivated != null) ItemActivated(idx, item);
            };

            _wpfList.ChannelClicked += item =>
            {
                if (ChannelClicked != null) ChannelClicked(item);
            };

            _wpfList.FavoriteClicked += (idx, item) =>
            {
                if (FavoriteClicked != null) FavoriteClicked(idx, item);
            };

            _wpfList.AddToQueueClicked += (idx, item) =>
            {
                if (AddToQueueClicked != null) AddToQueueClicked(idx, item);
            };

            _wpfList.WatchClipClicked += (idx, item) =>
            {
                if (WatchClipClicked != null) WatchClipClicked(idx, item);
            };

            _wpfList.LoadMoreRequested += () =>
            {
                if (LoadMoreRequested != null) LoadMoreRequested();
            };

            Controls.Add(_elementHost);
        }

        #region Public Properties

        public bool IsGridMode
        {
            get { return _wpfList.IsGridMode; }
            set { _wpfList.IsGridMode = value; }
        }

        public bool IsCompactSidebarMode
        {
            get { return _wpfList.IsCompactSidebarMode; }
            set { _wpfList.IsCompactSidebarMode = value; }
        }

        public bool IsMusicEnvironment
        {
            get { return _wpfList.IsMusicEnvironment; }
            set { _wpfList.IsMusicEnvironment = value; }
        }

        public bool HasMoreItems
        {
            get { return _wpfList.HasMoreItems; }
            set { _wpfList.HasMoreItems = value; }
        }

        public bool IsLoadingMore
        {
            get { return _wpfList.IsLoadingMore; }
            set { _wpfList.IsLoadingMore = value; }
        }

        public Color AccentColor
        {
            get { return _accentColor; }
            set
            {
                _accentColor = value;
                _wpfList.SetAccentColor(value.R, value.G, value.B);
            }
        }

        public Func<string, bool> IsFavoriteOrSavedCheck
        {
            get { return _wpfList.IsFavoriteOrSavedCheck; }
            set { _wpfList.IsFavoriteOrSavedCheck = value; }
        }

        public bool SingleClickActivates
        {
            get { return _wpfList.SingleClickActivates; }
            set { _wpfList.SingleClickActivates = value; }
        }

        public int SelectedIndex
        {
            get { return _wpfList.SelectedIndex; }
            set { _wpfList.SelectedIndex = value; }
        }

        public TrackItem SelectedItem
        {
            get { return _wpfList.SelectedItem; }
        }

        public string PlayingVideoId
        {
            get { return _wpfList.PlayingVideoId; }
            set { _wpfList.PlayingVideoId = value; }
        }

        public List<TrackItem> Items
        {
            get { return _wpfList.Items; }
        }

        public bool IsSuspended
        {
            get { return _wpfList != null && _wpfList.IsSuspended; }
        }

        public double ScrollOffset
        {
            get { return _wpfList != null ? _wpfList.ScrollOffset : 0; }
            set { if (_wpfList != null) _wpfList.ScrollOffset = value; }
        }

        #endregion

        #region Public Methods

        public void SuspendAndDetach()
        {
            if (_wpfList != null) _wpfList.SuspendAndDetach();
        }

        public void ResumeAndAttach()
        {
            if (_wpfList != null) _wpfList.ResumeAndAttach();
        }

        public void ClearItems(bool keepInMemory = false)
        {
            if (_wpfList != null) _wpfList.ClearItems(keepInMemory);
        }

        public void Clear()
        {
            ClearItems(false);
        }

        public void SetItems(List<TrackItem> items, bool resetScroll = true)
        {
            _wpfList.SetItems(items, resetScroll);
        }

        public int AppendItems(List<TrackItem> moreItems)
        {
            return _wpfList.AppendItems(moreItems);
        }

        public void ScrollToTop()
        {
            _wpfList.ScrollToTop();
        }

        public void PruneOffscreenResources()
        {
            _wpfList.PruneOffscreenResources();
        }

        public bool IsItemVisible(string id)
        {
            return _wpfList.IsItemVisible(id);
        }

        public void InvalidateItem(int index)
        {
            _wpfList.InvalidateItem(index);
        }

        public void InvalidateCardById(string targetId)
        {
            _wpfList.InvalidateCardById(targetId);
        }

        public new void Invalidate()
        {
            base.Invalidate();
            if (_wpfList != null && !_wpfList.IsSuspended)
            {
                _wpfList.RefreshVisuals();
            }
        }

        #endregion

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            var sv = _wpfList != null ? _wpfList.WpfScrollViewer : null;
            if (sv != null)
            {
                double offset = sv.VerticalOffset - (e.Delta / 3.0);
                sv.ScrollToVerticalOffset(Math.Max(0, Math.Min(sv.ScrollableHeight, offset)));
            }
            else if (_wpfList != null)
            {
                _wpfList.ScrollByDelta(e.Delta);
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (_elementHost != null)
                {
                    _elementHost.Dispose();
                }
            }
            base.Dispose(disposing);
        }
    }
}
