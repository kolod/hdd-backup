# Contract: Core API for shrinking

```csharp
public static class ShrinkPlanner
{
    // Read-only analysis of an image file (raw or gzip). Never throws for "not shrinkable":
    // reasons go to ShrinkPlan.BlockingReasons. Throws ImagingException only for I/O errors.
    public static ShrinkPlan Analyze(string imagePath, CancellationToken cancel);

    // True if a shrunk image would fit a device/partition of the given size.
    public static bool Fits(ShrinkPlan plan, long targetBytes);
}

// New operation, run through IImagingEngine like the others:
//   OperationRequest { Kind = OperationKind.ShrinkImage, ImagePath = source,
//                      ShrinkOutputPath = output, ShrinkTargetBytes = null | size }
// Progress phases: Preparing → Transferring (working copy, prefix, rebuild) → Verifying
//   (checker + tree compare) → Flushing; 100% only after commit.
// Result: OperationResult with ImagePath = output, ContentSha256/FileSha256 of the output.
```

## Ext4 components (internal, tested directly)

```csharp
internal sealed class Ext4Reader : IDisposable
{
    public Ext4Reader(IBlockDevice partition);          // validates superblock + feature set
    public Ext4Superblock Superblock { get; }
    public IReadOnlyList<string> Validate();             // R-3 preconditions; empty = OK
    public Ext4Inode ReadInode(uint number);
    public IEnumerable<Ext4DirEntry> ReadDirectory(Ext4Inode dir);
    public IEnumerable<(long Logical, long Physical, int Count)> MapFile(Ext4Inode inode);
    public IReadOnlyList<Ext4Xattr> ReadXattrs(Ext4Inode inode);
    public UsageSummary MeasureUsage();                   // blocks/inodes really in use
}

internal sealed class Ext4Writer
{
    public Ext4Writer(IBlockDevice target, Ext4Layout layout, Ext4Superblock template);
    public void CopyFrom(Ext4Reader source, IProgress<long> bytesCopied, CancellationToken cancel);
    public void Finish();                                 // bitmaps, counts, checksums, journal, resize inode
}

internal static class Ext4Checker
{
    public static IReadOnlyList<string> Check(IBlockDevice partition);   // empty = consistent
}

internal static class Ext4TreeComparer
{
    public static IReadOnlyList<string> Compare(Ext4Reader a, Ext4Reader b, CancellationToken cancel);
}
```

## Guarantees

1. The source image file is opened read-only, always.
2. Bytes `[0, root partition start)` of the output equal the source, except the root partition
   entry (MBR: 16-byte entry; GPT: entry + header/array CRCs + backup header at the new end).
3. Success only when `Ext4Checker.Check` and `Ext4TreeComparer.Compare` both return empty lists.
4. On failure or cancel, no file without the `.partial` suffix is created; partial files are
   deleted.
