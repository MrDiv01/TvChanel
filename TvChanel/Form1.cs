using System.Runtime.InteropServices;
using LibVLCSharp.Shared;
using LibVLCSharp.WinForms;

namespace TvChanel;

public partial class Form1 : Form
{
    private static readonly Color Bg = Color.FromArgb(11, 15, 20);
    private static readonly Color PanelBg = Color.FromArgb(17, 22, 29);
    private static readonly Color FieldBg = Color.FromArgb(28, 35, 46);
    private static readonly Color Accent = Color.FromArgb(245, 166, 35);
    private static readonly Color TextMain = Color.FromArgb(236, 240, 244);
    private static readonly Color Muted = Color.FromArgb(160, 170, 184);
    private static readonly Color Geo = Color.FromArgb(242, 176, 92);

    private readonly IptvCatalog _catalog = new();
    private readonly MatchGuide _matches = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly System.Windows.Forms.Timer _searchTimer = new() { Interval = 180 };

    private ComboBox _category = null!;
    private ComboBox _country = null!;
    private TextBox _search = null!;
    private Label _count = null!;
    private ListView _list = null!;
    private SplitContainer _split = null!;
    private VideoView _video = null!;
    private Label _now = null!;
    private TrackBar _volume = null!;
    private ToolStripStatusLabel _status = null!;
    private Button _refresh = null!;
    private Button _hideList = null!;
    private Panel _matchHost = null!;
    private Label _matchTitle = null!;
    private ListView _matchList = null!;
    private TextBox _matchDetail = null!;
    private CancellationTokenSource? _matchLookup;
    private List<ListedMatch> _channelMatches = [];
    private bool _fillingMatches;

    private LibVLC? _libvlc;
    private MediaPlayer? _player;
    private Media? _media;

    private List<PlayableChannel> _all = [];
    private List<PlayableChannel> _visible = [];
    private bool _suppress;
    private bool _listHidden;

    public Form1()
    {
        InitializeComponent();
        BuildInterface();
        InitPlayer();
        _searchTimer.Tick += (_, _) =>
        {
            _searchTimer.Stop();
            ApplyFilter();
        };
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        UseDarkTitleBar();
        try
        {
            _split.SplitterDistance = 470;
        }
        catch (InvalidOperationException)
        {
            // Splitter is not ready until the handle exists with a real width.
        }
    }

    protected override async void OnShown(EventArgs e)
    {
        base.OnShown(e);
        await LoadCatalogAsync(force: false);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        _lifetime.Cancel();
        _matchLookup?.Cancel();
        _searchTimer.Stop();
        _searchTimer.Dispose();

        if (_player != null)
        {
            _player.Stop();
            _video.MediaPlayer = null;
            _media?.Dispose();
            _player.Dispose();
            _libvlc?.Dispose();
        }

        base.OnFormClosing(e);
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == Keys.F11)
        {
            ToggleList();
            return true;
        }

        if (keyData == Keys.Escape && _listHidden)
        {
            ToggleList();
            return true;
        }

        return base.ProcessCmdKey(ref msg, keyData);
    }

    private void BuildInterface()
    {
        BackColor = Bg;
        ForeColor = TextMain;
        Font = new Font("Segoe UI", 10F);
        Text = "TV Kanallar";
            ClientSize = new Size(1280, 820);
            MinimumSize = new Size(980, 700);
        StartPosition = FormStartPosition.CenterScreen;
        KeyPreview = true;

        var top = new Panel
        {
            Dock = DockStyle.Top,
            Height = 62,
            BackColor = PanelBg,
            Padding = new Padding(12, 10, 12, 8)
        };

        _category = MakeCombo(190);
        _country = MakeCombo(210);
        _search = new TextBox
        {
            Width = 240,
            BorderStyle = BorderStyle.FixedSingle,
            BackColor = FieldBg,
            ForeColor = TextMain,
            PlaceholderText = "Kanal adı...",
            Margin = new Padding(0, 6, 12, 0)
        };
        _refresh = MakeButton("Yenilə", 88, filled: false);
        _count = new Label
        {
            Dock = DockStyle.Right,
            Width = 150,
            TextAlign = ContentAlignment.MiddleRight,
            ForeColor = Muted,
            Text = "0 kanal",
            Padding = new Padding(0, 0, 4, 0)
        };

        var filters = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            BackColor = PanelBg
        };
        filters.Controls.Add(MakeCaption("Kateqoriya"));
        filters.Controls.Add(_category);
        filters.Controls.Add(MakeCaption("Ölkə"));
        filters.Controls.Add(_country);
        filters.Controls.Add(MakeCaption("Axtar"));
        filters.Controls.Add(_search);
        filters.Controls.Add(_refresh);

        top.Controls.Add(filters);
        top.Controls.Add(_count);

        _list = new ListView
        {
            Dock = DockStyle.Fill,
            View = View.Details,
            FullRowSelect = true,
            MultiSelect = false,
            HideSelection = false,
            VirtualMode = true,
            BorderStyle = BorderStyle.None,
            BackColor = Color.FromArgb(14, 18, 24),
            ForeColor = TextMain,
            HeaderStyle = ColumnHeaderStyle.Nonclickable,
            Activation = ItemActivation.Standard
        };
        _list.Columns.Add("Kanal", 250);
        _list.Columns.Add("Ölkə", 130);
        _list.Columns.Add("Keyfiyyət", 80);
        _list.Columns.Add("Qeyd", 130);
        _list.RetrieveVirtualItem += OnRetrieveItem;
        _list.ItemActivate += (_, _) => PlaySelected();
        _list.Resize += (_, _) => StretchNameColumn();
        _list.HandleCreated += (_, _) => SetWindowTheme(_list.Handle, "DarkMode_Explorer", null);

        _video = new VideoView
        {
            Dock = DockStyle.Fill,
            BackColor = Color.Black
        };

        _now = new Label
        {
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = TextMain,
            Padding = new Padding(10, 0, 8, 0),
            Text = "Siyahıdan kanal seç"
        };

        var watch = MakeButton("Bax", 84, filled: true);
        var stop = MakeButton("Dayandır", 100, filled: false);
        _hideList = MakeButton("Gizlət", 84, filled: false);
        _volume = new TrackBar
        {
            Minimum = 0,
            Maximum = 100,
            Value = 80,
            TickFrequency = 25,
            Width = 120,
            Height = 32,
            BackColor = PanelBg,
            Margin = new Padding(0, 4, 0, 0)
        };

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Left,
            Width = 290,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            BackColor = PanelBg,
            Padding = new Padding(8, 8, 0, 0)
        };
        buttons.Controls.Add(watch);
        buttons.Controls.Add(stop);
        buttons.Controls.Add(_hideList);

        var volumeBox = new FlowLayoutPanel
        {
            Dock = DockStyle.Right,
            Width = 190,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            BackColor = PanelBg,
            Padding = new Padding(0, 10, 8, 0)
        };
        volumeBox.Controls.Add(new Label
        {
            Text = "Səs",
            AutoSize = true,
            ForeColor = Muted,
            Margin = new Padding(0, 6, 6, 0)
        });
        volumeBox.Controls.Add(_volume);

        var playerBar = new Panel
        {
            Dock = DockStyle.Bottom,
            Height = 52,
            BackColor = PanelBg
        };
        playerBar.Controls.Add(_now);
        playerBar.Controls.Add(volumeBox);
        playerBar.Controls.Add(buttons);

        _matchTitle = new Label
        {
            Dock = DockStyle.Top,
            Height = 28,
            Text = "Bu kanaldakı matçlar",
            ForeColor = Accent,
            BackColor = PanelBg,
            Padding = new Padding(10, 6, 8, 0)
        };
        _matchList = new ListView
        {
            Dock = DockStyle.Left,
            Width = 340,
            View = View.Details,
            FullRowSelect = true,
            MultiSelect = false,
            HideSelection = false,
            BorderStyle = BorderStyle.None,
            BackColor = Color.FromArgb(14, 18, 24),
            ForeColor = TextMain,
            HeaderStyle = ColumnHeaderStyle.Nonclickable
        };
        _matchList.Columns.Add("Saat", 78);
        _matchList.Columns.Add("Matç", 250);
        _matchList.HandleCreated += (_, _) => SetWindowTheme(_matchList.Handle, "DarkMode_Explorer", null);
        _matchList.SelectedIndexChanged += async (_, _) => await OnMatchSelectedAsync();
        _matchDetail = new TextBox
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            ReadOnly = true,
            BorderStyle = BorderStyle.None,
            BackColor = Color.FromArgb(14, 18, 24),
            ForeColor = TextMain,
            Font = new Font("Segoe UI", 9.5F),
            ScrollBars = ScrollBars.Vertical,
            Text = "Matça bir dəfə bas. Burada bukmeker xətti, forma və statistika açılır."
        };
        _matchHost = new Panel
        {
            Dock = DockStyle.Bottom,
            Height = 250,
            Visible = false,
            BackColor = PanelBg,
            Padding = new Padding(0, 0, 0, 0)
        };
        _matchHost.Controls.Add(_matchDetail);
        _matchHost.Controls.Add(_matchList);
        _matchHost.Controls.Add(_matchTitle);

        var stage = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = Color.Black,
            Padding = new Padding(0)
        };
        stage.Controls.Add(_video);
        stage.Controls.Add(_matchHost);
        stage.Controls.Add(playerBar);

        _split = new SplitContainer
        {
            Dock = DockStyle.Fill,
            BackColor = Bg,
            FixedPanel = FixedPanel.Panel1,
            SplitterWidth = 6
        };
        _split.Panel1.BackColor = Bg;
        _split.Panel2.BackColor = Color.Black;
        _split.Panel1.Controls.Add(_list);
        _split.Panel2.Controls.Add(stage);

        var status = new StatusStrip
        {
            BackColor = PanelBg,
            ForeColor = Muted,
            SizingGrip = false
        };
        _status = new ToolStripStatusLabel("Hazır")
        {
            ForeColor = Muted,
            Spring = true,
            TextAlign = ContentAlignment.MiddleLeft
        };
        status.Items.Add(_status);

        Controls.Add(_split);
        Controls.Add(top);
        Controls.Add(status);

        _category.SelectedIndexChanged += (_, _) => { if (!_suppress) ApplyFilter(); };
        _country.SelectedIndexChanged += (_, _) => { if (!_suppress) ApplyFilter(); };
        _search.TextChanged += (_, _) =>
        {
            _searchTimer.Stop();
            _searchTimer.Start();
        };
        _refresh.Click += async (_, _) => await LoadCatalogAsync(force: true);
        watch.Click += (_, _) => PlaySelected();
        stop.Click += (_, _) => StopPlayback();
        _hideList.Click += (_, _) => ToggleList();
        _volume.ValueChanged += (_, _) =>
        {
            if (_player != null)
                _player.Volume = _volume.Value;
        };
    }

    private void InitPlayer()
    {
        try
        {
            Core.Initialize();
            _libvlc = new LibVLC(
                "--no-video-title-show",
                "--avcodec-hw=any",
                "--network-caching=400",
                "--live-caching=400",
                "--drop-late-frames",
                "--skip-frames");
            _player = new MediaPlayer(_libvlc) { Volume = _volume.Value };
            _video.MediaPlayer = _player;
            _player.Playing += (_, _) => Ui(() => SetStatus("Oynadılır"));
            _player.EncounteredError += (_, _) => Ui(() => SetStatus("Bu yayım açılmadı. Başqa kanal yoxla."));
            _player.EndReached += (_, _) => Ui(() => SetStatus("Yayım bitdi"));
        }
        catch (Exception ex)
        {
            SetStatus("Pleyer açılmadı: " + ex.Message);
            MessageBox.Show(
                this,
                "Video pleyer yüklənmədi. LibVLC faylları tapılmadı.\n\n" + ex.Message,
                "TV Kanallar",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
    }

    private async Task LoadCatalogAsync(bool force)
    {
        if (_lifetime.IsCancellationRequested)
            return;

        SetBusy(true);
        SetStatus(force ? "Kataloq yenilənir..." : "İdman kanalları yüklənir...");

        try
        {
            var snapshot = await _catalog.LoadAsync(force, _lifetime.Token);
            BindCatalog(snapshot);
            SetStatus("Hazırdır. İstədiyin kanala iki dəfə bas və ya Bax düyməsini istifadə et.");
        }
        catch (OperationCanceledException)
        {
            // Form is closing.
        }
        catch (Exception ex)
        {
            SetStatus("Kataloq yüklənmədi");
            MessageBox.Show(
                this,
                "Kanallar yüklənmədi. İnterneti yoxla və Yenilə düyməsinə bas.\n\n" + ex.Message,
                "TV Kanallar",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void BindCatalog(CatalogSnapshot snapshot)
    {
        _all = snapshot.Channels.ToList();

        _suppress = true;
        try
        {
            _category.Items.Clear();
            _category.Items.Add(new ComboEntry("", "Hamısı"));
            foreach (var category in snapshot.Categories)
                _category.Items.Add(new ComboEntry(category.Id, category.Name));

            var sports = IndexOfEntry(_category, "sports");
            _category.SelectedIndex = sports >= 0 ? sports : 0;

            _country.Items.Clear();
            _country.Items.Add(new ComboEntry("", "Hamısı"));
            foreach (var country in snapshot.Countries)
                _country.Items.Add(new ComboEntry(country.Code, country.Name));

            _country.SelectedIndex = 0;
        }
        finally
        {
            _suppress = false;
        }

        ApplyFilter();
    }

    private void ApplyFilter()
    {
        if (_category.SelectedItem is not ComboEntry category || _country.SelectedItem is not ComboEntry country)
        {
            _visible = [];
            _list.VirtualListSize = 0;
            _count.Text = "0 kanal";
            return;
        }

        var query = _search.Text.Trim();
        IEnumerable<PlayableChannel> queryable = _all;

        if (category.Id.Length > 0)
        {
            queryable = queryable.Where(channel =>
                channel.Categories.Any(id => id.Equals(category.Id, StringComparison.OrdinalIgnoreCase)));
        }

        if (country.Id.Length > 0)
        {
            queryable = queryable.Where(channel =>
                channel.CountryCode.Equals(country.Id, StringComparison.OrdinalIgnoreCase));
        }

        if (query.Length > 0)
        {
            queryable = queryable.Where(channel =>
                channel.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
                || channel.CountryName.Contains(query, StringComparison.OrdinalIgnoreCase)
                || channel.CountryCode.Contains(query, StringComparison.OrdinalIgnoreCase));
        }

        _visible = queryable.ToList();
        _list.BeginUpdate();
        _list.VirtualListSize = 0;
        _list.VirtualListSize = _visible.Count;
        _list.EndUpdate();
        _count.Text = $"{_visible.Count:N0} kanal";
        StretchNameColumn();
    }

    private void OnRetrieveItem(object? sender, RetrieveVirtualItemEventArgs e)
    {
        if (e.ItemIndex < 0 || e.ItemIndex >= _visible.Count)
        {
            e.Item = new ListViewItem(string.Empty);
            return;
        }

        var channel = _visible[e.ItemIndex];
        var item = new ListViewItem(channel.Name);
        item.SubItems.Add(channel.CountryName);
        item.SubItems.Add(channel.Quality ?? "");
        item.SubItems.Add(channel.Labels);
        item.ForeColor = channel.IsGeoBlocked ? Geo : TextMain;
        item.BackColor = Color.FromArgb(14, 18, 24);
        e.Item = item;
    }

    private void PlaySelected()
    {
        if (_player == null || _libvlc == null)
            return;
        if (_list.SelectedIndices.Count == 0)
        {
            SetStatus("Siyahıdan kanal seç");
            return;
        }

        var index = _list.SelectedIndices[0];
        if (index < 0 || index >= _visible.Count)
            return;

        var channel = _visible[index];
        _player.Stop();
        _media?.Dispose();

        _media = new Media(_libvlc, channel.Url, FromType.FromLocation);
        _media.AddOption(":network-caching=400");
        _media.AddOption(":live-caching=400");
        _media.AddOption(":clock-jitter=0");
        _media.AddOption(":clock-synchro=0");
        _media.AddOption(":http-reconnect");
        if (!string.IsNullOrWhiteSpace(channel.Referrer))
            _media.AddOption(":http-referrer=" + channel.Referrer);
        if (!string.IsNullOrWhiteSpace(channel.UserAgent))
            _media.AddOption(":http-user-agent=" + channel.UserAgent);

        _player.Volume = _volume.Value;
        _player.Play(_media);
        _now.Text = channel.Name;
        Text = channel.Name + " — TV Kanallar";
        SetStatus("Açılır: " + channel.Name);
        StartMatchLookup(channel);
    }

    private void StartMatchLookup(PlayableChannel channel)
    {
        _matchLookup?.Cancel();
        _matchLookup?.Dispose();
        _matchLookup = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);

        var sports = channel.Categories.Any(id => id.Equals("sports", StringComparison.OrdinalIgnoreCase))
            || channel.Name.Contains("sport", StringComparison.OrdinalIgnoreCase)
            || channel.Name.Contains("spor", StringComparison.OrdinalIgnoreCase);
        if (!sports)
        {
            _matchHost.Visible = false;
            return;
        }

        _matchHost.Visible = true;
        _fillingMatches = true;
        _matchList.Items.Clear();
        _channelMatches = [];
        _fillingMatches = false;
        _matchTitle.Text = "Matçlar yoxlanılır...";
        _matchDetail.Text = "Bu kanalın ölkəsi üzrə yaxın futbol yayımı axtarılır.";
        _ = LoadMatchesAsync(channel, _matchLookup.Token);
    }

    private async Task LoadMatchesAsync(PlayableChannel channel, CancellationToken cancellationToken)
    {
        try
        {
            var matches = await _matches.ForChannelAsync(channel, cancellationToken);
            if (cancellationToken.IsCancellationRequested || IsDisposed)
                return;

            _channelMatches = matches.ToList();
            _fillingMatches = true;
            _matchList.BeginUpdate();
            _matchList.Items.Clear();
            foreach (var match in _channelMatches)
            {
                var when = match.KickoffUtc.ToLocalTime();
                var item = new ListViewItem(when.ToString("dd.MM HH:mm"));
                var score = match.Started && match.Score.Length > 0 ? $"  {match.Score}" : "";
                item.SubItems.Add(match.Title + score);
                item.ForeColor = TextMain;
                _matchList.Items.Add(item);
            }

            _matchList.EndUpdate();
            _fillingMatches = false;
            var country = string.IsNullOrWhiteSpace(channel.CountryCode) ? "AZ" : channel.CountryCode.ToUpperInvariant();
            _matchTitle.Text = _channelMatches.Count == 0
                ? "Bu kanalda yaxın matç tapılmadı"
                : $"Bu kanaldakı matçlar · {_channelMatches.Count}";
            _matchDetail.Text = _channelMatches.Count == 0
                ? $"Yayım cədvəli {country} ölkəsi üçündür. Kanal adı ilə televiziya stansiyası uyğun gəlmirsə, matç siyahısı boş qalır. Matç elan olunanda Yenidən kanalı aç."
                : "Matça bas. Sağda bukmeker xətti, forma, qarşılıqlı oyunlar və statistika görünəcək.";
        }
        catch (OperationCanceledException)
        {
            // A newer channel replaced this lookup.
        }
        catch (Exception ex)
        {
            if (cancellationToken.IsCancellationRequested || IsDisposed)
                return;
            _matchTitle.Text = "Matçlar yüklənmədi";
            _matchDetail.Text = ex.Message;
        }
    }

    private async Task OnMatchSelectedAsync()
    {
        if (_fillingMatches || _matchList.SelectedIndices.Count == 0)
            return;
        var index = _matchList.SelectedIndices[0];
        if (index < 0 || index >= _channelMatches.Count)
            return;

        var match = _channelMatches[index];
        _matchDetail.Text = "Statistika açılır...";
        var token = _matchLookup?.Token ?? CancellationToken.None;
        try
        {
            var briefing = await _matches.BriefingAsync(match, token);
            if (token.IsCancellationRequested || IsDisposed)
                return;
            if (_matchList.SelectedIndices.Count == 0 || _matchList.SelectedIndices[0] != index)
                return;
            _matchDetail.Text = briefing.Text;
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            if (!token.IsCancellationRequested && !IsDisposed)
                _matchDetail.Text = "Statistika açılmadı.\r\n" + ex.Message;
        }
    }

    private void StopPlayback()
    {
        _player?.Stop();
        _now.Text = "Dayandırıldı";
        Text = "TV Kanallar";
        SetStatus("Dayandırıldı");
    }

    private void ToggleList()
    {
        _listHidden = !_listHidden;
        _split.Panel1Collapsed = _listHidden;
        _hideList.Text = _listHidden ? "Siyahı" : "Gizlət";
    }

    private void StretchNameColumn()
    {
        if (_list.Columns.Count < 4 || _list.ClientSize.Width <= 0)
            return;

        var used = _list.Columns[1].Width + _list.Columns[2].Width + _list.Columns[3].Width + SystemInformation.VerticalScrollBarWidth + 8;
        _list.Columns[0].Width = Math.Max(180, _list.ClientSize.Width - used);
    }

    private void SetBusy(bool busy)
    {
        _refresh.Enabled = !busy;
        _category.Enabled = !busy;
        _country.Enabled = !busy;
        _search.Enabled = !busy;
        UseWaitCursor = busy;
    }

    private void SetStatus(string text) => _status.Text = text;

    private void Ui(Action action)
    {
        if (IsDisposed || !IsHandleCreated)
            return;
        if (InvokeRequired)
            BeginInvoke(action);
        else
            action();
    }

    private void UseDarkTitleBar()
    {
        var useDark = 1;
        DwmSetWindowAttribute(Handle, 20, ref useDark, sizeof(int));
    }

    private static int IndexOfEntry(ComboBox combo, string id)
    {
        for (var i = 0; i < combo.Items.Count; i++)
        {
            if (combo.Items[i] is ComboEntry entry && entry.Id.Equals(id, StringComparison.OrdinalIgnoreCase))
                return i;
        }

        return -1;
    }

    private static Label MakeCaption(string text) => new()
    {
        Text = text,
        AutoSize = true,
        ForeColor = Muted,
        Margin = new Padding(0, 8, 6, 0)
    };

    private static ComboBox MakeCombo(int width)
    {
        var combo = new ComboBox
        {
            Width = width,
            DropDownStyle = ComboBoxStyle.DropDownList,
            FlatStyle = FlatStyle.Flat,
            BackColor = FieldBg,
            ForeColor = TextMain,
            Margin = new Padding(0, 4, 14, 0)
        };
        combo.HandleCreated += (_, _) => SetWindowTheme(combo.Handle, "DarkMode_Explorer", null);
        return combo;
    }

    private static Button MakeButton(string text, int width, bool filled)
    {
        var button = new Button
        {
            Text = text,
            Width = width,
            Height = 32,
            FlatStyle = FlatStyle.Flat,
            BackColor = filled ? Accent : FieldBg,
            ForeColor = filled ? Color.FromArgb(24, 18, 6) : TextMain,
            Margin = new Padding(0, 2, 8, 0),
            Cursor = Cursors.Hand
        };
        button.FlatAppearance.BorderSize = filled ? 0 : 1;
        button.FlatAppearance.BorderColor = Color.FromArgb(55, 65, 80);
        return button;
    }

    [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
    private static extern int SetWindowTheme(IntPtr hwnd, string appName, string? idList);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    private sealed class ComboEntry(string id, string label)
    {
        public string Id { get; } = id;
        public string Label { get; } = label;
        public override string ToString() => Label;
    }
}
