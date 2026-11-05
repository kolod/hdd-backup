using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace HddBackup.Core.Diagnostics
{
    /// <summary>A time of day (hour 0-23, minute 0-59).</summary>
    public struct TimeOfDay : IEquatable<TimeOfDay>
    {
        public TimeOfDay(int hour, int minute)
        {
            Hour = hour;
            Minute = minute;
        }

        public int Hour { get; }

        public int Minute { get; }

        public bool Equals(TimeOfDay other) => Hour == other.Hour && Minute == other.Minute;

        public override bool Equals(object obj) => obj is TimeOfDay t && Equals(t);

        public override int GetHashCode() => Hour * 60 + Minute;

        public override string ToString() => Hour.ToString("00", CultureInfo.InvariantCulture) + ":" + Minute.ToString("00", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// When something runs, in a language-neutral form (research R-9): a class and its numbers. The App turns it
    /// into words ("every day at 06:25"); anything that does not fit a class is <see cref="ScheduleClass.Custom"/>
    /// and shows its <see cref="Raw"/> text.
    /// </summary>
    public sealed class CronSchedule
    {
        private static readonly string[] MonthNames = { "jan", "feb", "mar", "apr", "may", "jun", "jul", "aug", "sep", "oct", "nov", "dec" };
        private static readonly string[] DayNames = { "sun", "mon", "tue", "wed", "thu", "fri", "sat" };

        private CronSchedule(ScheduleClass cls, string raw)
        {
            Class = cls;
            Raw = raw;
        }

        public ScheduleClass Class { get; }

        /// <summary>The original text (the five fields, the shortcut, or the timer expression).</summary>
        public string Raw { get; }

        /// <summary>Times of day for the daily, weekly, monthly and yearly classes (at most four).</summary>
        public IReadOnlyList<TimeOfDay> Times { get; private set; } = Array.Empty<TimeOfDay>();

        /// <summary>Days of the week, 0 = Sunday .. 6 = Saturday, for <see cref="ScheduleClass.WeeklyOn"/>.</summary>
        public IReadOnlyList<int> DaysOfWeek { get; private set; } = Array.Empty<int>();

        /// <summary>N of "every N minutes", the minute of "every hour at :MM", or the day of the month.</summary>
        public int Number { get; private set; }

        /// <summary>1-12 for <see cref="ScheduleClass.YearlyOn"/>.</summary>
        public int Month { get; private set; }

        public static CronSchedule AtBoot(string raw = "@reboot") => new CronSchedule(ScheduleClass.AtBoot, raw);

        public static CronSchedule Custom(string raw) => new CronSchedule(ScheduleClass.Custom, raw);

        /// <summary>anacron periods: "every N days".</summary>
        public static CronSchedule EveryDays(int days, string raw) => new CronSchedule(ScheduleClass.EveryNDays, raw) { Number = days };

        /// <summary>Parses the five cron fields (<c>m h dom mon dow</c>) or a shortcut such as <c>@daily</c>; null when malformed.</summary>
        public static CronSchedule Parse(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return null;
            }

            string raw = string.Join(" ", text.Split((char[])null, StringSplitOptions.RemoveEmptyEntries));
            if (raw[0] == '@')
            {
                return Shortcut(raw);
            }

            var f = raw.Split(' ');
            if (f.Length != 5)
            {
                return null;
            }

            Field minute = ParseField(f[0], 0, 59, null);
            Field hour = ParseField(f[1], 0, 23, null);
            Field dom = ParseField(f[2], 1, 31, null);
            Field month = ParseField(f[3], 1, 12, MonthNames);
            Field dow = ParseField(f[4], 0, 7, DayNames);
            if (minute == null || hour == null || dom == null || month == null || dow == null)
            {
                return null;
            }

            return Classify(raw, minute, hour, dom, month, dow);
        }

        /// <summary>Common <c>OnCalendar=</c> forms of systemd timers; anything else is <see cref="ScheduleClass.Custom"/>.</summary>
        public static CronSchedule FromTimer(string onCalendar)
        {
            if (string.IsNullOrWhiteSpace(onCalendar))
            {
                return null;
            }

            string raw = string.Join(" ", onCalendar.Split((char[])null, StringSplitOptions.RemoveEmptyEntries));
            switch (raw.ToLowerInvariant())
            {
                case "minutely":
                    return new CronSchedule(ScheduleClass.EveryMinute, raw);
                case "hourly":
                    return new CronSchedule(ScheduleClass.HourlyAt, raw) { Number = 0 };
                case "daily":
                    return new CronSchedule(ScheduleClass.DailyAt, raw) { Times = new[] { new TimeOfDay(0, 0) } };
                case "weekly":
                    return new CronSchedule(ScheduleClass.WeeklyOn, raw) { DaysOfWeek = new[] { 1 }, Times = new[] { new TimeOfDay(0, 0) } };
                case "monthly":
                    return new CronSchedule(ScheduleClass.MonthlyOn, raw) { Number = 1, Times = new[] { new TimeOfDay(0, 0) } };
                case "yearly":
                case "annually":
                    return new CronSchedule(ScheduleClass.YearlyOn, raw) { Number = 1, Month = 1, Times = new[] { new TimeOfDay(0, 0) } };
            }

            var parts = raw.Split(' ');
            int i = 0;
            IReadOnlyList<int> days = null;
            if (parts[i].Length > 0 && char.IsLetter(parts[i][0]))
            {
                days = ParseTimerDays(parts[i]);
                if (days == null)
                {
                    return Custom(raw);
                }

                i++;
            }

            if (i < parts.Length && parts[i] == "*-*-*")
            {
                i++;
            }
            else if (i < parts.Length && parts[i].IndexOf('-') >= 0)
            {
                return Custom(raw);
            }

            if (i != parts.Length - 1)
            {
                return Custom(raw);
            }

            var time = ParseTimerTime(parts[i]);
            if (time == null)
            {
                return Custom(raw);
            }

            if (time.Item1 == null)
            {
                // "*:MM": every hour at minute MM.
                return days == null ? new CronSchedule(ScheduleClass.HourlyAt, raw) { Number = time.Item2 } : Custom(raw);
            }

            if (time.Item1.Count > 4)
            {
                return Custom(raw);
            }

            var times = time.Item1.Select(h => new TimeOfDay(h, time.Item2)).ToList();
            return days == null
                ? new CronSchedule(ScheduleClass.DailyAt, raw) { Times = times }
                : new CronSchedule(ScheduleClass.WeeklyOn, raw) { DaysOfWeek = days, Times = times };
        }

        private static CronSchedule Shortcut(string raw)
        {
            switch (raw.ToLowerInvariant())
            {
                case "@reboot":
                    return AtBoot(raw);
                case "@hourly":
                    return new CronSchedule(ScheduleClass.HourlyAt, raw) { Number = 0 };
                case "@daily":
                case "@midnight":
                    return new CronSchedule(ScheduleClass.DailyAt, raw) { Times = new[] { new TimeOfDay(0, 0) } };
                case "@weekly":
                    return new CronSchedule(ScheduleClass.WeeklyOn, raw) { DaysOfWeek = new[] { 0 }, Times = new[] { new TimeOfDay(0, 0) } };
                case "@monthly":
                    return new CronSchedule(ScheduleClass.MonthlyOn, raw) { Number = 1, Times = new[] { new TimeOfDay(0, 0) } };
                case "@yearly":
                case "@annually":
                    return new CronSchedule(ScheduleClass.YearlyOn, raw) { Number = 1, Month = 1, Times = new[] { new TimeOfDay(0, 0) } };
                default:
                    return null;
            }
        }

        private static CronSchedule Classify(string raw, Field minute, Field hour, Field dom, Field month, Field dow)
        {
            bool domStar = dom.IsStar;
            bool monStar = month.IsStar;
            bool dowStar = dow.IsStar;

            if (domStar && monStar && dowStar)
            {
                if (hour.IsStar)
                {
                    if (minute.IsStar)
                    {
                        return new CronSchedule(ScheduleClass.EveryMinute, raw);
                    }

                    if (minute.StepOfStar > 1 && minute.StepOfStar < 60)
                    {
                        return new CronSchedule(ScheduleClass.EveryNMinutes, raw) { Number = minute.StepOfStar };
                    }

                    return minute.Values.Count == 1
                        ? new CronSchedule(ScheduleClass.HourlyAt, raw) { Number = minute.Values[0] }
                        : Custom(raw);
                }

                var times = TimesOf(hour, minute);
                return times == null ? Custom(raw) : new CronSchedule(ScheduleClass.DailyAt, raw) { Times = times };
            }

            var t = hour.IsStar || minute.IsStar ? null : TimesOf(hour, minute);
            if (t == null)
            {
                return Custom(raw);
            }

            if (domStar && monStar && !dowStar)
            {
                return new CronSchedule(ScheduleClass.WeeklyOn, raw) { DaysOfWeek = dow.Values.Select(d => d % 7).Distinct().OrderBy(d => d).ToList(), Times = t };
            }

            if (!domStar && monStar && dowStar && dom.Values.Count == 1)
            {
                return new CronSchedule(ScheduleClass.MonthlyOn, raw) { Number = dom.Values[0], Times = t };
            }

            if (!domStar && !monStar && dowStar && dom.Values.Count == 1 && month.Values.Count == 1 && t.Count == 1)
            {
                return new CronSchedule(ScheduleClass.YearlyOn, raw) { Number = dom.Values[0], Month = month.Values[0], Times = t };
            }

            return Custom(raw);
        }

        private static List<TimeOfDay> TimesOf(Field hour, Field minute)
        {
            if (hour.Values.Count * minute.Values.Count > 4)
            {
                return null;
            }

            var result = new List<TimeOfDay>();
            foreach (int h in hour.Values)
            {
                foreach (int m in minute.Values)
                {
                    result.Add(new TimeOfDay(h, m));
                }
            }

            return result;
        }

        private sealed class Field
        {
            public List<int> Values;
            public bool IsStar;

            /// <summary>N for a lone "*/N" item, else 0.</summary>
            public int StepOfStar;
        }

        private static Field ParseField(string text, int min, int max, string[] names)
        {
            var values = new SortedSet<int>();
            int stepOfStar = 0;
            var items = text.Split(',');
            foreach (string item in items)
            {
                if (item.Length == 0)
                {
                    return null;
                }

                string range = item;
                int step = 1;
                int slash = item.IndexOf('/');
                if (slash >= 0)
                {
                    range = item.Substring(0, slash);
                    if (!int.TryParse(item.Substring(slash + 1), NumberStyles.None, CultureInfo.InvariantCulture, out step) || step < 1)
                    {
                        return null;
                    }
                }

                int lo;
                int hi;
                if (range == "*")
                {
                    lo = min;
                    hi = names == DayNames ? 6 : max;
                    if (slash >= 0 && items.Length == 1)
                    {
                        stepOfStar = step;
                    }
                }
                else
                {
                    int dash = range.IndexOf('-');
                    if (dash > 0)
                    {
                        if (!Value(range.Substring(0, dash), min, max, names, out lo) || !Value(range.Substring(dash + 1), min, max, names, out hi) || hi < lo)
                        {
                            return null;
                        }
                    }
                    else
                    {
                        if (!Value(range, min, max, names, out lo))
                        {
                            return null;
                        }

                        hi = slash >= 0 ? max : lo;
                    }
                }

                for (int v = lo; v <= hi; v += step)
                {
                    values.Add(v);
                }
            }

            var list = values.ToList();
            int full = (names == DayNames ? 7 : max - min + 1);
            // Sunday may be written 0 or 7; both together are still "every day".
            var normalized = names == DayNames ? list.Select(v => v % 7).Distinct().OrderBy(v => v).ToList() : list;
            bool star = normalized.Count == (names == DayNames ? 7 : full) && normalized.Count > 0;
            return new Field { Values = normalized, IsStar = star, StepOfStar = stepOfStar };
        }

        private static bool Value(string text, int min, int max, string[] names, out int value)
        {
            if (names != null)
            {
                int idx = Array.IndexOf(names, text.ToLowerInvariant());
                if (idx >= 0)
                {
                    value = names == MonthNames ? idx + 1 : idx;
                    return true;
                }
            }

            if (int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value) && value >= min && value <= max)
            {
                return true;
            }

            value = 0;
            return false;
        }

        private static IReadOnlyList<int> ParseTimerDays(string text)
        {
            var days = new SortedSet<int>();
            foreach (string part in text.Split(','))
            {
                string[] range = part.Split(new[] { ".." }, StringSplitOptions.None);
                int lo = DayIndex(range[0]);
                int hi = range.Length > 1 ? DayIndex(range[1]) : lo;
                if (lo < 0 || hi < 0 || range.Length > 2)
                {
                    return null;
                }

                // systemd weeks start on Monday; a range may wrap through Sunday.
                for (int d = lo; ; d = (d + 1) % 7)
                {
                    days.Add(d);
                    if (d == hi)
                    {
                        break;
                    }
                }
            }

            return days.ToList();
        }

        private static int DayIndex(string text)
        {
            string t = text.Length >= 3 ? text.Substring(0, 3).ToLowerInvariant() : text.ToLowerInvariant();
            return Array.IndexOf(DayNames, t);
        }

        /// <summary>"HH:MM", "HH:MM:SS", "H1,H2:MM", "*:MM"; hours null for "*".</summary>
        private static Tuple<List<int>, int> ParseTimerTime(string text)
        {
            int colon = text.IndexOf(':');
            if (colon <= 0)
            {
                return null;
            }

            string hours = text.Substring(0, colon);
            string[] rest = text.Substring(colon + 1).Split(':');
            if (rest.Length > 2 || !int.TryParse(rest[0], NumberStyles.None, CultureInfo.InvariantCulture, out int minute) || minute > 59)
            {
                return null;
            }

            if (rest.Length == 2 && (!int.TryParse(rest[1], NumberStyles.None, CultureInfo.InvariantCulture, out int seconds) || seconds > 59))
            {
                return null;
            }

            if (hours == "*")
            {
                return Tuple.Create<List<int>, int>(null, minute);
            }

            var list = new List<int>();
            foreach (string h in hours.Split(','))
            {
                if (!int.TryParse(h, NumberStyles.None, CultureInfo.InvariantCulture, out int hour) || hour > 23)
                {
                    return null;
                }

                list.Add(hour);
            }

            return Tuple.Create(list, minute);
        }
    }
}
