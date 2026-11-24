using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Navigation;
using LuminaDB.App.ViewModels;

namespace LuminaDB.App.Views
{
    public partial class MailView : UserControl
    {
        private MailViewModel _vm;
        private MainViewModel _main;
        private bool _loadingHtml;

        public MailView()
        {
            InitializeComponent();
            Browser.Navigating += OnNavigating;
            Browser.Navigated += (s, e) => Silence();
            DataContextChanged += (s, e) => Attach(e.NewValue as MailViewModel);
            Loaded += (s, e) =>
            {
                if (_vm == null) Attach(DataContext as MailViewModel);
                _main = Window.GetWindow(this)?.DataContext as MainViewModel;
                if (_main != null) _main.PropertyChanged += OnMainChanged;
                Render();
            };
            Unloaded += (s, e) =>
            {
                if (_main != null) _main.PropertyChanged -= OnMainChanged;
                _main = null;
                Attach(null);
            };
        }

        private void Attach(MailViewModel vm)
        {
            if (_vm != null) _vm.HtmlChanged -= Render;
            _vm = vm;
            if (_vm != null) _vm.HtmlChanged += Render;
        }

        private void OnMainChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(MainViewModel.HasSlip)) UpdateVisibility();
        }

        private void UpdateVisibility()
        {
            bool show = _vm != null && _vm.IsHtml && _vm.HasSelection && !(_main?.HasSlip ?? false);
            HtmlHost.Visibility = show ? Visibility.Visible : Visibility.Hidden;
        }

        private void Render()
        {
            UpdateVisibility();
            if (_vm == null || !_vm.IsHtml || !IsLoaded) return;
            var html = _vm.SafeHtml ?? "<html><body></body></html>";
            _loadingHtml = true;
            try { Browser.NavigateToString(html); }
            catch (Exception ex) { AppLog.Error("mail preview failed", ex); }
        }

        /// <summary>Only our own NavigateToString loads in the preview; a clicked link opens in the normal browser.</summary>
        private void OnNavigating(object sender, NavigatingCancelEventArgs e)
        {
            if (_loadingHtml && e.Uri == null) { _loadingHtml = false; return; }
            e.Cancel = true;
            var uri = e.Uri;
            if (uri != null && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
            {
                try { Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true })?.Dispose(); }
                catch (Exception ex) { AppLog.Error("opening a mail link failed", ex); }
            }
        }

        /// <summary>No script-error pop-ups from the old IE engine.</summary>
        private void Silence()
        {
            try
            {
                var field = typeof(WebBrowser).GetField("_axIWebBrowser2", BindingFlags.Instance | BindingFlags.NonPublic);
                var ax = field?.GetValue(Browser);
                ax?.GetType().InvokeMember("Silent", BindingFlags.SetProperty, null, ax, new object[] { true });
            }
            catch (Exception) { }
        }
    }
}
