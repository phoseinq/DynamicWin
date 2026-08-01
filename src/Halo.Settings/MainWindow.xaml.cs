using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace Halo.Settings;

// The rows are built in code rather than templated in XAML. Five kinds of row over eleven pages is not
// enough variety to earn a DataTemplate each, and the earlier attempts at this panel all failed on
// stock control chrome leaking through - which is much easier to see and fix when every element is
// created in one place.
public partial class MainWindow : Window
{
    private readonly Store _store = new();
    private PageId _page = PageId.General;
    private readonly System.Collections.Generic.Dictionary<PageId, Button> _navButtons = new();

    public MainWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) => Glass.Apply(this);
        BuildNav();
        ShowPage(PageId.General);
    }

    private void BuildNav()
    {
        foreach (var group in Catalog.Nav)
        {
            if (group.Header.Length > 0)
                NavPanel.Children.Add(new TextBlock
                {
                    Text = group.Header,
                    Foreground = (Brush)FindResource("Quiet"),
                    FontFamily = new FontFamily("Cascadia Mono, Consolas"),
                    FontSize = 9.5,
                    FontWeight = FontWeights.SemiBold,
                    Margin = new Thickness(18, 16, 0, 6),
                });
            foreach (var id in group.Pages) NavPanel.Children.Add(BuildNavItem(Catalog.Get(id)));
        }
    }

    private Button BuildNavItem(Page page)
    {
        var icon = new TextBlock
        {
            Text = Catalog.Glyph(page.Id),
            FontFamily = new FontFamily("Segoe Fluent Icons"),
            FontSize = 16,
            Foreground = Accent(page.Id),
            Width = 20,
            TextAlignment = TextAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var label = new TextBlock
        {
            Text = page.Label,
            Foreground = (Brush)FindResource("Secondary"),
            FontSize = 12.5,
            Margin = new Thickness(11, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(icon);
        row.Children.Add(label);

        var button = new Button { Style = (Style)FindResource("NavItem"), Content = row, Tag = page.Id };
        button.Click += (_, _) => ShowPage(page.Id);
        _navButtons[page.Id] = button;
        return button;
    }

    // Selection is a blue-tinted pill with blue ink - the one place blue appears outside focus. Frost
    // was the first attempt and it read as "hovered", not "you are here".
    private void Paint(PageId selected)
    {
        foreach (var (id, button) in _navButtons)
        {
            bool on = id == selected;
            var accent = Catalog.Accent(id);
            button.Background = on
                ? new SolidColorBrush(Color.FromArgb(0x2E, accent.R, accent.G, accent.B))
                : Brushes.Transparent;
            button.BorderBrush = on
                ? new SolidColorBrush(Color.FromArgb(0x55, accent.R, accent.G, accent.B))
                : Brushes.Transparent;
            if (button.Content is not StackPanel row) continue;
            // the glyph keeps its colour whether selected or not - it is the entry's identity, not its
            // state - and only the label and the tint behind it move
            if (row.Children[1] is TextBlock text)
            {
                text.Foreground = on ? (Brush)FindResource("Ink") : (Brush)FindResource("Secondary");
                text.FontWeight = on ? FontWeights.SemiBold : FontWeights.Normal;
            }
        }
    }

    private Brush Accent(PageId page)
    {
        var (r, g, b) = Catalog.Accent(page);
        return new SolidColorBrush(Color.FromRgb(r, g, b));
    }

    private void ShowPage(PageId id)
    {
        _page = id;
        var page = Array.Find(Catalog.Pages, p => p.Id == id)!;
        Paint(id);
        PageTitle.Text = page.Label;
        PageDescription.Text = page.Description;
        DetailScroll.ScrollToTop();
        ContentPanel.Children.Clear();

        if (id == PageId.Home) { BuildHome(); return; }

        foreach (var section in page.Sections)
        {
            ContentPanel.Children.Add(new TextBlock
            {
                Text = section.Label,
                Foreground = (Brush)FindResource("Quiet"),
                FontFamily = new FontFamily("Cascadia Mono, Consolas"),
                FontSize = 9.5,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(2, ContentPanel.Children.Count == 0 ? 0 : 22, 0, 8),
            });

            // one card per row rather than one box with separators: a row is a thing you act on, and a
            // shared container made four of them read as a single paragraph
            foreach (var row in section.Rows)
                ContentPanel.Children.Add(new Border
                {
                    CornerRadius = new CornerRadius(14),
                    Background = (Brush)FindResource("RailFill"),
                    BorderBrush = (Brush)FindResource("FrostEdge"),
                    BorderThickness = new Thickness(1),
                    Margin = new Thickness(0, 0, 0, 8),
                    Child = BuildRow(row),
                });
        }
    }

    // label and description on the left, the control hard right, and a 44px floor so a row is a
    // comfortable target rather than a line of text with something clickable on the end
    private UIElement BuildRow(Row row)
    {
        var grid = new Grid { MinHeight = 44, Margin = new Thickness(16, 11, 14, 11) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock
        {
            Text = row.Label,
            Foreground = (Brush)FindResource("Ink"),
            FontSize = 12.5,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
        });
        if (row.Description.Length > 0)
            text.Children.Add(new TextBlock
            {
                Text = row.Description,
                Foreground = (Brush)FindResource("Quiet"),
                FontSize = 11.5,
                Margin = new Thickness(0, 3, 12, 0),
                TextWrapping = TextWrapping.Wrap,
            });
        grid.Children.Add(text);

        var control = row.Kind switch
        {
            RowKind.Toggle => BuildToggle(row),
            RowKind.Choice => BuildChoice(row),
            RowKind.Slider => BuildSlider(row),
            _ => BuildAction(row),
        };
        Grid.SetColumn(control, 1);
        control.VerticalAlignment = VerticalAlignment.Center;
        grid.Children.Add(control);
        return grid;
    }

    // Mint when on, graphite when off - never blue, which belongs to navigation. The knob moves rather
    // than the colour alone changing, so the state survives being looked at in a hurry.
    private FrameworkElement BuildToggle(Row row)
    {
        bool on = _store.Bool(row.Key, row.Fallback == "on");
        var knob = new Border
        {
            Width = 16,
            Height = 16,
            CornerRadius = new CornerRadius(8),
            Background = Brushes.White,
            HorizontalAlignment = on ? HorizontalAlignment.Right : HorizontalAlignment.Left,
            Margin = new Thickness(3, 0, 3, 0),
        };
        var track = new Border
        {
            Width = 44,
            Height = 22,
            CornerRadius = new CornerRadius(11),
            Background = (Brush)FindResource(on ? "Mint" : "Graphite"),
            Child = knob,
            Cursor = Cursors.Hand,
        };
        track.MouseLeftButtonUp += (_, _) =>
        {
            on = !on;
            _store.Set(row.Key, on ? "on" : "off");
            track.Background = (Brush)FindResource(on ? "Mint" : "Graphite");
            knob.HorizontalAlignment = on ? HorizontalAlignment.Right : HorizontalAlignment.Left;
        };
        return track;
    }

    // A segmented control rather than a dropdown: three or four options are quicker to read side by side
    // than behind a click, and a popup over an acrylic window is a second surface to style.
    private FrameworkElement BuildChoice(Row row)
    {
        string value = _store.Text(row.Key, row.Fallback);
        var strip = new StackPanel { Orientation = Orientation.Horizontal };
        var buttons = new System.Collections.Generic.List<(string Option, Border Cell, TextBlock Ink)>();

        foreach (var option in row.Options)
        {
            var ink = new TextBlock
            {
                Text = option,
                FontSize = 11.5,
                Margin = new Thickness(12, 5, 12, 6),
                HorizontalAlignment = HorizontalAlignment.Center,
            };
            var cell = new Border { CornerRadius = new CornerRadius(9), Child = ink, Cursor = Cursors.Hand, Margin = new Thickness(2, 0, 0, 0) };
            cell.MouseLeftButtonUp += (_, _) =>
            {
                value = option;
                _store.Set(row.Key, option);
                foreach (var (o, c, t) in buttons) PaintCell(c, t, o == value);
            };
            buttons.Add((option, cell, ink));
            strip.Children.Add(cell);
        }
        foreach (var (option, cell, ink) in buttons) PaintCell(cell, ink, option == value);
        return strip;
    }

    private void PaintCell(Border cell, TextBlock ink, bool on)
    {
        cell.Background = on ? (Brush)FindResource("Frost") : Brushes.Transparent;
        cell.BorderBrush = on ? (Brush)FindResource("FrostEdge") : Brushes.Transparent;
        cell.BorderThickness = new Thickness(1);
        ink.Foreground = (Brush)FindResource(on ? "Ink" : "Quiet");
        ink.FontWeight = on ? FontWeights.SemiBold : FontWeights.Normal;
    }

    // A track with the stops the setting actually has, not a continuous one: pill scale is five values,
    // and a slider that landed between them would be offering precision the pill cannot use.
    private FrameworkElement BuildSlider(Row row)
    {
        string value = _store.Text(row.Key, row.Fallback);
        var stops = new System.Collections.Generic.List<string>(row.Options);
        int index = System.Math.Max(0, stops.IndexOf(value));
        const double TrackW = 120;

        var fill = new Border
        {
            Height = 4,
            CornerRadius = new CornerRadius(2),
            Background = (Brush)FindResource("Mint"),
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        var track = new Border
        {
            Width = TrackW,
            Height = 4,
            CornerRadius = new CornerRadius(2),
            Background = (Brush)FindResource("Graphite"),
            VerticalAlignment = VerticalAlignment.Center,
            Child = fill,
        };
        var readout = new TextBlock
        {
            Foreground = (Brush)FindResource("Ink"),
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Width = 46,
            TextAlignment = TextAlignment.Right,
            Margin = new Thickness(14, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        var strip = new StackPanel { Orientation = Orientation.Horizontal };
        strip.Children.Add(track);
        strip.Children.Add(readout);
        var host = new Border
        {
            Background = Brushes.Transparent,
            Padding = new Thickness(10, 10, 10, 10),
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
            int next = (int)System.Math.Round(x / (TrackW / (stops.Count - 1)));
            next = System.Math.Clamp(next, 0, stops.Count - 1);
            if (next == index) return;
            index = next;
            _store.Set(row.Key, stops[index]);
            Paint();
        }
        host.MouseLeftButtonDown += (_, e) => { host.CaptureMouse(); Pick(e.GetPosition(track).X); };
        host.MouseMove += (_, e) => { if (host.IsMouseCaptured) Pick(e.GetPosition(track).X); };
        host.MouseLeftButtonUp += (_, _) => host.ReleaseMouseCapture();
        Paint();
        return host;
    }

    private FrameworkElement BuildAction(Row row)
    {
        var button = new Button
        {
            Style = (Style)FindResource("Glass"),
            Content = row.ActionLabel.Length > 0 ? row.ActionLabel : "Open",
        };
        button.Click += (_, _) => Actions.Run(row.Key);
        return button;
    }


    // Home is not a settings page and is not built like one: a mark, the product's name, one line about
    // what it is for, and then the four places to go. Rendering it through the row builder produced a
    // list of switches with nothing above them, which is what every other page already is.
    private void BuildHome()
    {
        var hero = new Grid { Margin = new Thickness(2, 2, 0, 20) };
        hero.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        hero.ColumnDefinitions.Add(new ColumnDefinition());
        hero.Children.Add(Mark(76));

        var copy = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(22, 0, 0, 0) };
        copy.Children.Add(new TextBlock
        {
            Text = "Halo",
            Foreground = (Brush)FindResource("Ink"),
            FontFamily = new FontFamily("Segoe UI Variable Display, Segoe UI"),
            FontSize = 34,
            FontWeight = FontWeights.SemiBold,
        });
        copy.Children.Add(new TextBlock
        {
            Text = "Your apps, activity and agents - surfaced when they matter.",
            Foreground = (Brush)FindResource("Secondary"),
            FontSize = 13,
            FontWeight = FontWeights.Medium,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 6, 0, 0),
        });
        Grid.SetColumn(copy, 1);
        hero.Children.Add(copy);
        ContentPanel.Children.Add(hero);

        ContentPanel.Children.Add(Eyebrow("EXPLORE"));

        var grid = new Grid { Margin = new Thickness(0, 0, 2, 4) };
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        PageId[] shortcuts = [PageId.General, PageId.Features, PageId.Agents, PageId.Access];
        for (int i = 0; i < shortcuts.Length; i++)
        {
            var card = Shortcut(shortcuts[i]);
            card.Margin = new Thickness(i % 2 == 0 ? 0 : 5, i < 2 ? 0 : 10, i % 2 == 0 ? 5 : 0, 0);
            Grid.SetColumn(card, i % 2);
            Grid.SetRow(card, i / 2);
            grid.Children.Add(card);
        }
        ContentPanel.Children.Add(grid);
    }

    private FrameworkElement Shortcut(PageId id)
    {
        var page = Catalog.Get(id);
        var stack = new StackPanel();
        stack.Children.Add(new TextBlock
        {
            Text = Catalog.Glyph(id),
            FontFamily = new FontFamily("Segoe Fluent Icons"),
            FontSize = 19,
            Foreground = Accent(id),
        });
        stack.Children.Add(new TextBlock
        {
            Text = page.Label,
            Foreground = (Brush)FindResource("Ink"),
            FontSize = 13.5,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 9, 0, 0),
        });
        stack.Children.Add(new TextBlock
        {
            Text = page.Description,
            Foreground = (Brush)FindResource("Quiet"),
            FontSize = 11.5,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 4, 0, 0),
        });

        var button = new Button
        {
            Style = (Style)FindResource("Glass"),
            Content = stack,
            Padding = new Thickness(16, 14, 16, 15),
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Background = (Brush)FindResource("RailFill"),
        };
        button.Click += (_, _) => ShowPage(id);
        return button;
    }

    private static TextBlock Eyebrow(string text) => new()
    {
        Text = text,
        Foreground = new SolidColorBrush(Color.FromRgb(0x82, 0x90, 0x9F)),
        FontFamily = new FontFamily("Cascadia Mono, Consolas"),
        FontSize = 9.5,
        FontWeight = FontWeights.SemiBold,
        Margin = new Thickness(2, 0, 0, 9),
    };

    // The product's own icon, not an approximation of it drawn in XAML. The hand-built ring and bar were
    // a stand-in and looked like one next to the same icon in the taskbar.
    private static FrameworkElement Mark(double size)
    {
        var image = new System.Windows.Controls.Image { Width = size, Height = size };
        try
        {
            image.Source = System.Windows.Media.Imaging.BitmapFrame.Create(
                new Uri("pack://application:,,,/halo.ico"),
                System.Windows.Media.Imaging.BitmapCreateOptions.None,
                System.Windows.Media.Imaging.BitmapCacheOption.OnLoad);
        }
        catch { }
        return image;
    }

    private void TitleBar_Drag(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2) { WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized; return; }
        try { DragMove(); } catch { }
    }

}
