using System.Runtime.CompilerServices;

// EditMode tests force individual execution paths (reference, LUT, JIT) on built SimChips.
[assembly: InternalsVisibleTo("DLS.Tests.EditMode")]
