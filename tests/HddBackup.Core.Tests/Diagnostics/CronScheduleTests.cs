using System.Linq;
using HddBackup.Core.Diagnostics;
using Xunit;

namespace HddBackup.Core.Tests.Diagnostics
{
    public class CronScheduleTests
    {
        private static string Times(CronSchedule s) => string.Join(",", s.Times.Select(t => t.ToString()));

        [Theory]
        [InlineData("* * * * *", ScheduleClass.EveryMinute)]
        [InlineData("*/1 * * * *", ScheduleClass.EveryMinute)]
        [InlineData("0-59 * * * *", ScheduleClass.EveryMinute)]
        public void EveryMinute(string text, ScheduleClass expected)
        {
            Assert.Equal(expected, CronSchedule.Parse(text).Class);
        }

        [Theory]
        [InlineData("*/5 * * * *", 5)]
        [InlineData("*/15 * * * *", 15)]
        [InlineData("*/7 * * * *", 7)]
        public void EveryNMinutes(string text, int n)
        {
            var s = CronSchedule.Parse(text);

            Assert.Equal(ScheduleClass.EveryNMinutes, s.Class);
            Assert.Equal(n, s.Number);
        }

        [Theory]
        [InlineData("0 * * * *", 0)]
        [InlineData("17 * * * *", 17)]
        [InlineData("@hourly", 0)]
        [InlineData("*/60 * * * *", 0)]
        public void HourlyAt(string text, int minute)
        {
            var s = CronSchedule.Parse(text);

            Assert.Equal(ScheduleClass.HourlyAt, s.Class);
            Assert.Equal(minute, s.Number);
        }

        [Theory]
        [InlineData("25 6 * * *", "06:25")]
        [InlineData("25 6,18 * * *", "06:25,18:25")]
        [InlineData("0 0 * * *", "00:00")]
        [InlineData("@daily", "00:00")]
        [InlineData("@midnight", "00:00")]
        [InlineData("0,30 7 * * *", "07:00,07:30")]
        [InlineData("0 6-7 * * *", "06:00,07:00")]
        public void DailyAt(string text, string times)
        {
            var s = CronSchedule.Parse(text);

            Assert.Equal(ScheduleClass.DailyAt, s.Class);
            Assert.Equal(times, Times(s));
        }

        [Theory]
        [InlineData("0 3 * * mon", "1", "03:00")]
        [InlineData("0 3 * * MON", "1", "03:00")]
        [InlineData("0 3 * * 1-5", "1,2,3,4,5", "03:00")]
        [InlineData("47 6 * * 7", "0", "06:47")]
        [InlineData("47 6 * * 0", "0", "06:47")]
        [InlineData("30 4 * * sat,sun", "0,6", "04:30")]
        [InlineData("@weekly", "0", "00:00")]
        public void WeeklyOn(string text, string days, string times)
        {
            var s = CronSchedule.Parse(text);

            Assert.Equal(ScheduleClass.WeeklyOn, s.Class);
            Assert.Equal(days, string.Join(",", s.DaysOfWeek));
            Assert.Equal(times, Times(s));
        }

        [Theory]
        [InlineData("30 2 1 * *", 1, "02:30")]
        [InlineData("52 6 15 * *", 15, "06:52")]
        [InlineData("@monthly", 1, "00:00")]
        public void MonthlyOn(string text, int day, string times)
        {
            var s = CronSchedule.Parse(text);

            Assert.Equal(ScheduleClass.MonthlyOn, s.Class);
            Assert.Equal(day, s.Number);
            Assert.Equal(times, Times(s));
        }

        [Theory]
        [InlineData("0 0 25 12 *", 12, 25, "00:00")]
        [InlineData("30 8 1 jan *", 1, 1, "08:30")]
        [InlineData("@yearly", 1, 1, "00:00")]
        [InlineData("@annually", 1, 1, "00:00")]
        public void YearlyOn(string text, int month, int day, string times)
        {
            var s = CronSchedule.Parse(text);

            Assert.Equal(ScheduleClass.YearlyOn, s.Class);
            Assert.Equal(month, s.Month);
            Assert.Equal(day, s.Number);
            Assert.Equal(times, Times(s));
        }

        [Fact]
        public void RebootIsAtBoot()
        {
            Assert.Equal(ScheduleClass.AtBoot, CronSchedule.Parse("@reboot").Class);
            Assert.Equal(ScheduleClass.AtBoot, CronSchedule.AtBoot().Class);
        }

        [Theory]
        [InlineData("0 0 1 * mon")]
        [InlineData("0,30 * * * *")]
        [InlineData("0 */2 * * *")]
        [InlineData("0 0 * 6 *")]
        [InlineData("*/10 3 * * *")]
        [InlineData("0 0 1,15 * *")]
        [InlineData("0 6,9,12,15,18 * * *")]
        [InlineData("* 3 * * *")]
        public void AnythingElseIsCustomAndKeepsItsText(string text)
        {
            var s = CronSchedule.Parse(text);

            Assert.Equal(ScheduleClass.Custom, s.Class);
            Assert.Equal(text, s.Raw);
        }

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("* * * *")]
        [InlineData("* * * * * *")]
        [InlineData("60 * * * *")]
        [InlineData("* 24 * * *")]
        [InlineData("* * 0 * *")]
        [InlineData("* * 32 * *")]
        [InlineData("* * * 13 *")]
        [InlineData("* * * * 8")]
        [InlineData("a * * * *")]
        [InlineData("*/0 * * * *")]
        [InlineData("5-3 * * * *")]
        [InlineData("1,,2 * * * *")]
        [InlineData("@sometimes")]
        public void MalformedTextIsNull(string text)
        {
            Assert.Null(CronSchedule.Parse(text));
        }

        [Fact]
        public void WhitespaceIsNormalisedAndTabsWork()
        {
            var s = CronSchedule.Parse("25\t6  *   * *");

            Assert.Equal(ScheduleClass.DailyAt, s.Class);
            Assert.Equal("25 6 * * *", s.Raw);
        }

        [Theory]
        [InlineData("daily", ScheduleClass.DailyAt, "00:00")]
        [InlineData("*-*-* 6,18:00", ScheduleClass.DailyAt, "06:00,18:00")]
        [InlineData("*-*-* 6:00", ScheduleClass.DailyAt, "06:00")]
        [InlineData("*-*-* 00:00:00", ScheduleClass.DailyAt, "00:00")]
        [InlineData("03:30", ScheduleClass.DailyAt, "03:30")]
        public void TimerDaily(string text, ScheduleClass cls, string times)
        {
            var s = CronSchedule.FromTimer(text);

            Assert.Equal(cls, s.Class);
            Assert.Equal(times, Times(s));
        }

        [Theory]
        [InlineData("weekly", "1", "00:00")]
        [InlineData("Sun *-*-* 03:10:00", "0", "03:10")]
        [InlineData("Mon..Fri *-*-* 09:00", "1,2,3,4,5", "09:00")]
        [InlineData("Sat,Sun *-*-* 10:30", "0,6", "10:30")]
        public void TimerWeekly(string text, string days, string times)
        {
            var s = CronSchedule.FromTimer(text);

            Assert.Equal(ScheduleClass.WeeklyOn, s.Class);
            Assert.Equal(days, string.Join(",", s.DaysOfWeek));
            Assert.Equal(times, Times(s));
        }

        [Fact]
        public void TimerHourlyMonthlyAndMinutely()
        {
            Assert.Equal(ScheduleClass.HourlyAt, CronSchedule.FromTimer("hourly").Class);
            Assert.Equal(ScheduleClass.HourlyAt, CronSchedule.FromTimer("*:15").Class);
            Assert.Equal(15, CronSchedule.FromTimer("*:15").Number);
            Assert.Equal(ScheduleClass.MonthlyOn, CronSchedule.FromTimer("monthly").Class);
            Assert.Equal(ScheduleClass.EveryMinute, CronSchedule.FromTimer("minutely").Class);
            Assert.Equal(ScheduleClass.YearlyOn, CronSchedule.FromTimer("yearly").Class);
        }

        [Theory]
        [InlineData("*-*-01 04:00:00")]
        [InlineData("*:0/15")]
        [InlineData("quarterly")]
        [InlineData("2024-01-01 00:00")]
        [InlineData("Mon *-*-* 1,2,3,4,5:00")]
        public void TimerCustom(string text)
        {
            var s = CronSchedule.FromTimer(text);

            Assert.Equal(ScheduleClass.Custom, s.Class);
            Assert.Equal(text, s.Raw);
        }
    }
}
