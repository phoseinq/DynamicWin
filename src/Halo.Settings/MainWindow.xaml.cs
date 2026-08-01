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
        foreach (var page in Catalog.Pages)
        {
            var icon = new Path
            {
                Data = Geometry.Parse(Catalog.Icon(page.Id)),
                Stroke = (Brush)FindResource("Secondary"),
                StrokeThickness = 1.3,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                StrokeLineJoin = PenLineJoin.Round,
                Width = 16,
                Height = 16,
                Stretch = Stretch.None,
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
            NavPanel.Children.Add(button);
            _navButtons[page.Id] = button;
        }
    }

    // Selection is a filled frost bar plus blue ink, which is the only place blue appears outside focus.
    private void Paint(PageId selected)
    {
        foreach (var (id, button) in _navButtons)
        {
            bool on = id == selected;
            button.Background = on ? (Brush)FindResource("Frost") : Brushes.Transparent;
            button.BorderBrush = on ? (Brush)FindResource("FrostEdge") : Brushes.Transparent;
            if (button.Content is not StackPanel row) continue;
            var ink = (Brush)FindResource(on ? "Navigation" : "Secondary");
            if (row.Children[0] is Path icon) icon.Stroke = ink;
            if (row.Children[1] is TextBlock text)
            {
                text.Foreground = on ? (Brush)FindResource("Ink") : ink;
                text.FontWeight = on ? FontWeights.SemiBold : FontWeights.Normal;
            }
        }
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

            var group = new Border
            {
                CornerRadius = new CornerRadius(16),
                Background = (Brush)FindResource("RailFill"),
                BorderBrush = (Brush)FindResource("FrostEdge"),
                BorderThickness = new Thickness(1),
            };
            var stack = new StackPanel { Margin = new Thickness(2) };
            for (int i = 0; i < section.Rows.Count; i++)
            {
                if (i > 0)
                    stack.Children.Add(new Border
                    {
                        Height = 1,
                        Background = new SolidColorBrush(Color.FromArgb(20, 255, 255, 255)),
                        Margin = new Thickness(16, 0, 16, 0),
                    });
                stack.Children.Add(BuildRow(section.Rows[i]));
            }
            group.Child = stack;
            ContentPanel.Children.Add(group);
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

    private void TitleBar_Drag(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2) { WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized; return; }
        try { DragMove(); } catch { }
    }

}
