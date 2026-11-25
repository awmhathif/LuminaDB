using System.Collections.Generic;
using System.Windows;
using System.Windows.Markup;

namespace LuminaDB.App.Views
{
    /// <summary>Cell and header templates for result grids: values by key, NULL shown as a dashed chip, never as blank.</summary>
    internal static class GridKit
    {
        private const string CellXaml = @"<DataTemplate xmlns=""http://schemas.microsoft.com/winfx/2006/xaml/presentation""
              xmlns:x=""http://schemas.microsoft.com/winfx/2006/xaml"" xmlns:c=""clr-namespace:LuminaDB.App.Controls;assembly=LuminaDB"">
  <Border x:Name=""chip"" HorizontalAlignment=""{ALIGN}"" CornerRadius=""2"" BorderThickness=""0"" Padding=""0"">
    <TextBlock x:Name=""t"" Text=""{Binding [{KEY}], Mode=OneWay}"" TextTrimming=""CharacterEllipsis"" TextWrapping=""NoWrap"" MaxHeight=""20""/>
  </Border>
  <DataTemplate.Triggers>
    <DataTrigger Binding=""{Binding [{KEY}], Converter={x:Static c:NullCell.IsNull}}"" Value=""True"">
      <Setter TargetName=""t"" Property=""Text"" Value=""NULL""/>
      <Setter TargetName=""t"" Property=""FontSize"" Value=""10""/>
      <Setter TargetName=""t"" Property=""Foreground"" Value=""#625B4D""/>
      <Setter TargetName=""chip"" Property=""BorderThickness"" Value=""1""/>
      <Setter TargetName=""chip"" Property=""BorderBrush"" Value=""{DynamicResource NullDash}""/>
      <Setter TargetName=""chip"" Property=""Padding"" Value=""4,0""/>
      <Setter TargetName=""chip"" Property=""HorizontalAlignment"" Value=""Left""/>
    </DataTrigger>
  </DataTemplate.Triggers>
</DataTemplate>";

        private const string HeaderXaml = @"<DataTemplate xmlns=""http://schemas.microsoft.com/winfx/2006/xaml/presentation"">
  <StackPanel>
    <TextBlock Foreground=""#1C1A16""><Run Text=""{Binding Name, Mode=OneWay}""/><Run Text=""{Binding Sort, Mode=OneWay}"" Foreground=""#1F3FBF""/></TextBlock>
    <TextBlock Text=""{Binding Type}"" FontSize=""11"" Foreground=""#625B4D"" Margin=""0,1,0,0""/>
  </StackPanel>
</DataTemplate>";

        private static DataTemplate _header;
        private static readonly Dictionary<string, DataTemplate> Cells = new Dictionary<string, DataTemplate>();

        public static DataTemplate Header => _header ?? (_header = (DataTemplate)XamlReader.Parse(HeaderXaml));

        public static DataTemplate Cell(string key, bool numeric)
        {
            var id = key + (numeric ? "R" : "L");
            if (Cells.TryGetValue(id, out var t)) return t;
            t = (DataTemplate)XamlReader.Parse(CellXaml.Replace("{KEY}", key).Replace("{ALIGN}", numeric ? "Right" : "Left"));
            Cells[id] = t;
            return t;
        }
    }
}
