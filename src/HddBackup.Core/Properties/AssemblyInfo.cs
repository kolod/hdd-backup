using System.Runtime.CompilerServices;

// Lets unit tests reach internal helpers (ImageReader, ImageWriter). Harmless when these
// sources are compiled into the executable.
[assembly: InternalsVisibleTo("HddBackup.Core.Tests")]
