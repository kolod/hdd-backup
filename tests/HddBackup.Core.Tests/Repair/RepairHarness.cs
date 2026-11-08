using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using HddBackup.Core.Browsing;
using HddBackup.Core.Diagnostics;
using HddBackup.Core.Editing;
using HddBackup.Core.Repair;
using HddBackup.Core.Tests.Editing;
using Xunit;

namespace HddBackup.Core.Tests.Repair
{
    /// <summary>Helpers for the repair tests: read an image as it is (or will be), plan, apply, save, check again.</summary>
    internal static class RepairHarness
    {
        public static readonly DateTime Today = new DateTime(2026, 11, 8);

        /// <summary>Runs the SSH check on the image with <paramref name="pending"/> laid over it and hands reader and report to <paramref name="use"/>.</summary>
        public static T Read<T>(string imagePath, IEnumerable<EditChange> pending, Func<SystemReader, SshReport, T> use)
        {
            using (var source = BrowseSource.OpenImage(imagePath))
            using (var view = source.OpenFileSystem(source.Browsable.First()))
            {
                var list = pending?.ToList() ?? new List<EditChange>();
                IFileTree tree = list.Count == 0 ? (IFileTree)view : new PendingTree(view, list);
                var reader = new SystemReader(tree);
                var report = SshCheck.Run(reader, null, CancellationToken.None);
                return use(reader, report);
            }
        }

        public static T Read<T>(string imagePath, Func<SystemReader, SshReport, T> use) => Read(imagePath, null, use);

        public static SshReport Check(string imagePath, IEnumerable<EditChange> pending = null) => Read(imagePath, pending, (r, rep) => rep);

        /// <summary>Plans with the image as it is.</summary>
        public static RepairPlan Plan(string imagePath, Func<SystemReader, SshReport, RepairPlan> make, params EditChange[] pending) =>
            Read(imagePath, pending, make);

        public static IReadOnlyList<RepairOffer> Offers(string imagePath, params EditChange[] pending) =>
            Read(imagePath, pending, (r, rep) => SshRepairPlanner.Offers(r, rep, Today));

        /// <summary>Adds the plans as repairs (each planned on top of the previous ones), saves, returns the saved image's tree.</summary>
        public static Dictionary<string, TreeEntry> ApplyAndSave(string imagePath, string scenario, params Func<SystemReader, SshReport, RepairPlan>[] makers)
        {
            using (var session = ImageEditSession.Open(imagePath))
            {
                foreach (var make in makers)
                {
                    var plan = Read(imagePath, session.Changes, make);
                    Assert.True(plan.CanApply, "the plan cannot be applied: " + plan.Unavailable);
                    session.AddRepair(plan.Changes, plan.Title);
                }

                var result = session.Save(null, CancellationToken.None);
                Assert.Equal(SaveStatus.Saved, result.Status);
            }

            Dictionary<string, TreeEntry> tree;
            using (var device = HddBackup.Core.Devices.FileBlockDevice.OpenRead(imagePath))
            {
                EditAssert.CheckerClean(device, scenario);
                tree = EditAssert.Snapshot(device);
            }

            EditAssert.ExportForCi(scenario, imagePath, tree);
            return tree;
        }

        public static byte[] Bytes(string imagePath, string path)
        {
            using (var device = HddBackup.Core.Devices.FileBlockDevice.OpenRead(imagePath))
            {
                return EditAssert.Content(device, path);
            }
        }

        public static string Text(string imagePath, string path) => System.Text.Encoding.GetEncoding("ISO-8859-1").GetString(Bytes(imagePath, path));
    }
}
