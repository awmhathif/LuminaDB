using LuminaDB.Core.Admin;
using Xunit;
using Area = LuminaDB.Core.Admin.WindowPlace.Area;

namespace LuminaDB.Core.Tests
{
    public class WindowPlaceTests
    {
        [Fact]
        public void A_laptop_screen_opens_maximised_and_never_taller_than_the_screen()
        {
            // 1366×768 with a 40 px taskbar
            var p = WindowPlace.FirstRun(new Area(0, 0, 1366, 728));
            Assert.True(p.Maximized);
            Assert.True(p.Height <= 728);
            Assert.True(p.Top >= 0);
        }

        [Fact]
        public void The_same_laptop_at_125_percent_also_fits()
        {
            var p = WindowPlace.FirstRun(new Area(0, 0, 1093, 582));
            Assert.True(p.Maximized);
            Assert.Equal(1093, p.Width);
            Assert.Equal(582, p.Height);
            Assert.Equal(0, p.Top);
        }

        [Fact]
        public void A_big_screen_gets_the_design_size_centred()
        {
            var p = WindowPlace.FirstRun(new Area(0, 0, 1920, 1040));
            Assert.False(p.Maximized);
            Assert.Equal(1280, p.Width);
            Assert.Equal(800, p.Height);
            Assert.Equal(320, p.Left);
            Assert.Equal(120, p.Top);
        }

        [Fact]
        public void A_title_bar_above_the_screen_is_pulled_back_down()
        {
            var p = new WindowPlace { Left = 40, Top = -16, Width = 1280, Height = 800 }.FitTo(new Area(0, 0, 1366, 728));
            Assert.Equal(0, p.Top);
            Assert.Equal(728, p.Height);
            Assert.Equal(40, p.Left);
        }

        [Fact]
        public void A_window_left_on_an_unplugged_screen_comes_back_whole()
        {
            var p = new WindowPlace { Left = 2400, Top = 100, Width = 1000, Height = 700 }.FitTo(new Area(0, 0, 1920, 1040));
            Assert.Equal(920, p.Left);
            Assert.Equal(100, p.Top);
            Assert.Equal(1000, p.Width);
        }

        [Fact]
        public void A_position_already_on_screen_is_kept_exactly()
        {
            var p = new WindowPlace { Left = 200, Top = 90, Width = 1100, Height = 650, Maximized = true }.FitTo(new Area(0, 0, 1920, 1040));
            Assert.Equal(200, p.Left);
            Assert.Equal(90, p.Top);
            Assert.Equal(1100, p.Width);
            Assert.Equal(650, p.Height);
            Assert.True(p.Maximized);
        }

        [Fact]
        public void A_too_small_window_grows_to_the_minimum()
        {
            var p = new WindowPlace { Left = 10, Top = 10, Width = 400, Height = 300 }.FitTo(new Area(0, 0, 1920, 1040));
            Assert.Equal(WindowPlace.MinWidth, p.Width);
            Assert.Equal(WindowPlace.MinHeight, p.Height);
        }

        [Fact]
        public void A_second_screen_on_the_left_keeps_negative_coordinates()
        {
            var p = new WindowPlace { Left = -1500, Top = 50, Width = 1200, Height = 760 }.FitTo(new Area(-1920, 0, 1920, 1040));
            Assert.Equal(-1500, p.Left);
            Assert.Equal(50, p.Top);
        }
    }
}
