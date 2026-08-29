using System.Collections;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;

namespace Sesame;

/// <summary>
/// Click a column header to sort. Type in the header box to filter that column.
/// Property names come from DisplayMemberBinding, or from the optional header map.
/// </summary>
public static class ListColumns
{
    private static readonly ConditionalWeakTable<ListView, State> States = new();

    public static void Attach(ListView list, IEnumerable items, bool sort = true,
        IReadOnlyDictionary<string, string>? properties = null)
    {
        var view = items as ListCollectionView ??
                   CollectionViewSource.GetDefaultView(items) as ListCollectionView ??
                   (ListCollectionView)CollectionViewSource.GetDefaultView(items);
        list.ItemsSource = view;
        var state = new State
        {
            View = view,
            OriginalFilter = view.Filter,
            SortEnabled = sort,
            Properties = properties
        };
        if (States.TryGetValue(list, out _))
            States.Remove(list);
        States.Add(list, state);
        view.Filter = row => Pass(state, row);
        list.AddHandler(ButtonBase.ClickEvent, new RoutedEventHandler(HeaderClick), true);
        list.Loaded += (_, _) => WrapHeaders(list, state);
        if (list.IsLoaded)
            WrapHeaders(list, state);
    }

    public static void Refresh(ListView list)
    {
        if (States.TryGetValue(list, out var state))
            state.View.Refresh();
    }

    public static bool TryInfo(GridViewColumn column, out string title, out string property)
    {
        title = "";
        property = "";
        if (column.Header is StackPanel panel && panel.Tag is HeaderBits bits)
        {
            title = bits.Title;
            property = bits.Property;
            return true;
        }

        return false;
    }

    private static void WrapHeaders(ListView list, State state)
    {
        if (list.View is not GridView grid) return;
        foreach (var column in grid.Columns)
        {
            var prop = PropertyOf(column, state.Properties);
            if (prop is null || prop.Length == 0) continue;
            if (column.Header is FrameworkElement) continue;
            var title = column.Header?.ToString() ?? prop;
            if (title.Length == 0) continue;
            column.Header = BuildHeader(title, prop, state);
        }
    }

    private static string? PropertyOf(GridViewColumn column, IReadOnlyDictionary<string, string>? map)
    {
        var header = column.Header?.ToString() ?? "";
        if (map is not null && header.Length > 0 && map.TryGetValue(header, out var mapped))
            return mapped;
        if (column.DisplayMemberBinding is Binding { Path.Path: { Length: > 0 } path })
            return path;
        return null;
    }

    private static FrameworkElement BuildHeader(string title, string prop, State state)
    {
        var label = new TextBlock
        {
            Text = title,
            FontWeight = FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        var box = new TextBox
        {
            Margin = new Thickness(0, 4, 4, 0),
            Padding = new Thickness(4, 2, 4, 2),
            FontSize = 11,
            FontWeight = FontWeights.Normal,
            MinHeight = 22,
            MaxHeight = 24,
            ToolTip = "Filter " + title
        };
        box.TextChanged += (_, _) =>
        {
            state.Filters[prop] = box.Text ?? "";
            state.View.Refresh();
        };
        box.GotKeyboardFocus += (_, _) =>
        {
            /* keep caret in the filter; do not sort */
        };
        var panel = new StackPanel { MinWidth = 64 };
        panel.Children.Add(label);
        panel.Children.Add(box);
        panel.Tag = new HeaderBits(title, prop, label);
        return panel;
    }

    private static void HeaderClick(object sender, RoutedEventArgs e)
    {
        if (sender is not ListView list || !States.TryGetValue(list, out var state) || !state.SortEnabled)
            return;
        if (e.OriginalSource is TextBox || FindParent<TextBox>(e.OriginalSource as DependencyObject) is not null)
            return;
        if (e.OriginalSource is Thumb || FindParent<Thumb>(e.OriginalSource as DependencyObject) is not null)
            return;
        if (e.OriginalSource is not DependencyObject src) return;
        var header = FindParent<GridViewColumnHeader>(src);
        if (header?.Column is null || !TryInfo(header.Column, out _, out var prop) || prop.Length == 0)
            return;
        Sort(state, prop);
        RefreshGlyphs(list, state);
        e.Handled = true;
    }

    private static void Sort(State state, string prop)
    {
        if (state.SortProperty == prop)
            state.SortDir = state.SortDir == ListSortDirection.Ascending
                ? ListSortDirection.Descending
                : ListSortDirection.Ascending;
        else
        {
            state.SortProperty = prop;
            state.SortDir = ListSortDirection.Ascending;
        }

        using (state.View.DeferRefresh())
        {
            state.View.SortDescriptions.Clear();
            state.View.SortDescriptions.Add(new SortDescription(prop, state.SortDir));
        }
    }

    private static void RefreshGlyphs(ListView list, State state)
    {
        if (list.View is not GridView grid) return;
        foreach (var column in grid.Columns)
        {
            if (column.Header is not StackPanel panel || panel.Tag is not HeaderBits bits) continue;
            var mark = bits.Property == state.SortProperty
                ? (state.SortDir == ListSortDirection.Ascending ? " ▲" : " ▼")
                : "";
            bits.Label.Text = bits.Title + mark;
        }
    }

    private static bool Pass(State state, object row)
    {
        if (state.OriginalFilter is not null && !state.OriginalFilter(row))
            return false;
        foreach (var (prop, text) in state.Filters)
        {
            var q = text.Trim();
            if (q.Length == 0) continue;
            var value = Read(row, prop);
            if (value.IndexOf(q, StringComparison.OrdinalIgnoreCase) < 0)
                return false;
        }

        return true;
    }

    private static string Read(object row, string prop)
    {
        var type = row.GetType();
        var info = type.GetProperty(prop);
        if (info is null) return "";
        var value = info.GetValue(row);
        return value switch
        {
            null => "",
            DateTime dt => dt.ToString("g", CultureInfo.CurrentCulture),
            IFormattable f => f.ToString(null, CultureInfo.CurrentCulture) ?? "",
            _ => value.ToString() ?? ""
        };
    }

    private static T? FindParent<T>(DependencyObject? node) where T : DependencyObject
    {
        while (node is not null)
        {
            if (node is T match) return match;
            node = VisualTreeHelper.GetParent(node);
        }

        return null;
    }

    private sealed class State
    {
        public required ListCollectionView View { get; init; }
        public Predicate<object>? OriginalFilter { get; init; }
        public bool SortEnabled { get; init; }
        public IReadOnlyDictionary<string, string>? Properties { get; init; }
        public Dictionary<string, string> Filters { get; } = new(StringComparer.OrdinalIgnoreCase);
        public string? SortProperty { get; set; }
        public ListSortDirection SortDir { get; set; } = ListSortDirection.Ascending;
    }

    private sealed record HeaderBits(string Title, string Property, TextBlock Label);
}
