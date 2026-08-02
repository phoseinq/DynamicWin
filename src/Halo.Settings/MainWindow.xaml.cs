using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace Halo.Settings;

public partial class MainWindow : Window
{
    private readonly Store _store = new();
    private PageId _page = PageId.Home;
    private readonly Dictionary<PageId, Button> _nav = [];

    private static readonly FontFamily Fluent = new("Segoe Fluent Icons");

    private static readonly string ChevronDown = ((char)0xE70D).ToString();
    private static readonly string ChevronUp = ((char)0xE70E).ToString();

    private static readonly FontFamily Display = new("Segoe UI Variable Display, Segoe UI");
    private static readonly FontFamily Mono = new("Cascadia Mono, Consolas");

    private bool _closing;

    public MainWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) => Glass.Apply(this);
        BuildNav();
        ShowPage(PageId.Home);
        Draft();
    }

    private void Draft()
    {
        int n = _store.PendingCount;
        DraftBar.Visibility = n > 0 ? Visibility.Visible : Visibility.Collapsed;
        DraftState.Text = n == 1 ? "1 change waiting" : $"{n} changes waiting";
        if (_restore != null) _restore.IsEnabled = n > 0;
    }

    private Button? _restore;

    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        _store.Apply();
        ShowPage(_page);
        Draft();
    }

    private void Reset_Click(object sender, RoutedEventArgs e)
    {
        _store.Discard();
        ShowPage(_page);
        Draft();
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (!_closing && _store.IsDirty)
        {
            e.Cancel = true;
            int n = _store.PendingCount;
            GuardDetail.Text = n == 1
                ? "One change has not been applied yet. Halo is still running the previous settings."
                : $"{n} changes have not been applied yet. Halo is still running the previous settings.";
            Guard.Visibility = Visibility.Visible;
            return;
        }
        base.OnClosing(e);
    }

    private void GuardCancel_Click(object sender, RoutedEventArgs e) => Guard.Visibility = Visibility.Collapsed;

    private void GuardScrim_Click(object sender, MouseButtonEventArgs e)
        => Guard.Visibility = Visibility.Collapsed;

    private void GuardCard_Click(object sender, MouseButtonEventArgs e) => e.Handled = true;

    private void GuardDiscard_Click(object sender, RoutedEventArgs e)
    {
        _store.Discard();
        _closing = true;
        Close();
    }

    private Brush Ink => (Brush)FindResource("Ink");
    private Brush Secondary => (Brush)FindResource("Secondary");
    private Brush Quiet => (Brush)FindResource("Quiet");
    private Brush FrostEdge => (Brush)FindResource("FrostEdge");
    private Brush Mint => (Brush)FindResource("Mint");
    private Brush Coral => (Brush)FindResource("Coral");
    private Brush Graphite => (Brush)FindResource("Graphite");

    private static SolidColorBrush Accent(PageId page, byte alpha = 0xFF)
    {
        var (r, g, b) = Catalog.Accent(page);
        return new SolidColorBrush(Color.FromArgb(alpha, r, g, b));
    }

    private static TextBlock Glyph(PageId page, Brush foreground, double size = 20) => new()
    {
        Text = Catalog.Glyph(page),
        FontFamily = Fluent,
        FontSize = size,
        Foreground = foreground,
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
    };

    private TextBlock Eyebrow(string text, Thickness margin) => new()
    {
        Text = text,
        Foreground = Quiet,
        FontFamily = Mono,
        FontWeight = FontWeights.SemiBold,
        FontSize = 9.5,
        Margin = margin,
    };

    private void BuildNav()
    {
        foreach (var group in Catalog.Nav)
        {
            if (group.Header.Length > 0)
                NavPanel.Children.Add(Eyebrow(group.Header, new Thickness(17, 9, 0, 4)));
            foreach (var id in group.Pages) NavPanel.Children.Add(NavItem(Catalog.Get(id)));
        }
    }

    private Button NavItem(Page page)
    {
        var content = new Grid();
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(22) });
        content.ColumnDefinitions.Add(new ColumnDefinition());

        content.Children.Add(Glyph(page.Id, Quiet));
        var label = new TextBlock
        {
            Text = page.Label,
            Foreground = Secondary,
            FontSize = 12,
            FontWeight = FontWeights.Medium,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(9, 0, 0, 0),
        };
        Grid.SetColumn(label, 1);
        content.Children.Add(label);

        var button = new Button { Style = (Style)FindResource("NavItem"), Content = content, Tag = page.Id };
        button.Click += (_, _) => ShowPage(page.Id);
        _nav[page.Id] = button;
        return button;
    }

    private void Paint(PageId selected)
    {
        foreach (var (id, button) in _nav)
        {
            bool on = id == selected;
            button.Background = on ? Accent(id, 0x25) : (Brush)FindResource("Frost");
            button.BorderBrush = on ? Accent(id) : FrostEdge;
            button.BorderThickness = on ? new Thickness(3, 1, 1, 1) : new Thickness(1);
            if (button.Content is not Grid grid) continue;
            if (grid.Children[0] is TextBlock icon) icon.Foreground = on ? Accent(id) : Quiet;
            if (grid.Children[1] is TextBlock label)
            {
                label.Foreground = on ? Ink : Secondary;
                label.FontWeight = on ? FontWeights.SemiBold : FontWeights.Medium;
            }
        }
    }

    internal void PreviewScrollToEnd() => DetailScroll.ScrollToEnd();

    internal void Preview(PageId id, string mode = "")
    {
        if (mode is "dirty" or "guard") _store.Set("appearance.motion", "Standard", "Soft");

        if (mode == "roundtrip")
        {
            _store.Set("appearance.motion", "Standard", "Soft");
            _store.Set("appearance.motion", "Soft", "Soft");
        }
        ShowPage(id);
        Draft();

        if (mode == "guard")
        {
            GuardDetail.Text = "One change has not been applied yet. Halo is still running the "
                             + "previous settings.";
            Guard.Visibility = Visibility.Visible;
        }
    }

    private void ShowPage(PageId id)
    {
        _page = id;
        var page = Catalog.Get(id);
        Paint(id);
        PageTitle.Text = page.Label;
        PageDescription.Text = page.Description;
        DetailScroll.ScrollToTop();
        ContentPanel.Children.Clear();

        _restore = null;
        if (id == PageId.Home) { BuildHome(); return; }

        foreach (var section in page.Sections)
        {
            ContentPanel.Children.Add(Heading(section, ContentPanel.Children.Count == 0 ? 3 : 14));
            ContentPanel.Children.Add(BuildSection(section));
        }
    }

    private FrameworkElement Heading(Section section, double top)
    {
        var panel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(3, top, 0, 7),
        };
        panel.Children.Add(new TextBlock
        {
            Text = section.Glyph,
            FontFamily = Fluent,
            FontSize = 12,
            Foreground = Accent(_page, 0xD8),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 7, 0),
        });
        var label = Eyebrow(section.Label, new Thickness(0));
        label.VerticalAlignment = VerticalAlignment.Center;
        panel.Children.Add(label);
        return panel;
    }

    private Border BuildSection(Section section)
    {
        var rows = new StackPanel();
        for (int i = 0; i < section.Rows.Count; i++)
            rows.Children.Add(BuildRow(section.Rows[i], i == section.Rows.Count - 1));

        return new Border
        {
            CornerRadius = new CornerRadius(16),
            BorderBrush = FrostEdge,
            BorderThickness = new Thickness(1),
            Background = new SolidColorBrush(Color.FromArgb(0x22, 0x0F, 0x14, 0x1C)),
            Margin = new Thickness(0, 0, 2, 2),
            Child = rows,
        };
    }

    private Border BuildRow(Row row, bool last)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var copy = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 18, 0) };
        copy.Children.Add(new TextBlock
        {
            Text = row.Label,
            Foreground = Ink,
            FontWeight = FontWeights.SemiBold,
            FontSize = 12.5,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        if (row.Description.Length > 0)
            copy.Children.Add(new TextBlock
            {
                Text = row.Description,
                Foreground = Secondary,
                FontSize = 11.5,
                Margin = new Thickness(0, 3, 0, 0),
                TextWrapping = TextWrapping.Wrap,
            });
        grid.Children.Add(copy);

        var control = row.Kind switch
        {
            RowKind.Toggle => BuildToggle(row),
            RowKind.Choice => BuildChoice(row),
            RowKind.Slider => BuildSlider(row),
            RowKind.Action => BuildAction(row),
            _ => BuildStatus(row),
        };
        control.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(control, 1);
        grid.Children.Add(control);

        return new Border
        {
            MinHeight = 66,
            Padding = new Thickness(16, 9, 14, 9),
            BorderBrush = FrostEdge,
            BorderThickness = last ? new Thickness(0) : new Thickness(0, 0, 0, 1),
            Child = grid,
        };
    }

    private FrameworkElement BuildToggle(Row row)
    {
        bool on = _store.Bool(row.Key, row.Fallback == "on");
        var track = new Grid { Width = 46, Height = 26 };
        var fill = new Border { CornerRadius = new CornerRadius(13), Background = on ? Mint : Graphite };
        var knob = new Ellipse
        {
            Width = 19,
            Height = 19,
            Fill = on ? Deep : Ink,
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(3.5),
            RenderTransform = new TranslateTransform(on ? 20 : 0, 0),
        };
        track.Children.Add(fill);
        track.Children.Add(knob);

        var button = Bare(row, 52, 38);
        button.Content = track;
        button.Click += (_, _) =>
        {
            on = !on;
            _store.Set(row.Key, on ? "on" : "off", row.Fallback);
            Draft();
            fill.Background = on ? Mint : Graphite;
            knob.Fill = on ? Deep : Ink;
            knob.RenderTransform = new TranslateTransform(on ? 20 : 0, 0);
        };
        return button;
    }

    private static readonly SolidColorBrush Deep = new(Color.FromRgb(0x10, 0x36, 0x29));

    private FrameworkElement BuildChoice(Row row)
    {
        string value = _store.Text(row.Key, row.Fallback);

        var panel = new Grid();
        panel.ColumnDefinitions.Add(new ColumnDefinition());
        panel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
        var valueText = new TextBlock
        {
            Text = value,
            Foreground = Ink,
            FontSize = 11.5,
            FontWeight = FontWeights.Medium,
            VerticalAlignment = VerticalAlignment.Center,
        };
        panel.Children.Add(valueText);
        var chevron = new TextBlock
        {
            Text = ChevronDown,
            FontFamily = Fluent,
            FontSize = 10,
            Foreground = Secondary,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(chevron, 1);
        panel.Children.Add(chevron);

        var button = Styled(row, 166);
        button.Content = panel;
        button.HorizontalContentAlignment = HorizontalAlignment.Stretch;

        var options = new StackPanel();
        var cells = new List<(string Option, Button Cell, TextBlock Label)>();
        var surface = new Border
        {
            Width = 166,
            Padding = new Thickness(6),
            Background = new SolidColorBrush(Color.FromArgb(0xF2, 0x0B, 0x11, 0x19)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(16),
            Child = options,

            BorderBrush = new LinearGradientBrush(
                [
                    new GradientStop(Color.FromArgb(0x76, 0xFF, 0xFF, 0xFF), 0),
                    new GradientStop(Color.FromArgb(0x24, 0xFF, 0xFF, 0xFF), 0.46),
                    new GradientStop(Color.FromArgb(0x12, 0xFF, 0xFF, 0xFF), 1),
                ], new Point(0, 0), new Point(1, 1)),
        };
        var popup = new Popup
        {
            PlacementTarget = button,
            Placement = PlacementMode.Bottom,
            VerticalOffset = 6,
            StaysOpen = false,
            AllowsTransparency = true,
            PopupAnimation = PopupAnimation.Fade,
            Child = surface,
        };
        popup.Opened += (_, _) => chevron.Text = ChevronUp;
        popup.Closed += (_, _) => chevron.Text = ChevronDown;
        button.Click += (_, _) => popup.IsOpen = !popup.IsOpen;

        void PaintCells()
        {
            foreach (var (option, cell, label) in cells)
            {
                bool on = option == value;
                cell.Background = on ? Accent(_page, 0x24) : Brushes.Transparent;
                cell.BorderBrush = on ? Accent(_page, 0x6E) : Brushes.Transparent;
                label.FontWeight = on ? FontWeights.SemiBold : FontWeights.Medium;
            }
        }

        foreach (var option in row.Options)
        {
            var label = new TextBlock
            {
                Text = option,
                Foreground = Ink,
                FontSize = 11.5,
                VerticalAlignment = VerticalAlignment.Center,
            };
            var cell = new Button
            {
                Style = (Style)FindResource("Glass"),
                Height = 36,
                Padding = new Thickness(12, 0, 12, 0),
                Margin = new Thickness(0, 1, 0, 1),
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Content = label,
            };
            string chosen = option;
            cell.Click += (_, _) =>
            {
                value = chosen;
                _store.Set(row.Key, chosen, row.Fallback);
                Draft();
                valueText.Text = chosen;
                PaintCells();
                popup.IsOpen = false;
            };
            cells.Add((option, cell, label));
            options.Children.Add(cell);
        }
        PaintCells();

        var host = new Grid();
        host.Children.Add(button);
        host.Children.Add(popup);
        return host;
    }

    private FrameworkElement BuildSlider(Row row)
    {
        var stops = new List<string>(row.Options);
        int index = Math.Max(0, stops.IndexOf(_store.Text(row.Key, row.Fallback)));
        const double TrackW = 104;

        var rail = new Border
        {
            Height = 3,
            Background = Graphite,
            CornerRadius = new CornerRadius(4),
            VerticalAlignment = VerticalAlignment.Center,
        };
        var fill = new Border
        {
            Height = 3,
            Width = 0,
            Background = Mint,
            CornerRadius = new CornerRadius(4),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var track = new Grid { Width = TrackW, Height = 20, VerticalAlignment = VerticalAlignment.Center };
        track.Children.Add(rail);
        track.Children.Add(fill);

        var readout = new TextBlock
        {
            Foreground = Ink,
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            Width = 48,
            TextAlignment = TextAlignment.Right,
            Margin = new Thickness(10, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };

        var strip = new Grid();
        strip.ColumnDefinitions.Add(new ColumnDefinition());
        strip.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(58) });
        strip.Children.Add(track);
        Grid.SetColumn(readout, 1);
        strip.Children.Add(readout);

        var host = new Border
        {
            Width = 190,
            Height = 38,
            Background = (Brush)FindResource("Frost"),
            BorderBrush = FrostEdge,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(13, 7, 13, 7),
            Cursor = Cursors.Hand,
            Child = strip,
        };

        void Paint()
        {
            fill.Width = stops.Count < 2 ? TrackW : TrackW * index / (stops.Count - 1);
            readout.Text = stops[index];
        }
        void Pick(double x)
        {
            if (stops.Count < 2) return;
            int next = Math.Clamp((int)Math.Round(x / (TrackW / (stops.Count - 1))), 0, stops.Count - 1);
            if (next == index) return;
            index = next;
            _store.Set(row.Key, stops[index], row.Fallback);
            Draft();
            Paint();
        }
        void Grabbed(bool on)
        {
            rail.Height = on ? 8 : 3;
            fill.Height = on ? 8 : 3;
        }
        host.MouseLeftButtonDown += (_, e) =>
        {
            host.CaptureMouse();
            Grabbed(true);
            Pick(e.GetPosition(track).X);
        };
        host.MouseMove += (_, e) => { if (host.IsMouseCaptured) Pick(e.GetPosition(track).X); };
        host.MouseLeftButtonUp += (_, _) => { host.ReleaseMouseCapture(); Grabbed(false); };
        host.LostMouseCapture += (_, _) => Grabbed(false);
        Paint();
        return host;
    }

    private FrameworkElement BuildAction(Row row)
    {
        var button = Styled(row, 124);
        button.Content = row.ActionLabel.Length > 0 ? row.ActionLabel : "Open";
        button.HorizontalContentAlignment = HorizontalAlignment.Center;
        button.Click += (_, _) => Actions.Run(row.Key);
        return button;
    }

    private FrameworkElement BuildStatus(Row row)
    {
        string value = Live.Value(row);
        var tone = Live.Tone(value) switch
        {
            Live.State.Enabled => Mint,
            Live.State.Attention => Coral,
            _ => Secondary,
        };

        if (value.Length == 0) return BuildAction(row);

        if (row.ActionLabel.Length > 0)
        {
            var panel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            panel.Children.Add(new TextBlock
            {
                Text = value,
                Foreground = tone,
                FontWeight = FontWeights.SemiBold,
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 10, 0),
            });
            var open = Styled(row, 126);
            open.Content = row.ActionLabel;
            open.HorizontalContentAlignment = HorizontalAlignment.Center;
            open.Click += (_, _) => Actions.Run(row.Key);
            panel.Children.Add(open);
            return panel;
        }

        var dot = new Ellipse { Width = 6, Height = 6, Fill = tone, VerticalAlignment = VerticalAlignment.Center };
        var text = new TextBlock
        {
            Text = value,
            Foreground = tone,
            FontWeight = FontWeights.SemiBold,
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0),
        };
        var content = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
        content.Children.Add(dot);
        content.Children.Add(text);

        return new Border
        {
            MinWidth = 128,
            MaxWidth = 230,
            Height = 38,
            CornerRadius = new CornerRadius(10),
            BorderBrush = tone,
            BorderThickness = new Thickness(1),
            Background = new SolidColorBrush(Live.Tone(value) == Live.State.Attention
                ? Color.FromArgb(0x19, 0xFF, 0x9A, 0x8B)
                : Color.FromArgb(0x17, 0x78, 0xDD, 0xB2)),
            Padding = new Thickness(12, 0, 12, 0),
            Child = content,
        };
    }

    private Button Styled(Row row, double width) => new()
    {
        Style = (Style)FindResource("Glass"),
        Width = width,
        Height = 38,
        Padding = new Thickness(13, 0, 13, 0),
        ToolTip = row.Label,
    };

    private Button Bare(Row row, double width, double height) => new()
    {
        Style = (Style)FindResource("Glass"),
        Width = width,
        Height = height,
        Background = Brushes.Transparent,
        BorderThickness = new Thickness(0),
        Padding = new Thickness(2),
        HorizontalContentAlignment = HorizontalAlignment.Center,
        ToolTip = row.Label,
    };

    private void BuildHome()
    {
        var hero = new Grid { Margin = new Thickness(4, 5, 0, 18) };
        hero.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        hero.ColumnDefinitions.Add(new ColumnDefinition());
        hero.Children.Add(Mark(84));

        var copy = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(22, 0, 0, 0) };
        copy.Children.Add(new TextBlock
        {
            Text = "Halo",
            Foreground = Ink,
            FontFamily = Display,
            FontSize = 34,
            FontWeight = FontWeights.SemiBold,
        });
        copy.Children.Add(new TextBlock
        {
            Text = "Your apps, activity and agents - surfaced when they matter.",
            Foreground = Secondary,
            FontSize = 13,
            FontWeight = FontWeights.Medium,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 6, 0, 0),
        });
        Grid.SetColumn(copy, 1);
        hero.Children.Add(copy);
        ContentPanel.Children.Add(hero);

        ContentPanel.Children.Add(Eyebrow("EXPLORE", new Thickness(3, 0, 0, 7)));
        var grid = new Grid { Margin = new Thickness(0, 0, 2, 18) };
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        for (int i = 0; i < Catalog.HomeShortcuts.Length; i++)
        {
            var card = Shortcut(Catalog.HomeShortcuts[i]);
            card.Margin = new Thickness(i % 2 == 0 ? 0 : 5, i < 2 ? 0 : 10, i % 2 == 0 ? 5 : 0, 0);
            Grid.SetColumn(card, i % 2);
            Grid.SetRow(card, i / 2);
            grid.Children.Add(card);
        }
        ContentPanel.Children.Add(grid);

        ContentPanel.Children.Add(Eyebrow("STATE", new Thickness(3, 0, 0, 7)));
        ContentPanel.Children.Add(StateCard());
    }

    private Button Shortcut(PageId id)
    {
        var content = new Grid();
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(22) });
        content.ColumnDefinitions.Add(new ColumnDefinition());
        content.Children.Add(Glyph(id, Accent(id)));

        var copy = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(13, 0, 0, 0) };
        copy.Children.Add(new TextBlock
        {
            Text = Catalog.Get(id).Label,
            Foreground = Ink,
            FontSize = 12.5,
            FontWeight = FontWeights.SemiBold,
        });
        copy.Children.Add(new TextBlock
        {
            Text = Catalog.Sub(id),
            Foreground = Secondary,
            FontSize = 10.5,
            Margin = new Thickness(0, 2, 0, 0),
        });
        Grid.SetColumn(copy, 1);
        content.Children.Add(copy);

        var button = new Button
        {
            Style = (Style)FindResource("Glass"),
            Height = 66,
            Background = new SolidColorBrush(Color.FromArgb(0x16, 0xFF, 0xFF, 0xFF)),
            BorderBrush = Accent(id, 0x54),
            Padding = new Thickness(15, 0, 15, 0),
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Content = content,
        };
        Ui.SetRadius(button, new CornerRadius(14));
        button.Click += (_, _) => ShowPage(id);
        return button;
    }

    private Border StateCard()
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var copy = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 14, 0) };
        copy.Children.Add(new TextBlock
        {
            Text = "Changes wait for Apply",
            Foreground = Mint,
            FontWeight = FontWeights.SemiBold,
            FontSize = 12.5,
        });
        copy.Children.Add(new TextBlock
        {
            Text = "Nothing here reaches Halo until you press Apply at the bottom of the page. "
                 + "Reset puts everything back the way you found it.",
            Foreground = Secondary,
            FontSize = 11.5,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 3, 0, 0),
        });
        grid.Children.Add(copy);

        var reset = new Button
        {
            Style = (Style)FindResource("Glass"),
            Content = "Restore defaults",
            Height = 40,
            Padding = new Thickness(16, 0, 16, 0),
            Background = new SolidColorBrush(Color.FromArgb(0x12, 0xFF, 0x9A, 0x8B)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x66, 0xFF, 0x9A, 0x8B)),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        reset.IsEnabled = _store.IsDirty;
        reset.Click += (_, _) => { _store.StageDefaults(); ShowPage(_page); Draft(); };
        _restore = reset;
        Grid.SetColumn(reset, 1);
        grid.Children.Add(reset);

        return new Border
        {
            CornerRadius = new CornerRadius(16),
            BorderBrush = FrostEdge,
            BorderThickness = new Thickness(1),
            Background = new SolidColorBrush(Color.FromArgb(0x22, 0x0F, 0x14, 0x1C)),
            Padding = new Thickness(16, 13, 14, 13),
            Margin = new Thickness(0, 0, 2, 4),
            Child = grid,
        };
    }

    private static Grid Mark(double size)
    {
        var gradient = new LinearGradientBrush(
            [
                new GradientStop(Color.FromRgb(0xFF, 0xFF, 0xFF), 0),
                new GradientStop(Color.FromRgb(0xB8, 0xD6, 0xFF), 0.54),
                new GradientStop(Color.FromRgb(0x78, 0xDD, 0xB2), 1),
            ], new Point(0, 0), new Point(1, 1));

        var mark = new Grid { Width = size, Height = size, VerticalAlignment = VerticalAlignment.Center };
        mark.Children.Add(new Ellipse
        {
            Width = size * 0.76,
            Height = size * 0.76,
            Stroke = new SolidColorBrush(Color.FromArgb(0x24, 0x78, 0xDD, 0xB2)),
            StrokeThickness = size * 0.14,
        });
        mark.Children.Add(new Ellipse
        {
            Width = size * 0.68,
            Height = size * 0.68,
            Stroke = gradient,
            StrokeThickness = size * 0.07,
        });
        return mark;
    }

}
