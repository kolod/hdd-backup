using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;

namespace HddBackup.Core.Imaging
{
    /// <summary>A reusable data buffer travelling through the pipeline.</summary>
    public sealed class Chunk
    {
        internal Chunk(int size)
        {
            Buffer = new byte[size];
        }

        public byte[] Buffer { get; }

        /// <summary>Number of payload bytes in <see cref="Buffer"/>.</summary>
        public int Count { get; set; }

        /// <summary>Payload rounded up to the sector size (zero padded) for device writes.</summary>
        public int AlignedCount { get; set; }

        /// <summary>Absolute data offset of the first byte.</summary>
        public long Offset { get; set; }

        /// <summary>Progress units reached once this chunk is processed.</summary>
        public long Units { get; set; }
    }

    /// <summary>
    /// Bounded producer/consumer pipeline. One thread fills buffers; each stage runs on its own
    /// thread and processes chunks in order, so reading, hashing and writing overlap.
    /// The first failure cancels all threads and is rethrown with its original stack trace.
    /// </summary>
    public static class BufferPipeline
    {
        public static void Run(
            int bufferSize,
            int bufferCount,
            Func<Chunk, bool> fill,
            IReadOnlyList<Action<Chunk>> stages,
            CancellationToken cancellationToken)
        {
            if (stages == null || stages.Count == 0)
            {
                throw new ArgumentException("At least one stage is required.", nameof(stages));
            }

            using (var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                var token = linked.Token;
                var free = new BlockingCollection<Chunk>(bufferCount);
                for (int i = 0; i < bufferCount; i++)
                {
                    free.Add(new Chunk(bufferSize));
                }

                var queues = new BlockingCollection<Chunk>[stages.Count];
                for (int i = 0; i < queues.Length; i++)
                {
                    queues[i] = new BlockingCollection<Chunk>(bufferCount);
                }

                ExceptionDispatchInfo failure = null;
                var failureLock = new object();
                Action<Exception> fail = ex =>
                {
                    lock (failureLock)
                    {
                        if (failure == null && !(ex is OperationCanceledException && token.IsCancellationRequested))
                        {
                            failure = ExceptionDispatchInfo.Capture(ex);
                        }
                    }

                    linked.Cancel();
                };

                var tasks = new List<Task>();
                tasks.Add(Task.Factory.StartNew(
                    () =>
                    {
                        try
                        {
                            while (true)
                            {
                                var chunk = free.Take(token);
                                if (!fill(chunk))
                                {
                                    break;
                                }

                                queues[0].Add(chunk, token);
                            }
                        }
                        catch (Exception ex)
                        {
                            fail(ex);
                        }
                        finally
                        {
                            queues[0].CompleteAdding();
                        }
                    },
                    CancellationToken.None,
                    TaskCreationOptions.LongRunning,
                    TaskScheduler.Default));

                for (int s = 0; s < stages.Count; s++)
                {
                    int index = s;
                    tasks.Add(Task.Factory.StartNew(
                        () =>
                        {
                            try
                            {
                                foreach (var chunk in queues[index].GetConsumingEnumerable(token))
                                {
                                    stages[index](chunk);
                                    if (index + 1 < queues.Length)
                                    {
                                        queues[index + 1].Add(chunk, token);
                                    }
                                    else
                                    {
                                        free.Add(chunk, token);
                                    }
                                }
                            }
                            catch (Exception ex)
                            {
                                fail(ex);
                            }
                            finally
                            {
                                if (index + 1 < queues.Length)
                                {
                                    queues[index + 1].CompleteAdding();
                                }
                            }
                        },
                        CancellationToken.None,
                        TaskCreationOptions.LongRunning,
                        TaskScheduler.Default));
                }

                Task.WaitAll(tasks.ToArray());

                failure?.Throw();
                cancellationToken.ThrowIfCancellationRequested();
                if (token.IsCancellationRequested)
                {
                    throw new OperationCanceledException(token);
                }
            }
        }
    }
}
