using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using LuminaDB.App.ViewModels;

namespace LuminaDB.App.Views
{
    public partial class DiagramView : UserControl
    {
        private double _dragX, _dragY;

        public DiagramView()
        {
            InitializeComponent();
            DataContextChanged += (s, e) =>
            {
                if (e.NewValue is DiagramViewModel vm) vm.RenderPng = (scene, file) => DiagramDrawing.SavePng(scene, file);
            };
        }

        private DiagramViewModel Vm => DataContext as DiagramViewModel;

        private void OnCardDragStarted(object sender, DragStartedEventArgs e)
        {
            if (!((sender as FrameworkElement)?.DataContext is DiagramCard card) || Vm == null) return;
            Vm.Selected = card;
            _dragX = card.X;
            _dragY = card.Y;
        }

        private void OnCardDragDelta(object sender, DragDeltaEventArgs e)
        {
            if (!((sender as FrameworkElement)?.DataContext is DiagramCard card) || Vm == null) return;
            // the Thumb reports in its own (unzoomed) coordinates; accumulate so rounding never drifts
            _dragX += e.HorizontalChange;
            _dragY += e.VerticalChange;
            Vm.MoveCard(card, _dragX, _dragY);
        }

        private void OnCardDragCompleted(object sender, DragCompletedEventArgs e)
        {
            if (!((sender as FrameworkElement)?.DataContext is DiagramCard card) || Vm == null) return;
            Vm.EndMove(card);
        }

        private void OnCardDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (!((sender as FrameworkElement)?.DataContext is DiagramCard card) || Vm == null) return;
            Vm.Selected = card;
            if (Vm.EditCommand.CanExecute(null)) Vm.EditCommand.Execute(null);
            e.Handled = true;
        }

        private void OnSurfaceDown(object sender, MouseButtonEventArgs e)
        {
            if (Vm == null || !ReferenceEquals(e.OriginalSource, Surface)) return;
            if (!Vm.IsEditing) Vm.Selected = null;
        }

        private void OnWheel(object sender, MouseWheelEventArgs e)
        {
            if (Vm == null || (Keyboard.Modifiers & ModifierKeys.Control) == 0) return;
            // keep the point under the mouse where it is
            var at = e.GetPosition(Scroller);
            double before = Vm.Zoom;
            double cx = (Scroller.HorizontalOffset + at.X) / before, cy = (Scroller.VerticalOffset + at.Y) / before;
            Vm.Zoom = Math.Round(before * (e.Delta > 0 ? 1.1 : 1 / 1.1), 2);
            Scroller.UpdateLayout();
            Scroller.ScrollToHorizontalOffset(cx * Vm.Zoom - at.X);
            Scroller.ScrollToVerticalOffset(cy * Vm.Zoom - at.Y);
            e.Handled = true;
        }

        private void OnScrollChanged(object sender, ScrollChangedEventArgs e)
        {
            if (Vm == null) return;
            Vm.ViewLeft = Scroller.HorizontalOffset / Vm.Zoom;
            Vm.ViewTop = Scroller.VerticalOffset / Vm.Zoom;
        }

        private void OnFit(object sender, RoutedEventArgs e)
        {
            var vm = Vm;
            if (vm == null || vm.Cards.Count == 0) return;
            double right = vm.Cards.Max(c => c.X + c.Width) + 60, bottom = vm.Cards.Max(c => c.Y + c.Height) + 40;
            double w = Math.Max(100, Scroller.ViewportWidth), h = Math.Max(100, Scroller.ViewportHeight);
            double fit = Math.Floor(Math.Max(DiagramViewModel.MinZoom, Math.Min(1, Math.Min(w / right, h / bottom))) * 100) / 100;
            // a second press when it already fits goes back to 100%
            vm.Zoom = Math.Abs(vm.Zoom - fit) < 0.005 ? 1 : fit;
            Scroller.ScrollToHorizontalOffset(0);
            Scroller.ScrollToVerticalOffset(0);
        }
    }
}
