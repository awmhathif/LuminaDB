using System.Linq;
using System.Windows;
using System.Windows.Controls;
using LuminaDB.App.ViewModels;

namespace LuminaDB.App.Views
{
    public partial class ImportView : UserControl
    {
        public ImportView()
        {
            InitializeComponent();
        }

        private void OnDragOver(object sender, DragEventArgs e)
        {
            e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        }

        private void OnDrop(object sender, DragEventArgs e)
        {
            if (!(e.Data.GetData(DataFormats.FileDrop) is string[] files) || files.Length == 0) return;
            (DataContext as ImportViewModel)?.OpenDropped(files.First());
            e.Handled = true;
        }
    }
}
