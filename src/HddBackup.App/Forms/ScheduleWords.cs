using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using HddBackup.App.Localization;
using HddBackup.Core.Diagnostics;

namespace HddBackup.App.Forms
{
    /// <summary>
    /// Says a <see cref="CronSchedule"/> in words ("every day at 6:25 AM"). Core only classifies the schedule;
    /// the words come from the <c>Sched_*</c> resources, day and month names and the time pattern from the
    /// culture of the interface language (research R-10).
    /// </summary>
    internal static class ScheduleWords
    {
        public static string Format(CronSchedule s) => Format(s, LanguageService.Culture);

        // Expected output (checked with the scripted harness, tasks T043/T046), English:
        //   AtBoot "at boot"; EveryMinute "every minute"; EveryNMinutes(5) "every 5 minutes"; HourlyAt(17) "every hour at :17";
        //   DailyAt 06:25 "every day at 6:25 AM"; DailyAt 06:00,18:00 "every day at 6:00 AM and 6:00 PM";
        //   WeeklyOn Mon 03:00 "every Monday at 3:00 AM"; MonthlyOn(1) "on day 1 of every month at 2:30 AM";
        //   YearlyOn Dec 25 "every year on December 25 at 12:00 AM"; EveryNDays(1) "every day"; Custom "on the schedule 0 0 1 * mon".
        public static string Format(CronSchedule s, CultureInfo culture)
        {
            switch (s.Class)
            {
                case ScheduleClass.AtBoot:
                    return Strings.Get("Sched_AtBoot");
                case ScheduleClass.EveryMinute:
                    return Strings.Get("Sched_EveryMinute");
                case ScheduleClass.EveryNMinutes:
                    return Strings.Format("Sched_EveryNMinutes", s.Number.ToString(culture));
                case ScheduleClass.HourlyAt:
                    return Strings.Format("Sched_HourlyAt", s.Number.ToString("00", culture));
                case ScheduleClass.DailyAt:
                    return Strings.Format("Sched_DailyAt", Times(s.Times, culture));
                case ScheduleClass.WeeklyOn:
                    return Strings.Format("Sched_WeeklyOn", List(s.DaysOfWeek.Select(d => culture.DateTimeFormat.GetDayName((DayOfWeek)d))), Times(s.Times, culture));
                case ScheduleClass.MonthlyOn:
                    return Strings.Format("Sched_MonthlyOn", s.Number.ToString(culture), Times(s.Times, culture));
                case ScheduleClass.YearlyOn:
                    return Strings.Format("Sched_YearlyOn", MonthDay(s.Month, s.Number, culture), Times(s.Times, culture));
                case ScheduleClass.EveryNDays:
                    return s.Number == 1 ? Strings.Get("Sched_EveryDay") : Strings.Format("Sched_EveryNDays", s.Number.ToString(culture));
                default:
                    return Strings.Format("Sched_Custom", s.Raw);
            }
        }

        private static string Times(IReadOnlyList<TimeOfDay> times, CultureInfo culture)
        {
            return List(times.Select(t => new DateTime(2000, 1, 1, t.Hour, t.Minute, 0).ToString("t", culture)));
        }

        private static string MonthDay(int month, int day, CultureInfo culture)
        {
            try
            {
                return new DateTime(2000, month, day).ToString("m", culture);
            }
            catch (ArgumentOutOfRangeException)
            {
                return culture.DateTimeFormat.GetMonthName(month) + " " + day.ToString(culture);
            }
        }

        /// <summary>"a", "a and b", "a, b and c" with the localized "and".</summary>
        private static string List(IEnumerable<string> items)
        {
            var list = items.ToList();
            if (list.Count <= 1)
            {
                return list.Count == 0 ? string.Empty : list[0];
            }

            string head = string.Join(", ", list.Take(list.Count - 1));
            return Strings.Format("Sched_TimeList", head, list[list.Count - 1]);
        }
    }
}
