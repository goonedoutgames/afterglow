using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Afterglow.Core;

namespace Afterglow.Controls;

/// <summary>
/// Web-parity tag chips: collapse to Limit with "+N more", optional expand, optional click.
/// </summary>
public sealed class TagBadges : WrapPanel
{
    public static readonly StyledProperty<IEnumerable<string>?> TagsProperty =
        AvaloniaProperty.Register<TagBadges, IEnumerable<string>?>(nameof(Tags));

    public static readonly StyledProperty<int> LimitProperty =
        AvaloniaProperty.Register<TagBadges, int>(nameof(Limit), 4);

    public static readonly StyledProperty<bool> ExpandableProperty =
        AvaloniaProperty.Register<TagBadges, bool>(nameof(Expandable), true);

    public static readonly StyledProperty<bool> ClickableProperty =
        AvaloniaProperty.Register<TagBadges, bool>(nameof(Clickable));

    public static readonly StyledProperty<string> SizeProperty =
        AvaloniaProperty.Register<TagBadges, string>(nameof(Size), "sm");

    public static readonly StyledProperty<ICommand?> TagClickCommandProperty =
        AvaloniaProperty.Register<TagBadges, ICommand?>(nameof(TagClickCommand));

    private bool _expanded;

    static TagBadges()
    {
        TagsProperty.Changed.AddClassHandler<TagBadges>((c, _) => c.Rebuild());
        LimitProperty.Changed.AddClassHandler<TagBadges>((c, _) => c.Rebuild());
        ExpandableProperty.Changed.AddClassHandler<TagBadges>((c, _) => c.Rebuild());
        ClickableProperty.Changed.AddClassHandler<TagBadges>((c, _) => c.Rebuild());
        SizeProperty.Changed.AddClassHandler<TagBadges>((c, _) => c.Rebuild());
        TagClickCommandProperty.Changed.AddClassHandler<TagBadges>((c, _) => c.Rebuild());
    }

    public TagBadges()
    {
        Orientation = Orientation.Horizontal;
        // Approx web gap 0.35rem
    }

    public IEnumerable<string>? Tags
    {
        get => GetValue(TagsProperty);
        set => SetValue(TagsProperty, value);
    }

    public int Limit
    {
        get => GetValue(LimitProperty);
        set => SetValue(LimitProperty, value);
    }

    public bool Expandable
    {
        get => GetValue(ExpandableProperty);
        set => SetValue(ExpandableProperty, value);
    }

    public bool Clickable
    {
        get => GetValue(ClickableProperty);
        set => SetValue(ClickableProperty, value);
    }

    public string Size
    {
        get => GetValue(SizeProperty);
        set => SetValue(SizeProperty, value);
    }

    public ICommand? TagClickCommand
    {
        get => GetValue(TagClickCommandProperty);
        set => SetValue(TagClickCommandProperty, value);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Rebuild();
    }

    private void Rebuild()
    {
        Children.Clear();
        var clean = TagHelpers.HumanTags(Tags);
        if (clean.Count == 0)
        {
            IsVisible = false;
            return;
        }

        IsVisible = true;
        var limit = Math.Max(1, Limit);
        var showAll = _expanded && Expandable;
        var visible = showAll ? clean : clean.Take(limit).ToList();
        var hidden = Math.Max(0, clean.Count - limit);
        var sm = !string.Equals(Size, "md", StringComparison.OrdinalIgnoreCase);

        foreach (var tag in visible)
            Children.Add(MakeChip(tag, sm, more: false, clickable: Clickable));

        if (!_expanded && hidden > 0)
        {
            var more = MakeChip($"+{hidden} more", sm, more: true, clickable: Expandable);
            if (Expandable)
                more.PointerPressed += (_, e) =>
                {
                    e.Handled = true;
                    _expanded = true;
                    Rebuild();
                };
            Children.Add(more);
        }
        else if (_expanded && Expandable && clean.Count > limit)
        {
            var less = MakeChip("Show less", sm, more: true, clickable: true);
            less.PointerPressed += (_, e) =>
            {
                e.Handled = true;
                _expanded = false;
                Rebuild();
            };
            Children.Add(less);
        }
    }

    private Border MakeChip(string text, bool sm, bool more, bool clickable)
    {
        var border = new Border
        {
            Classes = { "tag-badge", sm ? "tag-sm" : "tag-md" },
            Margin = new Thickness(0, 0, 6, 6),
            Child = new TextBlock
            {
                Text = text,
                VerticalAlignment = VerticalAlignment.Center
            }
        };
        if (more) border.Classes.Add("tag-more");
        if (clickable && !more)
        {
            border.Classes.Add("tag-clickable");
            border.Cursor = new Cursor(StandardCursorType.Hand);
            var captured = text;
            border.PointerPressed += (_, e) =>
            {
                e.Handled = true;
                if (TagClickCommand?.CanExecute(captured) == true)
                    TagClickCommand.Execute(captured);
            };
        }
        else if (more && Expandable)
        {
            border.Cursor = new Cursor(StandardCursorType.Hand);
        }

        return border;
    }
}
