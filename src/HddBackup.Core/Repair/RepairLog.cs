using System;
using System.Collections.Generic;
using System.Linq;
using HddBackup.Core.Editing;

namespace HddBackup.Core.Repair
{
    public enum RepairState
    {
        /// <summary>Queued in the diff file of an image; takes effect on Save.</summary>
        Waiting,

        /// <summary>Was waiting, then the image was saved.</summary>
        Saved,

        /// <summary>Written to a card at once.</summary>
        Written
    }

    /// <summary>One repair of this session, in words for the report: what, for whom, how it stands. Never key text.</summary>
    public sealed class RepairLogEntry
    {
        public RepairLogEntry(RepairTitle title, RepairState state, DateTime time, int group)
        {
            Title = title;
            State = state;
            Time = time;
            Group = group;
        }

        public RepairTitle Title { get; }

        public RepairState State { get; internal set; }

        public DateTime Time { get; }

        /// <summary>The group in the diff file (images); 0 for a card.</summary>
        public int Group { get; }
    }

    /// <summary>The repairs queued or applied in this session of the wizard, for the saved or copied report (FR-019).</summary>
    public sealed class RepairLog
    {
        private readonly List<RepairLogEntry> _entries = new List<RepairLogEntry>();

        public IReadOnlyList<RepairLogEntry> Entries => _entries;

        public void Add(RepairPlan plan, RepairState state, DateTime time, int group = 0)
        {
            _entries.Add(new RepairLogEntry(plan.Title, state, time, group));
        }

        /// <summary>A waiting repair was undone.</summary>
        public void Remove(int group)
        {
            _entries.RemoveAll(e => e.State == RepairState.Waiting && e.Group == group);
        }

        /// <summary>The image was saved: everything waiting is now saved.</summary>
        public void Saved()
        {
            foreach (var e in _entries.Where(x => x.State == RepairState.Waiting))
            {
                e.State = RepairState.Saved;
            }
        }

        /// <summary>The waiting changes were discarded.</summary>
        public void Discarded() => _entries.RemoveAll(e => e.State == RepairState.Waiting);
    }

    /// <summary>The words for the "Repairs in this session" section of a report, supplied by the UI (research R-10 of feature 009).</summary>
    public interface IRepairReportTexts
    {
        string RepairsCaption { get; }

        string Repair(RepairLogEntry entry);
    }
}
