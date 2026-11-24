using System;
using System.Windows.Controls;
using System.Windows.Threading;
using LuminaDB.App.ViewModels;

namespace LuminaDB.App.Views
{
    public partial class ProjectsView : UserControl
    {
        private ProjectsViewModel _vm;

        public ProjectsView()
        {
            InitializeComponent();
            DataContextChanged += (s, e) => Attach(e.NewValue as ProjectsViewModel);
            Loaded += (s, e) => { if (_vm == null) Attach(DataContext as ProjectsViewModel); };
            Unloaded += (s, e) => Attach(null);
        }

        private void Attach(ProjectsViewModel vm)
        {
            if (_vm != null)
            {
                _vm.LogAppended -= OnLogAppended;
                _vm.ComposerAppended -= OnComposerAppended;
            }
            _vm = vm;
            if (_vm != null)
            {
                _vm.LogAppended += OnLogAppended;
                _vm.ComposerAppended += OnComposerAppended;
            }
        }

        // New lines arrive at the bottom: keep them in view (after the binding has pushed the new text).
        private void OnLogAppended() => Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => LogBox.ScrollToEnd()));
        private void OnComposerAppended() => Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => ComposerOut.ScrollToEnd()));
    }
}
