using System.Windows;
using System.Windows.Controls;

namespace LuminaDB.App.Views
{
    public partial class TodayView : UserControl
    {
        /// <summary>Below this width the receipt tape moves under the other two columns (1366×768 lab screens).</summary>
        public const double NarrowWidth = 1120;

        public TodayView()
        {
            InitializeComponent();
            SizeChanged += (s, e) => ApplyLayout(e.NewSize.Width);
        }

        private void ApplyLayout(double width)
        {
            bool narrow = width < NarrowWidth;
            Col3.Width = narrow ? new GridLength(0) : new GridLength(0.85, GridUnitType.Star);
            Grid.SetRow(ReceiptColumn, narrow ? 1 : 0);
            Grid.SetColumn(ReceiptColumn, narrow ? 0 : 2);
            Grid.SetColumnSpan(ReceiptColumn, narrow ? 2 : 1);
            ReceiptColumn.BorderThickness = narrow ? new Thickness(0) : new Thickness(1, 0, 0, 0);
            ReceiptColumn.Padding = narrow ? new Thickness(0, 30, 0, 0) : new Thickness(26, 0, 0, 0);
        }
    }
}
