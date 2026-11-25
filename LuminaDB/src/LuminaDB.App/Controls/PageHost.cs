using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;

namespace LuminaDB.App.Controls
{
    /// <summary>
    /// Shows the selected page and keeps pages it has already built, hidden, instead of building them again on every
    /// visit (a page switch took 100-360 ms when each visit rebuilt the page). Each page is built on its first visit,
    /// from the same template a ContentControl would use and with the same data context, so it behaves as before.
    /// </summary>
    public sealed class PageHost : Grid
    {
        public static readonly DependencyProperty SectionProperty =
            DependencyProperty.Register(nameof(Section), typeof(object), typeof(PageHost), new PropertyMetadata(null, (d, e) => ((PageHost)d).Show(e.NewValue)));

        public object Section
        {
            get => GetValue(SectionProperty);
            set => SetValue(SectionProperty, value);
        }

        public DataTemplateSelector Selector { get; set; }

        private readonly Dictionary<object, ContentPresenter> _pages = new Dictionary<object, ContentPresenter>();
        private ContentPresenter _shown;

        private void Show(object section)
        {
            if (section == null) return;
            if (!_pages.TryGetValue(section, out var page))
            {
                var template = Selector?.SelectTemplate(section, this);
                if (template == null) return;
                page = new ContentPresenter { Content = section, ContentTemplate = template, Focusable = false };
                _pages[section] = page;
                Children.Add(page);
            }
            if (page == _shown) return;
            page.Visibility = Visibility.Visible;          // the new page first, so there's never a blank frame
            if (_shown != null) _shown.Visibility = Visibility.Collapsed;
            _shown = page;
        }
    }
}
