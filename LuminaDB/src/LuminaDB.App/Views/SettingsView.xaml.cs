using System.Windows;
using System.Windows.Controls;

namespace LuminaDB.App.Views
{
    public partial class SettingsView : UserControl
    {
        public SettingsView()
        {
            InitializeComponent();
        }

        /// <summary>Settings › Dark theme: the colours change now, spreading from the switch (the setting is saved by the binding).</summary>
        private void OnDarkSwitch(object sender, RoutedEventArgs e)
        {
            bool night = DarkSwitch.IsChecked == true;
            if (night == Palette.Night) return;     // the page loading with the switch already right
            if (Application.Current?.MainWindow is MainWindow w) w.SwitchTheme(night, DarkSwitch);
            else Palette.Apply(night);
        }
    }
}
